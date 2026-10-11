using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// Strongly-typed runtime plugin context abstraction.
    /// </summary>
    public interface ILocalPluginContext
    {
        /// <summary>
        /// Gets the original runtime service provider.
        /// </summary>
        IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the current execution context.
        /// </summary>
        IPluginExecutionContext PluginExecutionContext { get; }

        /// <summary>
        /// Gets tracing service.
        /// </summary>
        ITracingService TracingService { get; }

        /// <summary>
        /// Gets service endpoint notification service.
        /// </summary>
        IServiceEndpointNotificationService NotificationService { get; }

        /// <summary>
        /// Gets organization service factory.
        /// </summary>
        IOrganizationServiceFactory OrganizationServiceFactory { get; }

        /// <summary>
        /// Gets the organization service for the current user (<see cref="UserId"/>): the step's "Run in User's
        /// Context" user when the step impersonates, otherwise the caller (including a CallerId impersonation).
        /// Record-level security applies. This is the service to use by default, and the one dependency injection
        /// resolves for <see cref="IOrganizationService"/>.
        /// </summary>
        IOrganizationService Service { get; }

        /// <summary>
        /// Gets the organization service for the SYSTEM user, which bypasses record-level security. Use it only
        /// where elevation is intended.
        /// </summary>
        IOrganizationService SystemService { get; }

        /// <summary>
        /// Gets the platform's Application Insights logger (<c>Microsoft.Xrm.Sdk.PluginTelemetry.ILogger</c>). Never
        /// null: a no-op logger stands in when the service provider supplies none. Prefer the leveled context helpers
        /// (<c>LogInformation</c>, ...), which also write to the plug-in trace log and attach the default dimensions.
        /// </summary>
        ILogger Logger { get; }

        /// <summary>
        /// Gets managed identity service when available in the runtime.
        /// </summary>
        IManagedIdentityService? ManagedIdentityService { get; }

        /// <summary>
        /// Gets plugin message name.
        /// </summary>
        string MessageName { get; }

        /// <summary>
        /// Gets pipeline stage.
        /// </summary>
        int Stage { get; }

        /// <summary>
        /// Gets execution mode.
        /// </summary>
        int Mode { get; }

        /// <summary>
        /// Gets execution depth.
        /// </summary>
        int Depth { get; }

        /// <summary>
        /// Gets primary entity name.
        /// </summary>
        string PrimaryEntityName { get; }

        /// <summary>
        /// Gets primary entity id.
        /// </summary>
        Guid PrimaryEntityId { get; }

        /// <summary>
        /// Gets current user id.
        /// </summary>
        Guid UserId { get; }

        /// <summary>
        /// Gets initiating user id.
        /// </summary>
        Guid InitiatingUserId { get; }

        /// <summary>
        /// Gets correlation id.
        /// </summary>
        Guid CorrelationId { get; }

        /// <summary>
        /// Gets organization id.
        /// </summary>
        Guid OrganizationId { get; }

        /// <summary>
        /// Gets offline execution flag.
        /// </summary>
        bool IsExecutingOffline { get; }

        /// <summary>
        /// Gets transaction flag.
        /// </summary>
        bool IsInTransaction { get; }

        /// <summary>
        /// Gets a value indicating whether the caller is an application user.
        /// Returns false when the runtime does not expose this property.
        /// </summary>
        bool IsApplicationUser { get; }

        /// <summary>
        /// Gets the Dataverse environment id when available, as the platform reports it (a string on
        /// <c>IPluginExecutionContext6</c>). Returns null when the runtime does not expose it or it is empty.
        /// </summary>
        string? EnvironmentId { get; }

        /// <summary>
        /// Gets the Entra tenant id when available.
        /// </summary>
        Guid? TenantId { get; }

        /// <summary>
        /// Gets the initiating client user-agent when available.
        /// </summary>
        string InitiatingUserAgent { get; }

        /// <summary>
        /// Gets a value indicating whether the request originated from a Power Pages portal client.
        /// Returns false when the runtime does not expose this property.
        /// </summary>
        bool IsPortalsClientCall { get; }

        /// <summary>
        /// Gets the portal contact id for portal-originated requests when available.
        /// </summary>
        Guid? PortalsContactId { get; }

        /// <summary>
        /// Gets the bulk operation targets of a CreateMultiple, UpdateMultiple or UpsertMultiple execution.
        /// Returns null when the current execution is not one of those messages.
        /// </summary>
        EntityCollection? BulkTargets { get; }

        /// <summary>
        /// Gets the pre-image collection per target for bulk messages when available.
        /// </summary>
        IReadOnlyList<EntityImageCollection> BulkPreEntityImagesCollection { get; }

        /// <summary>
        /// Gets the post-image collection per target for bulk messages when available.
        /// </summary>
        IReadOnlyList<EntityImageCollection> BulkPostEntityImagesCollection { get; }

        /// <summary>
        /// Gets a value indicating whether the current message is a bulk operation (CreateMultiple, UpdateMultiple or
        /// UpsertMultiple with a <c>Targets</c> collection).
        /// </summary>
        bool IsBulkOperation { get; }

        /// <summary>
        /// Gets the <c>tag</c> shared variable a client application passed on the request (Dataverse's documented
        /// client-to-plugin channel), or null when none was supplied. Shorthand for <c>GetSharedVariable("tag")</c>.
        /// </summary>
        string? Tag { get; }

        /// <summary>
        /// Gets the shared variable for <paramref name="key"/>, or null when absent. Shared variables flow across
        /// the pipeline: set by a client on the request (the <c>tag</c> parameter), by an earlier plugin in the
        /// same operation, or inherited from the parent context of a cascaded operation.
        /// </summary>
        /// <param name="key">Shared variable key.</param>
        /// <returns>The value, or null when not present.</returns>
        object? GetSharedVariable(string key);

        /// <summary>
        /// Gets the shared variable for <paramref name="key"/> typed as <typeparamref name="T"/>; returns false
        /// when it is absent or not of that type.
        /// </summary>
        /// <typeparam name="T">Expected value type.</typeparam>
        /// <param name="key">Shared variable key.</param>
        /// <param name="value">The resolved value when found.</param>
        /// <returns>True when a value of type <typeparamref name="T"/> was found.</returns>
        bool TryGetSharedVariable<T>(string key, out T value);

        /// <summary>
        /// Creates an organization service that acts as a specific user, for example
        /// <c>ctx.AsUser(ctx.InitiatingUserId)</c> to act as the user who started the operation.
        /// </summary>
        /// <param name="userId">Dataverse system user id.</param>
        /// <returns>Organization service for the specified user id.</returns>
        IOrganizationService AsUser(Guid userId);

        /// <summary>
        /// Traces formatted runtime messages.
        /// </summary>
        /// <param name="format">Message format.</param>
        /// <param name="args">Format args.</param>
        void Trace(string format, params object[] args);

        /// <summary>
        /// Writes a log entry at the given severity to the plug-in trace log and, when the environment has
        /// Application Insights configured, to Application Insights (with the pipeline dimensions and any active
        /// <see cref="BeginLogScope"/> properties). Always writes to the trace log, so entries are never lost when
        /// Application Insights is not set up. Never throws for telemetry reasons. Prefer the leveled extension
        /// helpers (<c>LogInformation</c>, <c>LogWarning</c>, <c>LogError</c>, ...).
        /// </summary>
        /// <param name="level">Severity.</param>
        /// <param name="exception">Optional exception to attach.</param>
        /// <param name="message">Message template (structured args bind to Application Insights dimensions).</param>
        /// <param name="args">Template args.</param>
        void Log(LogLevel level, Exception? exception, string message, params object[] args);

        /// <summary>
        /// Begins a logging scope: the given properties are attached as Application Insights custom dimensions to
        /// every log written until the returned handle is disposed (on top of the framework's default dimensions).
        /// Nestable.
        /// </summary>
        /// <param name="properties">Custom dimensions to attach.</param>
        IDisposable BeginLogScope(IReadOnlyDictionary<string, object?> properties);

        /// <summary>
        /// Emits a custom metric to Application Insights (when configured), with the default dimensions plus
        /// <paramref name="properties"/> (as invariant strings), and a <c>[Metric]</c> line to the plug-in trace log.
        /// Never throws for telemetry reasons.
        /// </summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">Metric value (the platform's metric API only takes integers).</param>
        /// <param name="properties">Optional custom dimensions.</param>
        void LogMetric(string name, long value, IReadOnlyDictionary<string, object?>? properties = null);

        /// <summary>
        /// Acquires a managed identity access token for the given scopes.
        /// </summary>
        /// <param name="scopes">Requested scopes.</param>
        /// <returns>Access token.</returns>
        string AcquireManagedIdentityToken(IEnumerable<string> scopes);

        /// <summary>
        /// Acquires a managed identity access token for a specific application user.
        /// </summary>
        /// <param name="applicationUserId">Application user id.</param>
        /// <param name="scopes">Requested scopes.</param>
        /// <returns>Access token.</returns>
        string AcquireManagedIdentityToken(Guid applicationUserId, IEnumerable<string> scopes);
    }
}
