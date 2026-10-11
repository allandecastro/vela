using System;
using System.Collections.Generic;
using Vela.Constants;
using Vela.DI;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace Vela.Context
{
    /// <summary>
    /// Runtime wrapper around IServiceProvider for Dataverse plugins.
    /// </summary>
    public sealed class LocalPluginContext : ILocalPluginContext, IServiceResolverContext
    {
        private readonly ITracingService _safeTracingService;
        private readonly string? _pluginTypeName;
        private string? _currentHandler;
        private int _scopeDepth;
        private readonly string? _environmentId;
        private readonly Guid? _tenantId;
        private readonly string _initiatingUserAgent;
        private readonly Guid? _portalsContactId;
        private readonly bool _isPortalsClientCall;
        private readonly bool _isApplicationUser;
        private readonly EntityCollection? _bulkTargets;
        private readonly IReadOnlyList<EntityImageCollection> _bulkPreEntityImagesCollection;
        private readonly IReadOnlyList<EntityImageCollection> _bulkPostEntityImagesCollection;
        private IOrganizationService? _service;
        private IOrganizationService? _systemService;
        private IServiceEndpointNotificationService? _notificationService;

        // Dataverse serves successive context interface versions (IPluginExecutionContext2..7), each adding members.
        // The richest one is requested first. The SDK the plugin compiles against defines them all, and the platform
        // always loads a newer SDK, so they are referenced directly.
        private static readonly Type[] PreferredContextTypes =
        {
            typeof(IPluginExecutionContext7),
            typeof(IPluginExecutionContext6),
            typeof(IPluginExecutionContext5),
            typeof(IPluginExecutionContext4),
            typeof(IPluginExecutionContext3),
            typeof(IPluginExecutionContext2),
            typeof(IPluginExecutionContext),
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalPluginContext"/> class.
        /// </summary>
        /// <param name="serviceProvider">Runtime service provider.</param>
        /// <param name="pluginTypeName">The plugin's type name, for the trace lines; null when unknown.</param>
        public LocalPluginContext(IServiceProvider serviceProvider, string? pluginTypeName = null)
        {
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _pluginTypeName = pluginTypeName;
            PluginExecutionContext = ResolveExecutionContext(serviceProvider);
            TracingService = serviceProvider.GetService(typeof(ITracingService)) as ITracingService ?? new NullTracingService();
            _safeTracingService = TracingService;
            OrganizationServiceFactory = serviceProvider.GetService(typeof(IOrganizationServiceFactory)) as IOrganizationServiceFactory
                ?? throw new InvalidPluginExecutionException("IOrganizationServiceFactory is unavailable.");
            // The platform serves its Application Insights logger as Microsoft.Xrm.Sdk.PluginTelemetry.ILogger (never as
            // a Microsoft.Extensions.Logging.ILogger). It is a no-op when the environment exports no telemetry.
            Logger = serviceProvider.GetService(typeof(ILogger)) as ILogger ?? NullPluginTelemetryLogger.Instance;
            ManagedIdentityService = serviceProvider.GetService(typeof(IManagedIdentityService)) as IManagedIdentityService;
            // The newer members are read through their SDK interface, never by name on the concrete type: the
            // platform's context class is opaque and may implement them explicitly, and the compiler then pins each
            // member's type (EnvironmentId is a string, TenantId a Guid).
            IPluginExecutionContext context = PluginExecutionContext;
            _environmentId = NullIfEmpty((context as IPluginExecutionContext6)?.EnvironmentId);
            _tenantId = NullIfEmpty((context as IPluginExecutionContext6)?.TenantId);
            _initiatingUserAgent = (context as IPluginExecutionContext5)?.InitiatingUserAgent ?? string.Empty;
            _isPortalsClientCall = (context as IPluginExecutionContext2)?.IsPortalsClientCall == true;
            _portalsContactId = NullIfEmpty((context as IPluginExecutionContext2)?.PortalsContactId);
            _isApplicationUser = (context as IPluginExecutionContext7)?.IsApplicationUser == true;
            _bulkTargets = ResolveBulkTargets(context);
            _bulkPreEntityImagesCollection = (IReadOnlyList<EntityImageCollection>?)(context as IPluginExecutionContext4)?.PreEntityImagesCollection
                ?? Array.Empty<EntityImageCollection>();
            _bulkPostEntityImagesCollection = (IReadOnlyList<EntityImageCollection>?)(context as IPluginExecutionContext4)?.PostEntityImagesCollection
                ?? Array.Empty<EntityImageCollection>();
            Resolver = SimpleServiceResolver.CreateDefault(this);
        }

        /// <summary>
        /// Gets per-execution service resolver.
        /// </summary>
        public SimpleServiceResolver Resolver { get; }

        /// <inheritdoc />
        IPluginServiceResolver IServiceResolverContext.Resolver => Resolver;

        /// <inheritdoc />
        public IServiceProvider ServiceProvider { get; }

        /// <inheritdoc />
        public IPluginExecutionContext PluginExecutionContext { get; }

        /// <inheritdoc />
        public ITracingService TracingService { get; }

        /// <inheritdoc />
        public IServiceEndpointNotificationService NotificationService =>
            _notificationService ??= ServiceProvider.GetService(typeof(IServiceEndpointNotificationService)) as IServiceEndpointNotificationService
                ?? new NullNotificationService();

        /// <inheritdoc />
        public IOrganizationServiceFactory OrganizationServiceFactory { get; }

        /// <inheritdoc />
        /// <remarks>Created on first use: an execution that never calls Dataverse creates no service.</remarks>
        public IOrganizationService Service =>
            _service ??= new TracingOrganizationService(OrganizationServiceFactory.CreateOrganizationService(PluginExecutionContext.UserId), this, "User", TraceServiceCalls);

        /// <inheritdoc />
        /// <remarks>Created on first use.</remarks>
        public IOrganizationService SystemService =>
            _systemService ??= new TracingOrganizationService(OrganizationServiceFactory.CreateOrganizationService(null), this, "System", TraceServiceCalls);

        /// <summary>
        /// Whether the wrapped organization services trace every call, or only a failed one (set by
        /// <c>PluginBase.AutoTraceServiceCalls</c>). Read when a service is first created.
        /// </summary>
        internal bool TraceServiceCalls { get; set; } = true;

        /// <inheritdoc />
        public ILogger Logger { get; }

        /// <inheritdoc />
        public IManagedIdentityService? ManagedIdentityService { get; }

        /// <inheritdoc />
        public string MessageName => PluginExecutionContext.MessageName;

        /// <inheritdoc />
        public int Stage => PluginExecutionContext.Stage;

        /// <inheritdoc />
        public int Mode => PluginExecutionContext.Mode;

        /// <inheritdoc />
        public int Depth => PluginExecutionContext.Depth;

        /// <inheritdoc />
        public string PrimaryEntityName => PluginExecutionContext.PrimaryEntityName;

        /// <inheritdoc />
        public Guid PrimaryEntityId => PluginExecutionContext.PrimaryEntityId;

        /// <inheritdoc />
        public Guid UserId => PluginExecutionContext.UserId;

        /// <inheritdoc />
        public Guid InitiatingUserId => PluginExecutionContext.InitiatingUserId;

        /// <inheritdoc />
        public Guid CorrelationId => PluginExecutionContext.CorrelationId;

        /// <inheritdoc />
        public Guid OrganizationId => PluginExecutionContext.OrganizationId;

        /// <inheritdoc />
        public bool IsExecutingOffline => PluginExecutionContext.IsExecutingOffline;

        /// <inheritdoc />
        public bool IsInTransaction => PluginExecutionContext.IsInTransaction;

        /// <inheritdoc />
        public bool IsApplicationUser => _isApplicationUser;

        /// <inheritdoc />
        public string? EnvironmentId => _environmentId;

        /// <inheritdoc />
        public Guid? TenantId => _tenantId;

        /// <inheritdoc />
        public string InitiatingUserAgent => _initiatingUserAgent;

        /// <inheritdoc />
        public bool IsPortalsClientCall => _isPortalsClientCall;

        /// <inheritdoc />
        public Guid? PortalsContactId => _portalsContactId;

        /// <inheritdoc />
        public EntityCollection? BulkTargets => _bulkTargets;

        /// <inheritdoc />
        public IReadOnlyList<EntityImageCollection> BulkPreEntityImagesCollection => _bulkPreEntityImagesCollection;

        /// <inheritdoc />
        public IReadOnlyList<EntityImageCollection> BulkPostEntityImagesCollection => _bulkPostEntityImagesCollection;

        /// <inheritdoc />
        public bool IsBulkOperation => _bulkTargets != null;

        /// <inheritdoc />
        public string? Tag => GetSharedVariable("tag") as string;

        /// <inheritdoc />
        public object? GetSharedVariable(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            // Walk parent contexts too: a shared variable set upstream in a cascaded pipeline is exposed via
            // ParentContext, exactly as it is online.
            IPluginExecutionContext? context = PluginExecutionContext;
            while (context != null)
            {
                if (context.SharedVariables != null && context.SharedVariables.TryGetValue(key, out object? value))
                {
                    return value;
                }

                context = context.ParentContext;
            }

            return null;
        }

        /// <inheritdoc />
        public bool TryGetSharedVariable<T>(string key, out T value)
        {
            if (GetSharedVariable(key) is T typed)
            {
                value = typed;
                return true;
            }

            value = default!;
            return false;
        }

        /// <inheritdoc />
        public IOrganizationService AsUser(Guid userId)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentException("User id cannot be empty.", nameof(userId));
            }

            return new TracingOrganizationService(OrganizationServiceFactory.CreateOrganizationService(userId), this, "AsUser", TraceServiceCalls);
        }

        /// <inheritdoc />
        public void Trace(string format, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return;
            }

            Log(LogLevel.Information, null, format, args);
        }

        /// <inheritdoc />
        public void Log(LogLevel level, Exception? exception, string message, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string formatted;
            if (args == null || args.Length == 0)
            {
                formatted = message;
            }
            else
            {
                // A caller whose placeholder count exceeds the supplied args (or a malformed template) makes
                // string.Format throw FormatException. The logger must never throw, so fall back to the literal
                // message on failure rather than surfacing the formatting error to the plug-in pipeline.
                try
                {
                    formatted = string.Format(message, args);
                }
                catch (FormatException)
                {
                    formatted = message;
                }
            }

            // Always land in the plug-in trace log, so entries survive whether or not Application Insights is
            // configured. Information stays unprefixed (back-compat with Trace); other levels carry a tag. Active
            // BeginLogScope depth indents the trace-log line so nested operations read as a tree.
            string indent = _scopeDepth > 0 ? new string(' ', _scopeDepth * 2) : string.Empty;
            SafeTrace(indent + (level == LogLevel.Information ? formatted : $"[{level}] {formatted}"));
            if (exception != null)
            {
                SafeTrace(exception.ToString());
            }

            // Platform Application Insights logger. The default dimensions and any active BeginLogScope properties
            // ride along as scopes. The already-formatted text goes in as the "{0}" argument, NOT as the message
            // template: trace text can contain arbitrary '{'/'}' (e.g. serialized snapshots) that would break
            // template parsing.
            if (!ReferenceEquals(Logger, NullPluginTelemetryLogger.Instance))
            {
                try
                {
                    using (Logger.BeginScope(DefaultDimensions()))
                    {
                        Logger.Log(level, exception, "{0}", formatted);
                    }
                }
                catch (Exception ex)
                {
                    _safeTracingService.Trace("Application Insights logging failed: {0}", ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public IDisposable BeginLogScope(IReadOnlyDictionary<string, object?> properties)
        {
            properties = properties ?? throw new ArgumentNullException(nameof(properties));

            // Push the scope onto the Application Insights logger so the properties become dimensions on every
            // subsequent log until disposed, and bump the depth so trace-log lines indent under the scope.
            IDisposable? telemetryScope = null;
            try
            {
                telemetryScope = Logger.BeginScope(properties);
            }
            catch (Exception ex)
            {
                _safeTracingService.Trace("Application Insights scope failed: {0}", ex.Message);
            }

            _scopeDepth++;
            return new ScopeHandle(this, telemetryScope);
        }

        // Sets the currently executing handler method name for the "Method" telemetry dimension. Called by the
        // dispatch just before it invokes the handler; cleared implicitly when the execution (and this context) ends.
        internal void SetCurrentHandler(string? handlerMethod) => _currentHandler = handlerMethod;

        /// <inheritdoc />
        public void LogMetric(string name, long value, IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A metric name is required.", nameof(name));
            }

            _safeTracingService.Trace("[Metric] {0}={1}", name, value);

            if (ReferenceEquals(Logger, NullPluginTelemetryLogger.Instance))
            {
                return;
            }

            // Application Insights custom metric. The platform's metric API only takes string dimensions, so the
            // defaults and the caller's properties are flattened to invariant strings (caller properties win).
            try
            {
                var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> pair in DefaultDimensions())
                {
                    dimensions[pair.Key] = DimensionString(pair.Value);
                }

                if (properties != null)
                {
                    foreach (KeyValuePair<string, object?> pair in properties)
                    {
                        if (!string.IsNullOrEmpty(pair.Key))
                        {
                            dimensions[pair.Key] = DimensionString(pair.Value);
                        }
                    }
                }

                Logger.LogMetric(name, dimensions, value);
            }
            catch (Exception ex)
            {
                _safeTracingService.Trace("Application Insights metric failed: {0}", ex.Message);
            }
        }

        private static string DimensionString(object? value) =>
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        // The framework's default Application Insights dimensions, derived from the execution context. Attached to
        // every log/metric; user BeginLogScope properties stack on top.
        private Dictionary<string, object?> DefaultDimensions()
        {
            var dimensions = new Dictionary<string, object?>
            {
                ["CorrelationId"] = CorrelationId,
                ["MessageName"] = MessageName,
                ["Stage"] = Stage,
                ["Mode"] = Mode,
                ["Depth"] = Depth,
                ["PrimaryEntityName"] = PrimaryEntityName,
                ["PrimaryEntityId"] = PrimaryEntityId,
            };

            if (!string.IsNullOrEmpty(_pluginTypeName))
            {
                dimensions["PluginType"] = _pluginTypeName;
            }

            if (!string.IsNullOrEmpty(_currentHandler))
            {
                dimensions["Method"] = _currentHandler;
            }

            return dimensions;
        }

        // Disposes the (possibly null) Application Insights scope from BeginLogScope and pops the trace-log
        // indentation depth. Idempotent.
        private sealed class ScopeHandle : IDisposable
        {
            private readonly LocalPluginContext _context;
            private readonly IDisposable? _telemetryScope;
            private bool _disposed;

            public ScopeHandle(LocalPluginContext context, IDisposable? telemetryScope)
            {
                _context = context;
                _telemetryScope = telemetryScope;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _telemetryScope?.Dispose();
                }
                catch (Exception ex)
                {
                    _context._safeTracingService.Trace("Application Insights scope failed: {0}", ex.Message);
                }
                if (_context._scopeDepth > 0)
                {
                    _context._scopeDepth--;
                }
            }
        }

        /// <inheritdoc />
        public string AcquireManagedIdentityToken(IEnumerable<string> scopes)
        {
            scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            return ManagedIdentityService?.AcquireToken(scopes)
                ?? throw new InvalidPluginExecutionException("IManagedIdentityService is unavailable.");
        }

        /// <inheritdoc />
        public string AcquireManagedIdentityToken(Guid applicationUserId, IEnumerable<string> scopes)
        {
            scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            return ManagedIdentityService?.AcquireToken(applicationUserId, scopes)
                ?? throw new InvalidPluginExecutionException("IManagedIdentityService is unavailable.");
        }

        private static Guid? NullIfEmpty(Guid? value) => value == Guid.Empty ? null : value;

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        // Only the bulk table messages carry their rows in Targets. Any other message (a Custom API, a custom
        // action) can have an EntityCollection input that happens to be named Targets: that is not a bulk operation.
        private static EntityCollection? ResolveBulkTargets(IPluginExecutionContext context)
        {
            if (!IsBulkMessage(context.MessageName) || !context.InputParameters.Contains("Targets"))
            {
                return null;
            }

            return context.InputParameters["Targets"] as EntityCollection;
        }

        private static bool IsBulkMessage(string? messageName) =>
            string.Equals(messageName, PluginMessages.CreateMultiple, StringComparison.Ordinal)
            || string.Equals(messageName, PluginMessages.UpdateMultiple, StringComparison.Ordinal)
            || string.Equals(messageName, "UpsertMultiple", StringComparison.Ordinal);

        // Traces an ALREADY-COMPOSED string safely. Dataverse's ITracingService.Trace runs string.Format even when
        // no args are supplied, so a pre-built line containing a literal '{' or '}' (e.g. a serialized entity
        // snapshot) would throw FormatException. Passing the text as the {0} argument means string.Format never
        // parses the braces inside it.
        private void SafeTrace(string text) => _safeTracingService.Trace("{0}", text);

        private static IPluginExecutionContext ResolveExecutionContext(IServiceProvider serviceProvider)
        {
            // Request the richest context interface the platform serves, falling back to older ones. All versions
            // derive from IPluginExecutionContext, so the resolved instance is returned as that common type.
            object? context = null;
            foreach (Type type in PreferredContextTypes)
            {
                context = serviceProvider.GetService(type);
                if (context != null)
                {
                    break;
                }
            }

            if (context is IPluginExecutionContext pluginExecutionContext)
            {
                return pluginExecutionContext;
            }

            throw new InvalidPluginExecutionException("IPluginExecutionContext is unavailable.");
        }
    }
}
