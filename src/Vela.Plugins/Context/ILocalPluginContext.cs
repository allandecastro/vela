using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
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
        /// Gets organization service for default user context (initiating user).
        /// </summary>
        IOrganizationService UserService { get; }

        /// <summary>
        /// Gets organization service for the step user context (Run in User's Context).
        /// </summary>
        IOrganizationService CallingUserService { get; }

        /// <summary>
        /// Gets organization service for SYSTEM user context.
        /// </summary>
        IOrganizationService SystemService { get; }

        /// <summary>
        /// Gets logger abstraction.
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
        /// Gets the Dataverse environment id when available.
        /// </summary>
        Guid? EnvironmentId { get; }

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
        /// Gets the bulk operation targets for messages such as CreateMultiple and UpdateMultiple.
        /// Returns null when the current execution is not a bulk operation.
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
        /// Gets a value indicating whether the current message is a bulk operation.
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
        /// Creates organization service for a specific user id, or system when null.
        /// </summary>
        /// <param name="userId">User id or null.</param>
        /// <returns>Organization service.</returns>
        IOrganizationService CreateOrganizationService(Guid? userId = null);

        /// <summary>
        /// Gets organization service in initiating user context.
        /// </summary>
        /// <returns>Organization service for <see cref="InitiatingUserId"/>.</returns>
        IOrganizationService AsInitiatingUser();

        /// <summary>
        /// Gets organization service in calling (step) user context.
        /// </summary>
        /// <returns>Organization service for <see cref="UserId"/>.</returns>
        IOrganizationService AsCallingUser();

        /// <summary>
        /// Gets organization service in a specific user context.
        /// </summary>
        /// <param name="userId">Dataverse system user id.</param>
        /// <returns>Organization service for the specified user id.</returns>
        IOrganizationService AsUser(Guid userId);

        /// <summary>
        /// Gets organization service in system context.
        /// </summary>
        /// <returns>Organization service for system context.</returns>
        IOrganizationService AsSystem();

        /// <summary>
        /// Traces formatted runtime messages.
        /// </summary>
        /// <param name="format">Message format.</param>
        /// <param name="args">Format args.</param>
        void Trace(string format, params object[] args);

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
