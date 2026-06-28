using System;
using System.Collections.Generic;

namespace Vela.Testing
{
    /// <summary>
    /// Minimal <see cref="IServiceProvider"/> backed by a type-keyed dictionary. Returns <c>null</c>
    /// for unregistered service types, mirroring how the Dataverse sandbox provider behaves.
    /// </summary>
    internal sealed class DictionaryServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyDictionary<Type, object> _services;

        public DictionaryServiceProvider(IReadOnlyDictionary<Type, object> services)
        {
            _services = services;
        }

        public object? GetService(Type serviceType)
        {
            return _services.TryGetValue(serviceType, out object? service) ? service : null;
        }
    }
}
