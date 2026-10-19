using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Steps;
using Vela.Testing.Security;

namespace Vela.Testing
{
    /// <summary>
    /// An in-memory simulation of the Dataverse plugin pipeline. Discover plugin registrations from an
    /// assembly (or register them explicitly), then drive writes through <see cref="Service"/>: each
    /// Create/Update/Delete/Associate (or Custom API <c>Execute</c>) fires the registered steps for the
    /// matching message/stage/entity, honouring filtering attributes, execution order, pre/post images and
    /// cascade (a plugin that writes triggers further plugins). Synchronous failures roll the operation
    /// back; asynchronous steps run once the outermost operation commits, and never when it rolls back. This is
    /// scenario/behavioural testing — fully offline, not integration testing.
    /// </summary>
    public sealed partial class PluginPipeline
    {
        private readonly List<PipelineRegistration> _registrations = new List<PipelineRegistration>();

        private readonly Dictionary<string, Type> _customApis = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        private readonly InMemoryOrganizationService _store = new InMemoryOrganizationService();

        private readonly List<PipelineExecutionEntry> _log = new List<PipelineExecutionEntry>();

        private readonly List<Exception> _asyncStepFailures = new List<Exception>();

        private readonly RecordingTracingService _trace = new RecordingTracingService();

        private readonly RecordingLogger _logger = new RecordingLogger();

        private readonly PipelineOrganizationService _service;

        private int _maxDepth = Vela.Plugin.PluginBase.DefaultMaxDepth;

        private int _depth;

        // One correlation id per top-level operation, shared by everything it cascades into, including the async
        // steps it queues (each restores the id captured when it was queued). Verified live: an ExecuteTransaction
        // batch shares one id, each child of an ExecuteMultiple gets its own.
        private Guid _correlationId = Guid.NewGuid();

        // One request id per call into the pipeline from outside (an ExecuteMultiple batch is one request), carried by
        // every step it triggers, async ones included. Verified live: a sync step's OperationId is the request id, an
        // async step's is its own system job id.
        private Guid _requestId = Guid.NewGuid();

        // The system job id of the asynchronous step running now (its OperationId).
        private Guid _asyncOperationId;

        // The execution contexts currently on the (synchronous) call stack. The context of the plugin that is
        // executing when a cascaded write re-enters the pipeline becomes the nested execution's ParentContext,
        // mirroring how Dataverse exposes IPluginExecutionContext.ParentContext (and its SharedVariables) to a
        // plugin fired by another plugin's write.
        private readonly Stack<FakePluginExecutionContext> _contextStack = new Stack<FakePluginExecutionContext>();

        // Shared variables for the operation currently running. Seeded from the request 'tag' (or other
        // shared variables) at the top level, shared across every stage/plugin of the operation, and inherited
        // by cascaded (nested) writes — mirroring how Dataverse surfaces them (and ParentContext.SharedVariables).
        private ParameterCollection _sharedVariables = new ParameterCollection();

        // Bypass directives for the operation currently running, read from the request (BypassCustomPluginExecution
        // / BypassBusinessLogicExecution / BypassBusinessLogicExecutionStepIds). Empty = nothing bypassed.
        private BypassContext _bypass = BypassContext.None;

        // Secure configuration values by name (WithSecureConfig). A step only carries its SecureConfigName; the
        // value is supplied at deployment time (vela apply --secure-config NAME=@file or VELA_SECURE_CONFIG_NAME), so the test supplies it here.
        private readonly Dictionary<string, string> _secureConfigs = new Dictionary<string, string>(StringComparer.Ordinal);

        // The services the test replaced with WithService, handed to every plugin the pipeline runs.
        private readonly ServiceOverrides _serviceOverrides = new ServiceOverrides(PlatformServices);

        // The plug-in cache of this pipeline, kept across its executions like the worker process's, and expiring on
        // the store's clock (WithClock) so a test moves time forward instead of sleeping.
        private Vela.Caching.PluginCacheService? _cache;

        private Vela.Caching.PluginCacheService Cache => _cache ??= Vela.Caching.PluginCacheService.OnClock(_store.Now);

        // The platform services WithService rejects, with how the pipeline supplies or replaces each one.
        private static readonly IReadOnlyList<KeyValuePair<Type, string>> PlatformServices = new[]
        {
            new KeyValuePair<Type, string>(typeof(IManagedIdentityService), "Use WithManagedIdentityService."),
            new KeyValuePair<Type, string>(typeof(ITracingService), "The pipeline records every trace line in Trace."),
            new KeyValuePair<Type, string>(typeof(Microsoft.Xrm.Sdk.PluginTelemetry.ILogger), "The pipeline records every log entry in LogCapture."),
            new KeyValuePair<Type, string>(typeof(IOrganizationService), "Plugins call the pipeline's in-memory store; seed it with Seed and configure it with the pipeline's With... methods."),
            new KeyValuePair<Type, string>(typeof(IOrganizationServiceFactory), "Plugins call the pipeline's in-memory store; seed it with Seed and configure it with the pipeline's With... methods."),
            new KeyValuePair<Type, string>(typeof(IExecutionContext), "The pipeline builds each step's execution context from the request it runs."),
        };

        // The managed-identity service handed to every plugin the pipeline runs (WithManagedIdentityService); null
        // leaves it out of the provider, as on an environment without one.
        private IManagedIdentityService? _managedIdentityService;

        // The identity of the service issuing the current call (null = the test-facing service: the configured
        // current user) and the user who initiated the outermost request (null until a plugin-issued call runs).
        private Guid? _callerUserId;

        private Guid? _initiatingUserId;

        /// <summary>
        /// The user id a plugin execution context carries when it was triggered by a write issued through the SYSTEM
        /// service (<c>ctx.SystemService</c>), standing in for the org's SYSTEM user. A service created for this id
        /// (e.g. <c>ctx.Service</c> in that nested plugin) is the unrestricted SYSTEM service, as on a real org.
        /// Internal: tests reason about the behaviour (SYSTEM is unrestricted), not about this id.
        /// </summary>
        internal static readonly Guid SystemUserId = new Guid("5e5e5e00-0000-0000-0000-000000000000");

        private PluginPipeline()
        {
            _service = new PipelineOrganizationService(this);
        }

        /// <summary>Creates an empty pipeline; register plugins with <see cref="Register{TPlugin}"/>.</summary>
        public static PluginPipeline Empty()
        {
            return new PluginPipeline();
        }

        /// <summary>Discovers every Vela plugin and Custom API in the assembly and registers their steps.</summary>
        public static PluginPipeline FromAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            return FromManifest(assembly, FrameworkManifestScanner.Scan(assembly));
        }

        // Registers a scanned manifest's steps and Custom APIs, resolving each type in the assembly.
        internal static PluginPipeline FromManifest(Assembly assembly, FrameworkManifest manifest)
        {
            PluginPipeline pipeline = new PluginPipeline();

            // A step or Custom API whose type cannot be resolved would never fire, and every test would stay green
            // without it: fail the setup instead, naming each one.
            List<string> unresolved = new List<string>();
            foreach (StepDefinition step in manifest.PluginSteps)
            {
                Type? type = ResolveType(assembly, step.PluginTypeName);
                if (type != null)
                {
                    pipeline._registrations.Add(new PipelineRegistration(type, step));
                    pipeline.SeedStepRow(step);
                }
                else
                {
                    unresolved.Add($"step {step.Message}/{step.Stage} of '{step.PluginTypeName}'");
                }
            }

            foreach (CustomApiDefinition api in manifest.CustomApis)
            {
                Type? type = ResolveType(assembly, api.PluginTypeName);
                if (type != null && !string.IsNullOrEmpty(api.UniqueName))
                {
                    pipeline._customApis[api.UniqueName] = type;
                }
                else
                {
                    unresolved.Add($"Custom API '{api.UniqueName}' of '{api.PluginTypeName}'");
                }
            }

            if (unresolved.Count > 0)
            {
                throw new InvalidOperationException(
                    $"PluginPipeline.FromAssembly could not register {unresolved.Count} definition(s) from '{assembly.GetName().Name}', " +
                    $"so they would never fire: {string.Join("; ", unresolved)}.");
            }

            return pipeline;
        }

        /// <summary>Discovers plugins from the assembly that contains <typeparamref name="T"/>.</summary>
        public static PluginPipeline FromAssemblyOf<T>()
        {
            return FromAssembly(typeof(T).Assembly);
        }

        /// <summary>Registers a single plugin (or Custom API) type explicitly.</summary>
        public PluginPipeline Register<TPlugin>()
            where TPlugin : IPlugin
        {
            return Register(typeof(TPlugin));
        }

        /// <summary>Registers a single plugin (or Custom API) type explicitly.</summary>
        public PluginPipeline Register(Type pluginType)
        {
            if (pluginType == null)
            {
                throw new ArgumentNullException(nameof(pluginType));
            }

            ConstructorInfo constructor = PluginActivator.RequireConfigurationConstructor(pluginType, "to be registered");
            object instance = PluginActivator.Create(constructor, null, null);

            bool registered = false;
            MethodInfo? method = pluginType.GetMethod("GetStepDefinitions");
            if (method != null && method.Invoke(instance, null) is StepDefinition[] steps)
            {
                foreach (StepDefinition step in steps)
                {
                    _registrations.Add(new PipelineRegistration(pluginType, step));
                    SeedStepRow(step);
                    registered = true;
                }
            }

            if (instance is CustomApiBase customApi)
            {
                CustomApiDefinition? definition = customApi.GetCustomApiDefinition();
                if (definition != null && !string.IsNullOrEmpty(definition.UniqueName))
                {
                    _customApis[definition.UniqueName] = pluginType;
                    registered = true;
                }
            }

            // A type that yields nothing to register would never fire, and the test would pass without it.
            if (!registered)
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginType.FullName}' declares no step and no Custom API, so registering it would add nothing " +
                    $"that can fire. Declare its steps in RegisterPluginSteps() (or its Custom API definition).");
            }

            return this;
        }

        /// <summary>Pre-populates the store (no plugins fire).</summary>
        public PluginPipeline Seed(params Entity[] entities)
        {
            _store.Seed(entities);
            return this;
        }

        /// <summary>
        /// Supplies the value of a named secure configuration. A step declared with <c>.WithSecureConfigFrom(name)</c>
        /// receives this value as the <c>secureConfig</c> constructor argument, exactly as <c>vela apply
        /// --secure-config NAME=@file</c> (or <c>VELA_SECURE_CONFIG_NAME</c>) provides it in production. A step whose name has no value receives null.
        /// </summary>
        public PluginPipeline WithSecureConfig(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A secure configuration name is required.", nameof(name));
            }

            _secureConfigs[name] = value;
            return this;
        }

        /// <summary>
        /// Sets the cascade-depth cap that aborts runaway recursion. The default is <see cref="Vela.Plugin.PluginBase.DefaultMaxDepth"/>
        /// (8), the Dataverse default: an operation that would run deeper throws. Unlike Dataverse, the pipeline does not
        /// track the one-hour window, only the depth of the current cascade.
        /// </summary>
        public PluginPipeline WithMaxDepth(int maxDepth)
        {
            _maxDepth = maxDepth < 1 ? 1 : maxDepth;
            return this;
        }

        /// <summary>The pipeline-aware service: drive the scenario through it, and plugins receive it for cascade.</summary>
        public IOrganizationService Service => _service;

        /// <summary>
        /// Ordered log of every step the platform invoked (for asserting what was invoked and in what order). An entry
        /// does not mean the handler ran: Vela's runtime can skip it (a <c>.ForRelationship(...)</c> scope,
        /// <c>.SkipWhenDepthAbove(n)</c>, the depth guard) or the execution can fail before it, which
        /// <see cref="PipelineExecutionEntry.HandlerInvoked"/> and <see cref="PipelineExecutionEntry.SkipReason"/> report.
        /// </summary>
        public IReadOnlyList<PipelineExecutionEntry> ExecutionLog => _log;

        /// <summary>
        /// Exceptions thrown by asynchronous steps. Real Dataverse runs async steps as decoupled system jobs whose
        /// failures never surface to the triggering caller, so the pipeline records them here instead of throwing
        /// out of the originating operation. Assert on this to verify an async step failed.
        /// </summary>
        public IReadOnlyList<Exception> AsyncStepFailures => _asyncStepFailures;

        /// <summary>Captured traces aggregated across the whole cascade.</summary>
        public RecordingTracingService Trace => _trace;

        /// <summary>
        /// Captured Application Insights entries and metrics (<c>ctx.LogInformation</c>, <c>ctx.LogMetric</c>, ...)
        /// aggregated across the whole cascade.
        /// </summary>
        public RecordingLogger LogCapture => _logger;

        /// <summary>Snapshot (clones) of all stored records of a logical name, for assertions.</summary>
        public IReadOnlyList<Entity> GetAll(string logicalName) => _store.GetAll(logicalName);

        internal InMemoryOrganizationService Store => _store;

        private static Type? ResolveType(Assembly assembly, string fullName)
        {
            return string.IsNullOrEmpty(fullName) ? null : assembly.GetType(fullName);
        }
    }
}
