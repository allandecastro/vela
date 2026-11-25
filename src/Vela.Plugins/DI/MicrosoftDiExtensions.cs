using System;
using System.Collections.Generic;
using Vela.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;

namespace Vela.DI
{
    /// <summary>
    /// Microsoft.Extensions.DependencyInjection registration helper.
    /// </summary>
    public static class MicrosoftDiExtensions
    {
        /// <summary>
        /// Adds the default plugin runtime service set, the same one the built-in resolver registers:
        /// <see cref="IOrganizationService"/> resolves the current user's service, never SYSTEM.
        /// </summary>
        /// <remarks>
        /// For tests and hosts that run outside the Dataverse sandbox. Inside a plugin, the built-in
        /// <see cref="SimpleServiceResolver"/> is the supported container: Vela.Plugins references only the
        /// dependency-injection abstractions, and shipping the full Microsoft.Extensions.DependencyInjection container
        /// in a plugin package is not something Vela supports or tests.
        /// </remarks>
        /// <param name="services">Service collection.</param>
        /// <param name="context">Execution context wrapper.</param>
        /// <returns>Service collection.</returns>
        public static IServiceCollection AddPluginRuntime(this IServiceCollection services, ILocalPluginContext context)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            foreach (KeyValuePair<Type, Func<object>> registration in SimpleServiceResolver.DefaultServices(context))
            {
                Func<object> create = registration.Value;
                services.AddScoped(registration.Key, _ => create());
            }

            return services;
        }
    }
}
