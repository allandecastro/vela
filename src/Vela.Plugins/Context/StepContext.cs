using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Vela.DI;

namespace Vela.Context
{
    /// <summary>
    /// Base for the per-message <b>pre-stage</b> typed contexts: wraps the runtime <see cref="ILocalPluginContext"/>
    /// and forwards its whole surface, adding the typed <see cref="Request"/> (a view over the live
    /// <c>InputParameters</c>). The delegation lives here, once; message pre-contexts derive this with the concrete
    /// request type. There is no <c>Response</c> here - it is only meaningful after the main operation, so it lives
    /// on <see cref="PostStepContext{TRequest, TResponse}"/>.
    /// </summary>
    /// <typeparam name="TRequest">The SDK request type for the message.</typeparam>
    public abstract class PreStepContext<TRequest> : IStepRequestContext<TRequest>, IServiceResolverContext
        where TRequest : OrganizationRequest, new()
    {
        private readonly ILocalPluginContext _inner;

        /// <summary>Creates a typed context wrapping the runtime context.</summary>
        /// <param name="inner">The runtime plugin context to wrap.</param>
        protected PreStepContext(ILocalPluginContext inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        IPluginServiceResolver IServiceResolverContext.Resolver =>
            (_inner as IServiceResolverContext)?.Resolver
            ?? throw new InvalidPluginExecutionException(
                $"The wrapped context '{_inner.GetType().FullName}' does not expose a service resolver, so " +
                "GetService/GetRequiredService are unavailable in this typed handler.");

        /// <inheritdoc />
        public TRequest Request => _inner.GetRequest<TRequest>();

        // ---- ILocalPluginContext (forwarded to the wrapped runtime context) ----------------------------

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => _inner.ServiceProvider;
        /// <inheritdoc />
        public IPluginExecutionContext PluginExecutionContext => _inner.PluginExecutionContext;
        /// <inheritdoc />
        public ITracingService TracingService => _inner.TracingService;
        /// <inheritdoc />
        public IServiceEndpointNotificationService NotificationService => _inner.NotificationService;
        /// <inheritdoc />
        public IOrganizationServiceFactory OrganizationServiceFactory => _inner.OrganizationServiceFactory;
        /// <inheritdoc />
        public IOrganizationService UserService => _inner.UserService;
        /// <inheritdoc />
        public IOrganizationService CallingUserService => _inner.CallingUserService;
        /// <inheritdoc />
        public IOrganizationService SystemService => _inner.SystemService;
        /// <inheritdoc />
        public ILogger Logger => _inner.Logger;
        /// <inheritdoc />
        public IManagedIdentityService? ManagedIdentityService => _inner.ManagedIdentityService;
        /// <inheritdoc />
        public string MessageName => _inner.MessageName;
        /// <inheritdoc />
        public int Stage => _inner.Stage;
        /// <inheritdoc />
        public int Mode => _inner.Mode;
        /// <inheritdoc />
        public int Depth => _inner.Depth;
        /// <inheritdoc />
        public string PrimaryEntityName => _inner.PrimaryEntityName;
        /// <inheritdoc />
        public Guid PrimaryEntityId => _inner.PrimaryEntityId;
        /// <inheritdoc />
        public Guid UserId => _inner.UserId;
        /// <inheritdoc />
        public Guid InitiatingUserId => _inner.InitiatingUserId;
        /// <inheritdoc />
        public Guid CorrelationId => _inner.CorrelationId;
        /// <inheritdoc />
        public Guid OrganizationId => _inner.OrganizationId;
        /// <inheritdoc />
        public bool IsExecutingOffline => _inner.IsExecutingOffline;
        /// <inheritdoc />
        public bool IsInTransaction => _inner.IsInTransaction;
        /// <inheritdoc />
        public bool IsApplicationUser => _inner.IsApplicationUser;
        /// <inheritdoc />
        public Guid? EnvironmentId => _inner.EnvironmentId;
        /// <inheritdoc />
        public Guid? TenantId => _inner.TenantId;
        /// <inheritdoc />
        public string InitiatingUserAgent => _inner.InitiatingUserAgent;
        /// <inheritdoc />
        public bool IsPortalsClientCall => _inner.IsPortalsClientCall;
        /// <inheritdoc />
        public Guid? PortalsContactId => _inner.PortalsContactId;
        /// <inheritdoc />
        public EntityCollection? BulkTargets => _inner.BulkTargets;
        /// <inheritdoc />
        public IReadOnlyList<EntityImageCollection> BulkPreEntityImagesCollection => _inner.BulkPreEntityImagesCollection;
        /// <inheritdoc />
        public IReadOnlyList<EntityImageCollection> BulkPostEntityImagesCollection => _inner.BulkPostEntityImagesCollection;
        /// <inheritdoc />
        public bool IsBulkOperation => _inner.IsBulkOperation;
        /// <inheritdoc />
        public string? Tag => _inner.Tag;

        /// <inheritdoc />
        public object? GetSharedVariable(string key) => _inner.GetSharedVariable(key);
        /// <inheritdoc />
        public bool TryGetSharedVariable<T>(string key, out T value) => _inner.TryGetSharedVariable(key, out value);
        /// <inheritdoc />
        public IOrganizationService CreateOrganizationService(Guid? userId = null) => _inner.CreateOrganizationService(userId);
        /// <inheritdoc />
        public IOrganizationService AsInitiatingUser() => _inner.AsInitiatingUser();
        /// <inheritdoc />
        public IOrganizationService AsCallingUser() => _inner.AsCallingUser();
        /// <inheritdoc />
        public IOrganizationService AsUser(Guid userId) => _inner.AsUser(userId);
        /// <inheritdoc />
        public IOrganizationService AsSystem() => _inner.AsSystem();
        /// <inheritdoc />
        public void Trace(string format, params object[] args) => _inner.Trace(format, args);

        /// <inheritdoc />
        public void Log(LogLevel level, Exception? exception, string message, params object[] args) => _inner.Log(level, exception, message, args);

        /// <inheritdoc />
        public IDisposable BeginLogScope(IReadOnlyDictionary<string, object?> properties) => _inner.BeginLogScope(properties);

        /// <inheritdoc />
        public void LogMetric(string name, double value, IReadOnlyDictionary<string, object?>? properties = null) => _inner.LogMetric(name, value, properties);
        /// <inheritdoc />
        public string AcquireManagedIdentityToken(IEnumerable<string> scopes) => _inner.AcquireManagedIdentityToken(scopes);
        /// <inheritdoc />
        public string AcquireManagedIdentityToken(Guid applicationUserId, IEnumerable<string> scopes) => _inner.AcquireManagedIdentityToken(applicationUserId, scopes);
    }

    /// <summary>
    /// Base for the per-message <b>post-stage</b> typed contexts: everything a <see cref="PreStepContext{TRequest}"/>
    /// has, plus the typed <see cref="Response"/> (a view over the live <c>OutputParameters</c>, filled by the
    /// platform after the main operation).
    /// </summary>
    /// <typeparam name="TRequest">The SDK request type for the message.</typeparam>
    /// <typeparam name="TResponse">The SDK response type for the message.</typeparam>
    public abstract class PostStepContext<TRequest, TResponse> : PreStepContext<TRequest>, IStepResponseContext<TResponse>
        where TRequest : OrganizationRequest, new()
        where TResponse : OrganizationResponse, new()
    {
        /// <summary>Creates a typed post-stage context wrapping the runtime context.</summary>
        /// <param name="inner">The runtime plugin context to wrap.</param>
        protected PostStepContext(ILocalPluginContext inner) : base(inner) { }

        /// <inheritdoc />
        public TResponse Response => this.GetResponse<TResponse>();
    }
}
