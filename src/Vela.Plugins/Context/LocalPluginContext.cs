using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vela.DI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// Runtime wrapper around IServiceProvider for Dataverse plugins.
    /// </summary>
    public sealed class LocalPluginContext : ILocalPluginContext, IServiceResolverContext
    {
        private readonly ITracingService _safeTracingService;
        private readonly object? _pluginTelemetryLogger;
        private readonly string? _pluginTypeName;
        private string? _currentHandler;
        private int _scopeDepth;
        private readonly Guid? _environmentId;
        private readonly Guid? _tenantId;
        private readonly string _initiatingUserAgent;
        private readonly Guid? _portalsContactId;
        private readonly bool _isPortalsClientCall;
        private readonly bool _isApplicationUser;
        private readonly EntityCollection? _bulkTargets;
        private readonly IReadOnlyList<EntityImageCollection> _bulkPreEntityImagesCollection;
        private readonly IReadOnlyList<EntityImageCollection> _bulkPostEntityImagesCollection;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalPluginContext"/> class.
        /// </summary>
        /// <param name="serviceProvider">Runtime service provider.</param>
        public LocalPluginContext(IServiceProvider serviceProvider, string? pluginTypeName = null)
        {
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _pluginTypeName = pluginTypeName;
            PluginExecutionContext = ResolveExecutionContext(serviceProvider);
            TracingService = serviceProvider.GetService(typeof(ITracingService)) as ITracingService ?? new NullTracingService();
            _safeTracingService = TracingService;
            NotificationService = serviceProvider.GetService(typeof(IServiceEndpointNotificationService)) as IServiceEndpointNotificationService ?? new NullNotificationService();
            OrganizationServiceFactory = serviceProvider.GetService(typeof(IOrganizationServiceFactory)) as IOrganizationServiceFactory
                ?? throw new InvalidPluginExecutionException("IOrganizationServiceFactory is unavailable.");
            IOrganizationService initiatingUserService = OrganizationServiceFactory.CreateOrganizationService(PluginExecutionContext.InitiatingUserId);
            IOrganizationService callingUserService = OrganizationServiceFactory.CreateOrganizationService(PluginExecutionContext.UserId);
            IOrganizationService systemService = OrganizationServiceFactory.CreateOrganizationService(null);
            UserService = new TracingOrganizationService(initiatingUserService, this, "InitiatingUser");
            CallingUserService = new TracingOrganizationService(callingUserService, this, "CallingUser");
            SystemService = new TracingOrganizationService(systemService, this, "System");
            Logger = serviceProvider.GetService(typeof(ILogger)) as ILogger ?? NullLogger.Instance;
            ManagedIdentityService = serviceProvider.GetService(typeof(IManagedIdentityService)) as IManagedIdentityService;
            _pluginTelemetryLogger = ResolvePluginTelemetryLogger(serviceProvider);
            _environmentId = ResolveContextGuid(PluginExecutionContext, "EnvironmentId");
            _tenantId = ResolveContextGuid(PluginExecutionContext, "TenantId");
            _initiatingUserAgent = ResolveContextString(PluginExecutionContext, "InitiatingUserAgent") ?? string.Empty;
            _isPortalsClientCall = ResolveContextBool(PluginExecutionContext, "IsPortalsClientCall");
            _portalsContactId = ResolveContextGuid(PluginExecutionContext, "PortalsContactId");
            _isApplicationUser = ResolveContextBool(PluginExecutionContext, "IsApplicationUser");
            _bulkTargets = ResolveBulkTargets(PluginExecutionContext);
            _bulkPreEntityImagesCollection = ResolveBulkEntityImages(PluginExecutionContext, "PreEntityImagesCollection");
            _bulkPostEntityImagesCollection = ResolveBulkEntityImages(PluginExecutionContext, "PostEntityImagesCollection");
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
        public IServiceEndpointNotificationService NotificationService { get; }

        /// <inheritdoc />
        public IOrganizationServiceFactory OrganizationServiceFactory { get; }

        /// <inheritdoc />
        public IOrganizationService UserService { get; }

        /// <inheritdoc />
        public IOrganizationService CallingUserService { get; }

        /// <inheritdoc />
        public IOrganizationService SystemService { get; }

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
        public Guid? EnvironmentId => _environmentId;

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
        public IOrganizationService CreateOrganizationService(Guid? userId = null)
        {
            string scope = userId.HasValue ? "FactoryUser" : "FactorySystem";
            IOrganizationService service = OrganizationServiceFactory.CreateOrganizationService(userId);
            return new TracingOrganizationService(service, this, scope);
        }

        /// <inheritdoc />
        public IOrganizationService AsInitiatingUser()
        {
            return UserService;
        }

        /// <inheritdoc />
        public IOrganizationService AsCallingUser()
        {
            return CallingUserService;
        }

        /// <inheritdoc />
        public IOrganizationService AsUser(Guid userId)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentException("User id cannot be empty.", nameof(userId));
            }

            return CreateOrganizationService(userId);
        }

        /// <inheritdoc />
        public IOrganizationService AsSystem()
        {
            return SystemService;
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

            string formatted = args == null || args.Length == 0 ? message : string.Format(message, args);

            // Always land in the plug-in trace log, so entries survive whether or not Application Insights is
            // configured. Information stays unprefixed (back-compat with Trace); other levels carry a tag. Active
            // BeginLogScope depth indents the trace-log line so nested operations read as a tree.
            string indent = _scopeDepth > 0 ? new string(' ', _scopeDepth * 2) : string.Empty;
            _safeTracingService.Trace(indent + (level == LogLevel.Information ? formatted : $"[{level}] {formatted}"));
            if (exception != null)
            {
                _safeTracingService.Trace(exception.ToString());
            }

            // Platform MEL logger (routes to Application Insights when the environment supplies it; NullLogger
            // otherwise). The default dimensions and any active BeginLogScope properties ride along as scopes.
            // Log the already-formatted text as a literal (identity formatter), NOT as a MEL message template:
            // trace text can contain arbitrary '{'/'}' (e.g. serialized snapshots) that would break template
            // parsing. Structured dimensions ride along via the scopes instead.
            using (Logger.BeginScope(DefaultDimensions()))
            {
                Logger.Log(level, default(EventId), formatted, exception, LiteralFormatter);
            }

            ForwardToPluginTelemetryLogger(level, exception, formatted);
        }

        /// <inheritdoc />
        public IDisposable BeginLogScope(IReadOnlyDictionary<string, object?> properties)
        {
            properties = properties ?? throw new ArgumentNullException(nameof(properties));

            // Push the scope onto both logger channels so the properties become Application Insights dimensions on
            // every subsequent log until disposed, and bump the depth so trace-log lines indent under the scope.
            _scopeDepth++;
            IDisposable? melScope = Logger.BeginScope(properties);
            IDisposable? telemetryScope = (_pluginTelemetryLogger as ILogger)?.BeginScope(properties);
            return new ScopeHandle(this, melScope, telemetryScope);
        }

        // Sets the currently executing handler method name for the "Method" telemetry dimension. Called by the
        // dispatch just before it invokes the handler; cleared implicitly when the execution (and this context) ends.
        internal void SetCurrentHandler(string? handlerMethod) => _currentHandler = handlerMethod;

        /// <inheritdoc />
        public void LogMetric(string name, double value, IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A metric name is required.", nameof(name));
            }

            _safeTracingService.Trace("[Metric] {0}={1}", name, value);

            // Emit as a structured log (visible via ILogger / Application Insights traces).
            string metricLine = string.Format("{0}={1}", name, value);
            using (Logger.BeginScope(DefaultDimensions()))
            {
                Logger.Log(LogLevel.Information, default(EventId), metricLine, null, LiteralFormatter);
            }

            // Best-effort Application Insights custom metric via the native PluginTelemetry logger, when present.
            ForwardMetricToPluginTelemetryLogger(name, value, properties);
        }

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

        // Disposes the (possibly null) scope handles from BeginLogScope's two channels and pops the trace-log
        // indentation depth. Idempotent.
        private sealed class ScopeHandle : IDisposable
        {
            private readonly LocalPluginContext _context;
            private readonly IDisposable? _first;
            private readonly IDisposable? _second;
            private bool _disposed;

            public ScopeHandle(LocalPluginContext context, IDisposable? first, IDisposable? second)
            {
                _context = context;
                _first = first;
                _second = second;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _first?.Dispose();
                _second?.Dispose();
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

        private static object? ResolvePluginTelemetryLogger(IServiceProvider serviceProvider)
        {
            Type? loggerType = Type.GetType("Microsoft.Xrm.Sdk.PluginTelemetry.ILogger, Microsoft.Xrm.Sdk", throwOnError: false);
            return loggerType == null ? null : serviceProvider.GetService(loggerType);
        }

        private static Guid? ResolveContextGuid(object context, string propertyName)
        {
            if (!TryGetContextProperty(context, propertyName, out object? value))
            {
                return null;
            }

            if (value is Guid guid)
            {
                return guid == Guid.Empty ? (Guid?)null : guid;
            }

            return null;
        }

        private static string? ResolveContextString(object context, string propertyName)
        {
            if (!TryGetContextProperty(context, propertyName, out object? value))
            {
                return null;
            }

            return value as string;
        }

        private static bool ResolveContextBool(object context, string propertyName)
        {
            if (!TryGetContextProperty(context, propertyName, out object? value))
            {
                return false;
            }

            return value is bool flag && flag;
        }

        private static EntityCollection? ResolveBulkTargets(IPluginExecutionContext context)
        {
            if (!context.InputParameters.Contains("Targets"))
            {
                return null;
            }

            return context.InputParameters["Targets"] as EntityCollection;
        }

        private static IReadOnlyList<EntityImageCollection> ResolveBulkEntityImages(IPluginExecutionContext context, string propertyName)
        {
            if (!TryGetContextProperty(context, propertyName, out object? value) || value == null)
            {
                return Array.Empty<EntityImageCollection>();
            }

            if (value is IReadOnlyList<EntityImageCollection> readOnlyList)
            {
                return readOnlyList;
            }

            if (value is IEnumerable<EntityImageCollection> typedEnumerable)
            {
                List<EntityImageCollection> items = new List<EntityImageCollection>();
                foreach (EntityImageCollection imageCollection in typedEnumerable)
                {
                    items.Add(imageCollection);
                }

                return items;
            }

            if (value is IEnumerable untypedEnumerable)
            {
                List<EntityImageCollection> items = new List<EntityImageCollection>();
                foreach (object? item in untypedEnumerable)
                {
                    if (item is EntityImageCollection imageCollection)
                    {
                        items.Add(imageCollection);
                    }
                }

                return items;
            }

            return Array.Empty<EntityImageCollection>();
        }

        private static bool TryGetContextProperty(object context, string propertyName, out object? value)
        {
            value = null;
            if (context == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            PropertyInfo? property = context.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                return false;
            }

            value = property.GetValue(context);
            return true;
        }

        // Identity formatter: logs a pre-formatted string verbatim, bypassing MEL message-template parsing.
        private static readonly Func<string, Exception?, string> LiteralFormatter = (state, _) => state;

        private void ForwardToPluginTelemetryLogger(LogLevel level, Exception? exception, string formatted)
        {
            if (_pluginTelemetryLogger == null)
            {
                return;
            }

            // Skip when the native telemetry logger IS the same instance already logged as Logger, to avoid a
            // duplicate Application Insights entry.
            if (ReferenceEquals(_pluginTelemetryLogger, Logger))
            {
                return;
            }

            try
            {
                // Preferred: the PluginTelemetry logger is a Microsoft.Extensions.Logging.ILogger, so log at the
                // real level with structured args and the default dimensions as a scope.
                if (_pluginTelemetryLogger is ILogger telemetry)
                {
                    using (telemetry.BeginScope(DefaultDimensions()))
                    {
                        telemetry.Log(level, default(EventId), formatted, exception, LiteralFormatter);
                    }

                    return;
                }

                // Fallback (older SDK where the logger is not MEL): call the level-named method, else Information,
                // else Trace, with the already-formatted string.
                MethodInfo? method = _pluginTelemetryLogger.GetType().GetMethod(LevelMethodName(level), new[] { typeof(string) })
                    ?? _pluginTelemetryLogger.GetType().GetMethod("LogInformation", new[] { typeof(string) })
                    ?? _pluginTelemetryLogger.GetType().GetMethod("Trace", new[] { typeof(string) });
                method?.Invoke(_pluginTelemetryLogger, new object[] { formatted });
            }
            catch (Exception ex)
            {
                _safeTracingService.Trace("Plugin telemetry forwarding failed: {0}", ex.Message);
            }
        }

        private static string LevelMethodName(LogLevel level) => level switch
        {
            LogLevel.Trace => "LogTrace",
            LogLevel.Debug => "LogDebug",
            LogLevel.Warning => "LogWarning",
            LogLevel.Error => "LogError",
            LogLevel.Critical => "LogCritical",
            _ => "LogInformation",
        };

        private void ForwardMetricToPluginTelemetryLogger(string name, double value, IReadOnlyDictionary<string, object?>? properties)
        {
            if (_pluginTelemetryLogger == null)
            {
                return;
            }

            try
            {
                // Application Insights custom metrics are exposed through the static
                // Microsoft.Xrm.Sdk.PluginTelemetry.LoggerExtensions.LogMetric(ILogger, string, double, ...).
                Type? extensions = Type.GetType("Microsoft.Xrm.Sdk.PluginTelemetry.LoggerExtensions, Microsoft.Xrm.Sdk", throwOnError: false);
                if (extensions == null)
                {
                    return;
                }

                foreach (MethodInfo method in extensions.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "LogMetric")
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length >= 3
                        && parameters[0].ParameterType.IsInstanceOfType(_pluginTelemetryLogger)
                        && parameters[1].ParameterType == typeof(string)
                        && parameters[2].ParameterType == typeof(double))
                    {
                        object?[] callArgs = new object?[parameters.Length];
                        callArgs[0] = _pluginTelemetryLogger;
                        callArgs[1] = name;
                        callArgs[2] = value;
                        for (int i = 3; i < parameters.Length; i++)
                        {
                            callArgs[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                        }

                        method.Invoke(null, callArgs);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _safeTracingService.Trace("Plugin telemetry metric forwarding failed: {0}", ex.Message);
            }
        }

        private static IPluginExecutionContext ResolveExecutionContext(IServiceProvider serviceProvider)
        {
            // Dataverse exposes successive context interface versions (IPluginExecutionContext2..7), each
            // adding members. We request the richest one the running platform offers and fall back to
            // older versions so the framework keeps working across environments. The types are resolved
            // by name (Type.GetType) because older SDK assemblies may not define the newer interfaces.
            // All versions derive from IPluginExecutionContext, so the resolved instance is returned as
            // that common type.
            object? context = null;
            string[] preferredContextTypes =
            {
                "Microsoft.Xrm.Sdk.IPluginExecutionContext7, Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.IPluginExecutionContext6, Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.IPluginExecutionContext5, Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.IPluginExecutionContext4, Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.IPluginExecutionContext3, Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.IPluginExecutionContext2, Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.IPluginExecutionContext, Microsoft.Xrm.Sdk"
            };

            foreach (string typeName in preferredContextTypes)
            {
                Type? type = Type.GetType(typeName, throwOnError: false);
                if (type == null)
                {
                    continue;
                }

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
