using System;

namespace Vela.Caching
{
    /// <summary>
    /// The <see cref="IPluginCacheService"/> view the runtime injects: every key is scoped to the executing
    /// organization, so an entry never leaks across organizations should a sandbox worker be shared. Backed by the
    /// process-wide <see cref="PluginCacheService"/>; <see cref="Clear"/> removes this organization's entries only.
    /// </summary>
    internal sealed class OrganizationScopedCacheService : IPluginCacheService
    {
        private readonly PluginCacheService _inner;
        private readonly string _prefix;

        public OrganizationScopedCacheService(PluginCacheService inner, Guid organizationId)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _prefix = "org:" + organizationId.ToString("N") + ":";
        }

        public T GetOrCreate<T>(string key, TimeSpan ttl, Func<T> factory) => _inner.GetOrCreate(Scope(key), ttl, factory);

        public T Refresh<T>(string key, TimeSpan ttl, Func<T> factory) => _inner.Refresh(Scope(key), ttl, factory);

        public bool Invalidate(string key) => _inner.Invalidate(Scope(key));

        public void Clear() => _inner.RemoveByPrefix(_prefix);

        // Validate before prefixing: a blank key must still be rejected, not silently become the bare prefix.
        private string Scope(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            return _prefix + key;
        }
    }
}
