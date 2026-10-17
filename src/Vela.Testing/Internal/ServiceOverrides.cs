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

        public bool IsEmpty => _services.Count == 0;

        public void Add<TService>(TService instance) where TService : class
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
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
    }
}
