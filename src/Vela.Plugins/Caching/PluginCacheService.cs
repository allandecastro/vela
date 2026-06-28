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
        // Number of stripes in the lock pool. Fixed-size by design: bounds memory so monitors never
        // accumulate per distinct key, and no lock object is ever removed while it may be held.
        private const int LockStripeCount = 64;

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        // Striped lock pool: keys are mapped onto a fixed set of monitor objects by hash. Two distinct
        // keys may share a stripe (benign extra serialization) but the pool can never grow unbounded.
        // This static is shared across concurrent sandbox executions, so the pool is immutable after
        // construction and each element is only ever used as a 'lock' target.
        private readonly object[] _keyLocks = CreateLockStripes(LockStripeCount);

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

            // Map the key onto a fixed stripe. Mask off the sign bit so the modulo stays non-negative.
            int index = (StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % _keyLocks.Length;
            return _keyLocks[index];
        }

        private static object[] CreateLockStripes(int count)
        {
            object[] stripes = new object[count];
            for (int i = 0; i < count; i++)
            {
                stripes[i] = new object();
            }

            return stripes;
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
