using System;
using System.Collections.Concurrent;

namespace Vela.Caching
{
    /// <summary>
    /// Global process-level cache service with TTL-based expiration.
    /// </summary>
    /// <remarks>
    /// Synchronisation uses plain <see cref="object"/> monitors (the C# <c>lock</c> statement) only,
    /// which are fully supported under the Dataverse plugin sandbox's partial-trust permission set.
    /// </remarks>
    public sealed class PluginCacheService : IPluginCacheService
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        // One monitor per cache key, so a cache-miss factory runs once per key (double-checked below) without
        // serializing unrelated keys. GetOrAdd hands every thread for a key the same object, so it is a correct
        // per-key lock. Lock objects are never removed (that would let two threads lock different objects for the
        // same key) - they are tiny and bounded by the number of distinct keys, which is the caller's concern.
        private readonly ConcurrentDictionary<string, object> _keyLocks = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        public T GetOrCreate<T>(string key, TimeSpan ttl, Func<T> factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            if (TryGetValue<T>(key, out T cached))
            {
                return cached;
            }

            lock (GetKeyLock(key))
            {
                // Double-check inside the lock: another thread may have populated the key while we waited.
                if (TryGetValue<T>(key, out cached))
                {
                    return cached;
                }

                T value = factory();
                SetValue(key, ttl, value);
                return value;
            }
        }

        public T Refresh<T>(string key, TimeSpan ttl, Func<T> factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            lock (GetKeyLock(key))
            {
                T value = factory();
                SetValue(key, ttl, value);
                return value;
            }
        }

        public bool Invalidate(string key)
        {
            EnsureKey(key);
            return _cache.TryRemove(key, out _);
        }

        public void Clear()
        {
            _cache.Clear();
        }

        private bool TryGetValue<T>(string key, out T value)
        {
            EnsureKey(key);
            if (!_cache.TryGetValue(key, out CacheEntry entry))
            {
                value = default!;
                return false;
            }

            if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _cache.TryRemove(key, out _);
                value = default!;
                return false;
            }

            if (entry.Value is not T typedValue)
            {
                throw new InvalidOperationException(
                    $"Cache key '{key}' contains '{entry.Value?.GetType().FullName ?? "null"}' but requested '{typeof(T).FullName}'.");
            }

            value = typedValue;
            return true;
        }

        private void SetValue<T>(string key, TimeSpan ttl, T value)
        {
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be greater than zero.");
            }

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(ttl);
            _cache[key] = new CacheEntry(value, expiresAt);
        }

        private object GetKeyLock(string key)
        {
            EnsureKey(key);

            // Same object for every thread that asks for this key (the factory may create a throwaway under
            // contention, but only the stored one is returned), so it is a correct per-key monitor.
            return _keyLocks.GetOrAdd(key, _ => new object());
        }

        private static void EnsureKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentNullException(nameof(key));
            }
        }

        private sealed class CacheEntry(object? value, DateTimeOffset expiresAtUtc)
        {
            public object? Value { get; } = value;

            public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        }
    }
}
