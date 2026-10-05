using System;
using Vela.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;

namespace Vela.DI
{
    /// <summary>
    /// Microsoft.Extensions.DependencyInjection registration helper.
    /// </summary>
    public static class MicrosoftDiExtensions
    {
        /// <summary>
        /// Adds the default plugin runtime service set.
        /// </summary>
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

            services.AddScoped(_ => context);
            services.AddScoped(_ => context.PluginExecutionContext);
            services.AddScoped(_ => context.TracingService);
            // IOrganizationService resolves to the initiating user, matching SimpleServiceResolver. Do NOT also
            // register SystemService under the same type: Microsoft DI returns the last registration, so that would
            // silently hand every consumer the elevated system service. System access is available via the
            // Func<Guid?, IOrganizationService> factory below (null => system) or ctx.SystemService.
            services.AddScoped(_ => context.UserService);
            services.AddScoped(_ => context.Logger ?? NullLogger.Instance);
            services.AddScoped<Func<Guid?, IOrganizationService>>(_ => userId => context.CreateOrganizationService(userId));
            if (context.ManagedIdentityService != null)
            {
                services.AddScoped(_ => context.ManagedIdentityService);
            }
            return services;
        }
    }
}
