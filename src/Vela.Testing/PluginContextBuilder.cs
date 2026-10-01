using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Constants;
using Vela.Context;
using Vela.Steps;

namespace Vela.Testing
{
    /// <summary>
    /// Fluent, strongly-typed builder for a fake plugin execution context. Mirrors the framework's
    /// registration DSL vocabulary (<c>OnUpdate().Stage(...).Mode(...)</c>) so tests read like registrations.
    /// Terminal methods produce an <see cref="IServiceProvider"/>, a <see cref="LocalPluginContext"/>, or
    /// execute a plugin directly.
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type of the primary entity (Target/images).</typeparam>
    public class PluginContextBuilder<TEntity>
        where TEntity : Entity
    {
        private readonly RecordingTracingService _recorder = new RecordingTracingService();
        private readonly RecordingLogger _loggerRecorder = new RecordingLogger();
        private ITracingService _tracingService;
        private Microsoft.Extensions.Logging.ILogger _logger;
        private IManagedIdentityService? _managedIdentityService;
        private bool _triggerConfigured;

        /// <summary>Initializes the builder, deriving the primary entity name from <typeparamref name="TEntity"/> when possible.</summary>
        public PluginContextBuilder()
        {
            _tracingService = _recorder;
            _logger = _loggerRecorder;
            Context = new FakePluginExecutionContext();
            Factory = new HookOrganizationServiceFactory();

            string? logicalName = ResolveLogicalName();
            if (!string.IsNullOrEmpty(logicalName))
            {
                Context.PrimaryEntityName = logicalName!;
            }
        }

        /// <summary>The mutable fake execution context being built (collections can be populated directly).</summary>
        public FakePluginExecutionContext Context { get; }

        /// <summary>The fake organization-service factory; its user/system services are replaceable.</summary>
        public HookOrganizationServiceFactory Factory { get; }

        /// <summary>The default recording tracer. Captures traces unless <see cref="WithTracingService"/> overrides it.</summary>
        public RecordingTracingService TraceCapture => _recorder;

        /// <summary>The default recording logger. Captures <c>ctx.LogInformation</c>/<c>LogError</c>/... entries unless <see cref="WithLogger"/> overrides it.</summary>
        public RecordingLogger LogCapture => _loggerRecorder;

        /// <summary>Sets the message to <c>Create</c>.</summary>
        public PluginContextBuilder<TEntity> OnCreate() => OnMessage(PluginMessages.Create);

        /// <summary>Sets the message to <c>Update</c>.</summary>
        public PluginContextBuilder<TEntity> OnUpdate() => OnMessage(PluginMessages.Update);

        /// <summary>Sets the message to <c>Delete</c>.</summary>
        public PluginContextBuilder<TEntity> OnDelete() => OnMessage(PluginMessages.Delete);

        /// <summary>Sets an arbitrary message name.</summary>
        public PluginContextBuilder<TEntity> OnMessage(string messageName)
        {
            Context.MessageName = messageName;
            _triggerConfigured = true;
            return this;
        }

        /// <summary>Sets the pipeline stage.</summary>
        public PluginContextBuilder<TEntity> Stage(PluginStages stage)
        {
            Context.Stage = (int)stage;
            _triggerConfigured = true;
            return this;
        }

        /// <summary>Sets the execution mode.</summary>
        public PluginContextBuilder<TEntity> Mode(ExecutionMode mode)
        {
            Context.Mode = (int)mode;
            _triggerConfigured = true;
            return this;
        }

        /// <summary>Sets the execution mode to synchronous (shortcut for <c>Mode(ExecutionMode.Synchronous)</c>).</summary>
        public PluginContextBuilder<TEntity> Sync() => Mode(ExecutionMode.Synchronous);

        /// <summary>Sets the execution mode to asynchronous (shortcut for <c>Mode(ExecutionMode.Asynchronous)</c>).</summary>
        public PluginContextBuilder<TEntity> Async() => Mode(ExecutionMode.Asynchronous);

        /// <summary>Sets the execution depth.</summary>
        public PluginContextBuilder<TEntity> Depth(int depth)
        {
            Context.Depth = depth;
            return this;
        }

        /// <summary>Overrides the primary entity name (and optionally its id).</summary>
        public PluginContextBuilder<TEntity> WithPrimaryEntity(string logicalName, Guid? id = null)
        {
            Context.PrimaryEntityName = logicalName;
            if (id.HasValue)
            {
                Context.PrimaryEntityId = id.Value;
            }

            return this;
        }

        /// <summary>Sets the step-runner user id.</summary>
        public PluginContextBuilder<TEntity> WithUserId(Guid id)
        {
            Context.UserId = id;
            return this;
        }

        /// <summary>Sets the initiating user id.</summary>
        public PluginContextBuilder<TEntity> WithInitiatingUserId(Guid id)
        {
            Context.InitiatingUserId = id;
            return this;
        }

        /// <summary>Sets the correlation id.</summary>
        public PluginContextBuilder<TEntity> WithCorrelationId(Guid id)
        {
            Context.CorrelationId = id;
            return this;
        }

        /// <summary>Sets the organization id.</summary>
        public PluginContextBuilder<TEntity> WithOrganizationId(Guid id)
        {
            Context.OrganizationId = id;
            return this;
        }

        /// <summary>Sets the <c>Target</c> input parameter.</summary>
        public PluginContextBuilder<TEntity> WithTarget(TEntity target)
        {
            Context.InputParameters["Target"] = target;
            return this;
        }

        /// <summary>Adds a pre-image under the given alias (default <see cref="PluginImageAliases.PreImage"/>).</summary>
        public PluginContextBuilder<TEntity> WithPreImage(TEntity image, string alias = PluginImageAliases.PreImage)
        {
            Context.PreEntityImages[alias] = image;
            return this;
        }

        /// <summary>Adds a post-image under the given alias (default <see cref="PluginImageAliases.PostImage"/>).</summary>
        public PluginContextBuilder<TEntity> WithPostImage(TEntity image, string alias = PluginImageAliases.PostImage)
        {
            Context.PostEntityImages[alias] = image;
            return this;
        }

        /// <summary>Sets the bulk <c>Targets</c> input parameter (CreateMultiple/UpdateMultiple).</summary>
        public PluginContextBuilder<TEntity> WithTargets(EntityCollection targets)
        {
            Context.InputParameters["Targets"] = targets;
            return this;
        }

        /// <summary>Sets the bulk <c>Targets</c> from typed entities (entity name taken from the primary entity).</summary>
        public PluginContextBuilder<TEntity> WithTargets(params TEntity[] targets)
        {
            EntityCollection collection = new EntityCollection { EntityName = Context.PrimaryEntityName };
            foreach (TEntity target in targets)
            {
                collection.Entities.Add(target);
            }

            return WithTargets(collection);
        }

        /// <summary>Sets the bulk pre-image collections, aligned by target index.</summary>
        public PluginContextBuilder<TEntity> WithBulkPreImages(params EntityImageCollection[] images)
        {
            Context.PreEntityImagesCollection = images;
            return this;
        }

        /// <summary>Sets the bulk post-image collections, aligned by target index.</summary>
        public PluginContextBuilder<TEntity> WithBulkPostImages(params EntityImageCollection[] images)
        {
            Context.PostEntityImagesCollection = images;
            return this;
        }

        /// <summary>Sets the parent execution context (the step that triggered this one).</summary>
        public PluginContextBuilder<TEntity> WithParentContext(IPluginExecutionContext parent)
        {
            Context.ParentContext = parent;
            return this;
        }

        /// <summary>Adds an input parameter.</summary>
        public PluginContextBuilder<TEntity> WithInputParameter(string key, object value)
        {
            Context.InputParameters[key] = value;
            return this;
        }

        /// <summary>Adds an output parameter.</summary>
        public PluginContextBuilder<TEntity> WithOutputParameter(string key, object value)
        {
            Context.OutputParameters[key] = value;
            return this;
        }

        /// <summary>Adds a shared variable.</summary>
        public PluginContextBuilder<TEntity> WithSharedVariable(string key, object value)
        {
            Context.SharedVariables[key] = value;
            return this;
        }

        /// <summary>Sets the <c>tag</c> shared variable a client application would pass on the request.</summary>
        public PluginContextBuilder<TEntity> WithTag(string tag)
        {
            return WithSharedVariable("tag", tag);
        }

        /// <summary>Uses the given organization service for both user and system contexts.</summary>
        public PluginContextBuilder<TEntity> WithOrganizationService(IOrganizationService service)
        {
            Factory.UserService = service;
            Factory.SystemService = service;
            return this;
        }

        /// <summary>Uses the given organization service for the user (initiating/calling) contexts.</summary>
        public PluginContextBuilder<TEntity> WithUserService(IOrganizationService service)
        {
            Factory.UserService = service;
            return this;
        }

        /// <summary>Uses the given organization service for the system context.</summary>
        public PluginContextBuilder<TEntity> WithSystemService(IOrganizationService service)
        {
            Factory.SystemService = service;
            return this;
        }

        /// <summary>Registers a managed-identity service so <c>AcquireManagedIdentityToken</c> works.</summary>
        public PluginContextBuilder<TEntity> WithManagedIdentityService(IManagedIdentityService service)
        {
            _managedIdentityService = service;
            return this;
        }

        /// <summary>Overrides the tracing service (disables the default <see cref="TraceCapture"/> recorder).</summary>
        public PluginContextBuilder<TEntity> WithTracingService(ITracingService service)
        {
            _tracingService = service;
            return this;
        }

        /// <summary>Overrides the logger (disables the default <see cref="LogCapture"/> recorder).</summary>
        public PluginContextBuilder<TEntity> WithLogger(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger;
            return this;
        }

        /// <summary>Sets the environment id (a string on the platform context).</summary>
        public PluginContextBuilder<TEntity> WithEnvironmentId(string environmentId)
        {
            Context.EnvironmentId = environmentId;
            return this;
        }

        /// <summary>Sets the tenant id.</summary>
        public PluginContextBuilder<TEntity> WithTenantId(Guid id)
        {
            Context.TenantId = id;
            return this;
        }

        /// <summary>Marks the caller as an application (S2S) user.</summary>
        public PluginContextBuilder<TEntity> AsApplicationUser(bool value = true)
        {
            Context.IsApplicationUser = value;
            return this;
        }

        /// <summary>Builds the fake <see cref="IServiceProvider"/> a plugin's <c>Execute</c> consumes.</summary>
        public IServiceProvider BuildServiceProvider()
        {
            Dictionary<Type, object> services = new Dictionary<Type, object>
            {
                [typeof(IPluginExecutionContext)] = Context,
                [typeof(IOrganizationServiceFactory)] = Factory,
                [typeof(ITracingService)] = _tracingService,
                [typeof(Microsoft.Extensions.Logging.ILogger)] = _logger
            };

            if (_managedIdentityService != null)
            {
                services[typeof(IManagedIdentityService)] = _managedIdentityService;
            }

            return new DictionaryServiceProvider(services);
        }

        /// <summary>Builds a <see cref="LocalPluginContext"/> around the fake provider for direct introspection.</summary>
        public LocalPluginContext Build()
        {
            return new LocalPluginContext(BuildServiceProvider());
        }

        /// <summary>Executes the supplied plugin against the fake provider.</summary>
        public void Execute(IPlugin plugin)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            TryAutoConfigureTrigger(plugin);
            plugin.Execute(BuildServiceProvider());
        }

        /// <summary>
        /// Instantiates and executes a plugin, returning the instance for assertions. Supports both a
        /// parameterless constructor and the Vela <c>(string unsecureConfig, string secureConfig)</c> primary
        /// constructor that generated plugins use, so <c>Execute&lt;MyPlugin&gt;()</c> works on scaffolded code.
        /// </summary>
        public TPlugin Execute<TPlugin>()
            where TPlugin : IPlugin
        {
            TPlugin plugin = CreatePluginInstance<TPlugin>();
            TryAutoConfigureTrigger(plugin);
            plugin.Execute(BuildServiceProvider());
            return plugin;
        }

        // A Vela plugin/Custom API has no parameterless constructor (its primary constructor takes the unsecure
        // and secure configuration); fall back to that two-string constructor with empty configuration when there
        // is no parameterless one.
        private static TPlugin CreatePluginInstance<TPlugin>()
            where TPlugin : IPlugin
        {
            Type type = typeof(TPlugin);

            if (type.GetConstructor(Type.EmptyTypes) is not null)
            {
                return (TPlugin)Activator.CreateInstance(type)!;
            }

            ConstructorInfo? configCtor = type.GetConstructor(new[] { typeof(string), typeof(string) });
            if (configCtor is not null)
            {
                return (TPlugin)configCtor.Invoke(new object?[] { string.Empty, string.Empty });
            }

            throw new InvalidOperationException(
                $"Cannot instantiate plugin '{type.Name}': it needs either a parameterless constructor or a " +
                "(string unsecureConfig, string secureConfig) constructor. Pass an instance to Execute(plugin) instead.");
        }

        // When the caller did not set a trigger (message/stage/mode), derive it from the plugin's own single
        // registered step so .Execute(plugin) "just works" without restating the registration. Multi-step plugins
        // still require an explicit trigger to select the handler under test.
        private void TryAutoConfigureTrigger(IPlugin plugin)
        {
            if (_triggerConfigured)
            {
                return;
            }

            try
            {
                MethodInfo? method = plugin.GetType().GetMethod("GetStepDefinitions", Type.EmptyTypes);
                if (method == null || method.Invoke(plugin, null) is not StepDefinition[] definitions || definitions.Length != 1)
                {
                    return;
                }

                StepDefinition step = definitions[0];
                if (!string.IsNullOrWhiteSpace(step.Message))
                {
                    Context.MessageName = step.Message;
                }

                Context.Stage = (int)step.Stage;
                Context.Mode = (int)step.Mode;

                // A Create/CreateMultiple Post step sees the created id(s) as OutputParameters (as Dataverse
                // surfaces them), so a post-create handler's ctx.Response.id / ctx.Response.Ids resolves from the
                // auto-derived single-step trigger without the caller restating the output.
                if (step.Stage == PluginStages.PostOperation)
                {
                    if (string.Equals(Context.MessageName, PluginMessages.Create, StringComparison.OrdinalIgnoreCase)
                        && !Context.OutputParameters.Contains("id")
                        && Context.InputParameters.Contains("Target")
                        && Context.InputParameters["Target"] is Entity createTarget)
                    {
                        Context.OutputParameters["id"] = createTarget.Id;
                    }
                    else if (string.Equals(Context.MessageName, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase)
                        && !Context.OutputParameters.Contains("Ids")
                        && Context.InputParameters.Contains("Targets")
                        && Context.InputParameters["Targets"] is EntityCollection createTargets)
                    {
                        Guid[] ids = new Guid[createTargets.Entities.Count];
                        for (int index = 0; index < ids.Length; index++)
                        {
                            ids[index] = createTargets.Entities[index].Id;
                        }

                        Context.OutputParameters["Ids"] = ids;
                    }
                }
            }
            catch
            {
                // Best-effort: if the registration can't be read, keep the configured defaults and let the
                // plugin's own Execute surface any real error.
            }
        }

        private static string? ResolveLogicalName()
        {
            EntityLogicalNameAttribute? attribute =
                (EntityLogicalNameAttribute?)Attribute.GetCustomAttribute(typeof(TEntity), typeof(EntityLogicalNameAttribute));
            return attribute?.LogicalName;
        }
    }

    /// <summary>
    /// Non-generic builder entry point for Custom APIs and message-only plugins (no specific entity type).
    /// Inherits the full fluent surface; <see cref="PluginContextBuilder{TEntity}.WithTarget"/> accepts any <see cref="Entity"/>.
    /// </summary>
    public sealed class PluginContextBuilder : PluginContextBuilder<Entity>
    {
    }
}
