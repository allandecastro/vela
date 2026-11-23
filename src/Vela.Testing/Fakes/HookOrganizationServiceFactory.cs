using System;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// A fake <see cref="IOrganizationServiceFactory"/> returning a user service for a non-null user id and
    /// a system service for a null id. Both services are replaceable so a test can inject any implementation.
    /// The same instances are returned on every call so configured hooks remain observable.
    /// </summary>
    internal sealed class HookOrganizationServiceFactory : IOrganizationServiceFactory
    {
        /// <summary>Service returned when a non-null user id is requested.</summary>
        public IOrganizationService UserService { get; set; } = new HookOrganizationService();

        /// <summary>Service returned when a null user id (SYSTEM) is requested.</summary>
        public IOrganizationService SystemService { get; set; } = new HookOrganizationService();

        public IOrganizationService CreateOrganizationService(Guid? userId)
        {
            return userId.HasValue ? UserService : SystemService;
        }
    }
}
