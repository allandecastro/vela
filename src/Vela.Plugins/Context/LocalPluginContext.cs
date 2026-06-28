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
    public sealed class LocalPluginContext : ILocalPluginContext
    {
        private readonly ITracingService _safeTracingService;
        private readonly object? _pluginTelemetryLogger;
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
        public LocalPluginContext(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
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

            string message = args == null || args.Length == 0 ? format : string.Format(format, args);
            _safeTracingService.Trace(message);
            using (Logger.BeginScope(new Dictionary<string, object?>
            {
                ["correlationId"] = CorrelationId,
                ["messageName"] = MessageName,
                ["stage"] = Stage,
                ["mode"] = Mode,
                ["depth"] = Depth,
                ["primaryEntityName"] = PrimaryEntityName,
                ["primaryEntityId"] = PrimaryEntityId
            }))
            {
                Logger.LogInformation(message);
            }
            ForwardToPluginTelemetryLogger(message);
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

        private void ForwardToPluginTelemetryLogger(string message)
        {
            if (_pluginTelemetryLogger == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                MethodInfo? logInfo = _pluginTelemetryLogger.GetType().GetMethod("LogInformation", new[] { typeof(string) });
                if (logInfo != null)
                {
                    logInfo.Invoke(_pluginTelemetryLogger, new object[] { message });
                    return;
                }

                MethodInfo? trace = _pluginTelemetryLogger.GetType().GetMethod("Trace", new[] { typeof(string) });
                trace?.Invoke(_pluginTelemetryLogger, new object[] { message });
            }
            catch (Exception ex)
            {
                _safeTracingService.Trace("Plugin telemetry forwarding failed: {0}", ex.Message);
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
