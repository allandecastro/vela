using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vela.Caching
{
    /// <summary>
    /// Global process-level cache service with TTL-based expiration and a bounded entry count.
    /// </summary>
    /// <remarks>
    /// Each slot folds a <see cref="Lazy{T}"/> in <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> mode,
    /// so a cache-miss factory runs exactly once per key under contention while unrelated keys never serialize.
    /// The synchronisation lives inside the slot, so it is reclaimed together with the entry on expiration,
    /// <see cref="Invalidate"/> or <see cref="Clear"/> - there is no separate lock registry to grow unbounded.
    /// <see cref="Lazy{T}"/> synchronises with plain monitors (the C# <c>lock</c> statement), which are fully
    /// supported under the Dataverse plugin sandbox's partial-trust permission set.
    /// <para>
    /// Because the cache is a static process-wide singleton reused across executions, expiry alone does not bound
    /// memory: expiry is lazy (an entry is dropped only when its own key is fetched again), so distinct short-lived
    /// keys would accumulate for the worker's lifetime. A <see cref="MaxEntries"/> cap bounds the count: when an
    /// insert pushes the count over the cap, a sweep first drops already-materialized expired entries, then evicts
    /// the oldest-inserted entries until the count is back at the cap. The sweep never forces a pending factory to
    /// run and never removes a fresher slot another thread has just installed for the same key.
    /// </para>
    /// </remarks>
    public sealed class PluginCacheService : IPluginCacheService
    {
        /// <summary>Default maximum number of live cache entries.</summary>
        public const int DefaultMaxEntries = 1024;

        private readonly ConcurrentDictionary<string, Slot> _cache =
            new ConcurrentDictionary<string, Slot>(StringComparer.Ordinal);

        private readonly int _maxEntries;
        private readonly Func<DateTimeOffset> _clock;
        private long _sequence;

        /// <summary>
        /// Initializes a new instance with the <see cref="DefaultMaxEntries"/> capacity.
        /// </summary>
        public PluginCacheService()
            : this(DefaultMaxEntries)
        {
        }

        /// <summary>
        /// Initializes a new instance with an explicit capacity.
        /// </summary>
        /// <param name="maxEntries">Maximum number of live entries (must be at least 1).</param>
        public PluginCacheService(int maxEntries)
            : this(maxEntries, () => DateTimeOffset.UtcNow)
        {
        }

        // Testability hook: the clock that stamps and checks expiry, so TTL tests advance time instead of sleeping.
        internal PluginCacheService(int maxEntries, Func<DateTimeOffset> clock)
        {
            if (maxEntries < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be at least 1.");
            }

            _maxEntries = maxEntries;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        // A cache on a test harness's clock (a DateTime as PluginPipeline.WithClock takes it; Unspecified means UTC).
        internal static PluginCacheService OnClock(Func<DateTime> utcNow)
            => new PluginCacheService(DefaultMaxEntries, () =>
            {
                DateTime now = utcNow();
                return new DateTimeOffset(now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : DateTime.SpecifyKind(now, DateTimeKind.Utc));
            });

        /// <summary>Gets the maximum number of live entries this cache retains.</summary>
        public int MaxEntries => _maxEntries;

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
                bool added = false;
                Slot slot = _cache.GetOrAdd(key, _ =>
                {
                    added = true;
                    return NewSlot(ttl, factory);
                });

                if (added)
                {
                    EvictIfNeeded();
                }

                CacheEntry entry;
                try
                {
                    // The factory runs at most once for this slot; every other caller blocks on the same Lazy and
                    // then observes the identical result.
                    entry = slot.Lazy.Value;
                }
                catch
                {
                    // ExecutionAndPublication caches a thrown exception for the slot's lifetime, so drop this exact
                    // slot on failure - the next caller rebuilds instead of replaying the failure forever.
                    RemoveSlot(key, slot);
                    throw;
                }

                if (entry.ExpiresAtUtc > _clock())
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

            Slot slot = NewSlot(ttl, factory);
            _cache[key] = slot;
            EvictIfNeeded();

            CacheEntry entry;
            try
            {
                entry = slot.Lazy.Value;
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

        // Removes every entry whose key starts with the prefix (one organization's scope). Each slot is removed only
        // while it is still the one enumerated, so a fresher slot installed concurrently survives.
        internal int RemoveByPrefix(string prefix)
        {
            int removed = 0;
            foreach (KeyValuePair<string, Slot> pair in _cache)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && RemoveSlot(pair.Key, pair.Value))
                {
                    removed++;
                }
            }

            return removed;
        }

        private Slot NewSlot<T>(TimeSpan ttl, Func<T> factory)
        {
            long sequence = Interlocked.Increment(ref _sequence);
            Lazy<CacheEntry> lazy = new Lazy<CacheEntry>(
                () => new CacheEntry(factory(), _clock().Add(ttl)),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return new Slot(lazy, sequence);
        }

        // Keep the live entry count at or below the cap. Called after an insert; a no-op while under the cap.
        private void EvictIfNeeded()
        {
            if (_cache.Count <= _maxEntries)
            {
                return;
            }

            // First pass: drop materialized entries that have already expired. Skip pending (not-yet-built) slots so
            // the sweep never forces a factory to run, and drop a slot whose factory threw (a poisoned Lazy).
            DateTimeOffset now = _clock();
            foreach (KeyValuePair<string, Slot> pair in _cache)
            {
                if (_cache.Count <= _maxEntries)
                {
                    return;
                }

                Lazy<CacheEntry> lazy = pair.Value.Lazy;
                if (!lazy.IsValueCreated)
                {
                    continue;
                }

                bool expired;
                try
                {
                    expired = lazy.Value.ExpiresAtUtc <= now;
                }
                catch
                {
                    expired = true;
                }

                if (expired)
                {
                    RemoveSlot(pair.Key, pair.Value);
                }
            }

            // Second pass: still over the cap -> evict the oldest-inserted slots (FIFO by creation sequence).
            if (_cache.Count <= _maxEntries)
            {
                return;
            }

            KeyValuePair<string, Slot>[] snapshot = _cache.ToArray();
            Array.Sort(snapshot, (a, b) => a.Value.Sequence.CompareTo(b.Value.Sequence));
            int toRemove = _cache.Count - _maxEntries;
            for (int i = 0; i < snapshot.Length && toRemove > 0; i++)
            {
                if (RemoveSlot(snapshot[i].Key, snapshot[i].Value))
                {
                    toRemove--;
                }
            }
        }

        // Remove the slot only when it is still the one we hold, so a concurrent Refresh/rebuild is never discarded.
        // net462's ConcurrentDictionary has no TryRemove(KeyValuePair) overload, so go through the ICollection one,
        // which removes only on a key AND value match (Slot compared by reference).
        private bool RemoveSlot(string key, Slot slot) =>
            ((ICollection<KeyValuePair<string, Slot>>)_cache)
                .Remove(new KeyValuePair<string, Slot>(key, slot));

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

        private sealed class Slot(Lazy<CacheEntry> lazy, long sequence)
        {
            public Lazy<CacheEntry> Lazy { get; } = lazy;

            public long Sequence { get; } = sequence;
        }

        private sealed class CacheEntry(object? value, DateTimeOffset expiresAtUtc)
        {
            public object? Value { get; } = value;

            public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        }
    }
}
