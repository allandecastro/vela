using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vela.Caching
{
    /// <summary>
    /// Global process-level cache service with TTL-based expiration.
    /// </summary>
    /// <remarks>
    /// Each slot folds a <see cref="Lazy{T}"/> in <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> mode,
    /// so a cache-miss factory runs exactly once per key under contention while unrelated keys never serialize.
    /// The synchronisation lives inside the slot, so it is reclaimed together with the entry on expiration,
    /// <see cref="Invalidate"/> or <see cref="Clear"/> - there is no separate lock registry to grow unbounded.
    /// <see cref="Lazy{T}"/> synchronises with plain monitors (the C# <c>lock</c> statement), which are fully
    /// supported under the Dataverse plugin sandbox's partial-trust permission set.
    /// </remarks>
    public sealed class PluginCacheService : IPluginCacheService
    {
        private readonly ConcurrentDictionary<string, Lazy<CacheEntry>> _cache =
            new ConcurrentDictionary<string, Lazy<CacheEntry>>(StringComparer.Ordinal);

        public T GetOrCreate<T>(string key, TimeSpan ttl, Func<T> factory)
        {
            EnsureKey(key);
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            EnsureTtl(ttl);

            while (true)
            {
                Lazy<CacheEntry> slot = _cache.GetOrAdd(key, _ => NewSlot(ttl, factory));

                CacheEntry entry;
                try
                {
                    // The factory runs at most once for this slot; every other caller blocks on the same Lazy and
                    // then observes the identical result.
                    entry = slot.Value;
                }
                catch
                {
                    // ExecutionAndPublication caches a thrown exception for the slot's lifetime, so drop this exact
                    // slot on failure - the next caller rebuilds instead of replaying the failure forever.
                    RemoveSlot(key, slot);
                    throw;
                }

                if (entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    return Cast<T>(entry, key);
                }

                // Expired: remove this exact slot (never a fresher one another thread may have installed) and retry.
                RemoveSlot(key, slot);
            }
        }

        public T Refresh<T>(string key, TimeSpan ttl, Func<T> factory)
        {
            EnsureKey(key);
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            EnsureTtl(ttl);

            Lazy<CacheEntry> slot = NewSlot(ttl, factory);
            _cache[key] = slot;

            CacheEntry entry;
            try
            {
                entry = slot.Value;
            }
            catch
            {
                RemoveSlot(key, slot);
                throw;
            }

            return Cast<T>(entry, key);
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

        private static Lazy<CacheEntry> NewSlot<T>(TimeSpan ttl, Func<T> factory) =>
            new Lazy<CacheEntry>(
                () => new CacheEntry(factory(), DateTimeOffset.UtcNow.Add(ttl)),
                LazyThreadSafetyMode.ExecutionAndPublication);

        // Remove the slot only when it is still the one we hold, so a concurrent Refresh/rebuild is never discarded.
        // net462's ConcurrentDictionary has no TryRemove(KeyValuePair) overload, so go through the ICollection one,
        // which removes only on a key AND value match (Lazy compared by reference).
        private void RemoveSlot(string key, Lazy<CacheEntry> slot) =>
            ((ICollection<KeyValuePair<string, Lazy<CacheEntry>>>)_cache)
                .Remove(new KeyValuePair<string, Lazy<CacheEntry>>(key, slot));

        private static T Cast<T>(CacheEntry entry, string key)
        {
            if (entry.Value is null)
            {
                // A factory may legitimately cache a null (e.g. a negative "not found" lookup held for its TTL).
                // Return it as-is for a reference/nullable T; only a value-type T over a stored null is a mismatch.
                if (default(T) is null)
                {
                    return default!;
                }
            }
            else if (entry.Value is T typedValue)
            {
                return typedValue;
            }

            throw new InvalidOperationException(
                $"Cache key '{key}' contains '{entry.Value?.GetType().FullName ?? "null"}' but requested '{typeof(T).FullName}'.");
        }

        private static void EnsureKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentNullException(nameof(key));
            }
        }

        private static void EnsureTtl(TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be greater than zero.");
            }
        }

        private sealed class CacheEntry(object? value, DateTimeOffset expiresAtUtc)
        {
            public object? Value { get; } = value;

            public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        }
    }
}
