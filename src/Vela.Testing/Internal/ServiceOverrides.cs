using System;
using System.Collections.Generic;
using Vela.DI;

namespace Vela.Testing
{
    // The services a test registered with WithService, handed to the plug-in through its service provider. Vela's
    // runtime applies them after the assembly registrar and the plug-in's ConfigureServices, so they win.
    internal sealed class ServiceOverrides : IServiceOverrides
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        // The platform services the context reads from the platform's IServiceProvider, never from Vela's resolver,
        // each with the hint naming how the harness replaces it. WithService for one of them would only reach the
        // resolver (ctx.GetRequiredService<T>()) while ctx.TracingService, ctx.Service, AcquireManagedIdentityToken...
        // keep the harness's own, so it is rejected and the test is pointed at the way that reaches both.
        private readonly IReadOnlyList<KeyValuePair<Type, string>> _platformServices;

        public ServiceOverrides(IReadOnlyList<KeyValuePair<Type, string>> platformServices)
        {
            _platformServices = platformServices;
        }

        public bool IsEmpty => _services.Count == 0;

        public void Add<TService>(TService instance) where TService : class
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            foreach (KeyValuePair<Type, string> platform in _platformServices)
            {
                if (IsPlatformService(typeof(TService), platform.Key))
                {
                    throw new ArgumentException(
                        $"WithService cannot replace {typeof(TService).FullName}: the plugin context reads it from the platform, not from the service resolver. {platform.Value}",
                        nameof(instance));
                }
            }

            _services[typeof(TService)] = instance;
        }

        public void Apply(SimpleServiceResolver services)
        {
            foreach (KeyValuePair<Type, object> service in _services)
            {
                services.AddScopedInstance(service.Key, service.Value);
            }
        }

        // The execution context is served as IPluginExecutionContext and read through its successive interface
        // versions (IPluginExecutionContext2..7), so every interface of that family counts.
        private static bool IsPlatformService(Type requested, Type platform)
        {
            if (requested == platform)
            {
                return true;
            }

            return platform == typeof(Microsoft.Xrm.Sdk.IExecutionContext)
                && requested.IsInterface
                && platform.IsAssignableFrom(requested);
        }
    }
}
