using System;

namespace Vela.Caching
{
    /// <summary>
    /// Global in-process cache service for plugin workloads.
    /// </summary>
    /// <remarks>
    /// The cache exposes a synchronous-only surface on purpose: the Dataverse plugin pipeline executes
    /// synchronously, and a synchronous lock (<c>Monitor</c>) is the most clearly sandbox-supported
    /// primitive. Run any I/O in the factory and cache the result.
    /// </remarks>
    public interface IPluginCacheService
    {
        /// <summary>
        /// Gets a cached value by key or creates it and stores it with a TTL.
        /// </summary>
        T GetOrCreate<T>(string key, TimeSpan ttl, Func<T> factory);

        /// <summary>
        /// Forces a cache refresh for a key by replacing the value from the factory.
        /// </summary>
        T Refresh<T>(string key, TimeSpan ttl, Func<T> factory);

        /// <summary>
        /// Removes a cache entry by key.
        /// </summary>
        bool Invalidate(string key);

        /// <summary>
        /// Clears all cache entries.
        /// </summary>
        void Clear();
    }
}
