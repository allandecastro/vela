using System;
using Vela.Caching;
using Xunit;

namespace Vela.Tests;

// Pins the edges of PluginCacheService that the behavioural suite leaves open: argument checks and messages, the
// mismatch message, and the exact shape of the over-cap sweep (FIFO order, stop at the cap, pending slots untouched).
public sealed class PluginCacheServiceMutationTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public void Constructor_AcceptsACapacityOfOne_AndKeepsOnlyTheLatestEntry()
    {
        PluginCacheService cache = new PluginCacheService(1);
        int rebuilds = 0;

        cache.GetOrCreate("a", Ttl, () => 1);
        cache.GetOrCreate("b", Ttl, () => 2);

        Assert.Equal(1, cache.MaxEntries);
        Assert.Equal(2, cache.GetOrCreate("b", Ttl, () => { rebuilds++; return -1; }));
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public void Constructor_ExplainsANonPositiveCapacity()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new PluginCacheService(0));

        Assert.Equal("maxEntries", exception.ParamName);
        Assert.StartsWith("maxEntries must be at least 1.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOrCreate_ExplainsANonPositiveTtl()
    {
        PluginCacheService cache = new PluginCacheService();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            cache.GetOrCreate("ttl:key", TimeSpan.Zero, () => 1));

        Assert.Equal("ttl", exception.ParamName);
        Assert.StartsWith("TTL must be greater than zero.", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalidate_RejectsABlankKey(string key)
    {
        PluginCacheService cache = new PluginCacheService();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => cache.Invalidate(key));

        Assert.Equal("key", exception.ParamName);
    }

    [Fact]
    public void GetOrCreate_TypeMismatch_NamesTheStoredAndRequestedTypes()
    {
        PluginCacheService cache = new PluginCacheService();
        cache.GetOrCreate("shared:key", Ttl, () => "value");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrCreate<int>("shared:key", Ttl, () => 5));

        Assert.Equal(
            "Cache key 'shared:key' contains 'System.String' but requested 'System.Int32'.",
            exception.Message);
    }

    [Fact]
    public void GetOrCreate_ACachedNullRequestedAsAValueType_NamesNullAsTheStoredType()
    {
        PluginCacheService cache = new PluginCacheService();
        cache.GetOrCreate<string?>("negative:key", Ttl, () => null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrCreate<int>("negative:key", Ttl, () => 5));

        Assert.Equal(
            "Cache key 'negative:key' contains 'null' but requested 'System.Int32'.",
            exception.Message);
    }

    [Fact]
    public void GetOrCreate_OverTheCap_KeepsExactlyTheMostRecentlyInsertedEntries()
    {
        // Eviction is FIFO by insertion order, whatever order the dictionary enumerates its keys in.
        PluginCacheService cache = new PluginCacheService(maxEntries: 4);
        for (int i = 0; i < 40; i++)
        {
            int captured = i;
            cache.GetOrCreate("entry-" + captured, Ttl, () => captured);
        }

        int rebuilds = 0;
        for (int i = 36; i < 40; i++)
        {
            Assert.Equal(i, cache.GetOrCreate("entry-" + i, Ttl, () => { rebuilds++; return -1; }));
        }

        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public void GetOrCreate_ExpiredSweep_StopsOnceBackAtTheCap()
    {
        // The sweep drops expired entries only until the count is back at the cap; the clock is rewound afterwards
        // so the expired entry the sweep left behind is observable as a cache hit.
        DateTimeOffset now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        PluginCacheService cache = new PluginCacheService(3, () => now);

        cache.GetOrCreate("live:a", Ttl, () => 1);
        cache.GetOrCreate("stale:x", TimeSpan.FromSeconds(10), () => 2);
        cache.GetOrCreate("stale:y", TimeSpan.FromSeconds(10), () => 3);
        now = now.AddSeconds(10); // both stale entries are now expired, still resident

        cache.GetOrCreate("live:b", Ttl, () => 4); // count 4 > 3: one expired entry is enough to get back to 3

        now = now.AddSeconds(-10);
        int rebuilds = 0;
        cache.GetOrCreate("stale:x", TimeSpan.FromSeconds(10), () => { rebuilds++; return -2; });
        cache.GetOrCreate("stale:y", TimeSpan.FromSeconds(10), () => { rebuilds++; return -3; });

        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void GetOrCreate_EnforcesTheCap_BeforeTheNewEntrysFactoryRuns()
    {
        // The sweep never forces a pending factory: the new entry's factory runs only after the oldest entry is
        // evicted, so a lookup of that entry from inside the factory misses.
        PluginCacheService cache = new PluginCacheService(1);
        cache.GetOrCreate("old", Ttl, () => 1);
        bool oldRebuilt = false;

        int value = cache.GetOrCreate("new", Ttl, () =>
        {
            cache.GetOrCreate("old", Ttl, () => { oldRebuilt = true; return -1; });
            return 2;
        });

        Assert.Equal(2, value);
        Assert.True(oldRebuilt);
    }

    [Fact]
    public void GetOrCreate_SweepsAnEntryExactlyAtItsExpiry()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        PluginCacheService cache = new PluginCacheService(2, () => now);

        cache.GetOrCreate("live:a", Ttl, () => 1);
        cache.GetOrCreate("stale:b", TimeSpan.FromSeconds(30), () => 2);
        now = now.AddSeconds(30); // ExpiresAt == now: expired

        cache.GetOrCreate("live:c", Ttl, () => 3);

        int rebuilds = 0;
        Assert.Equal(1, cache.GetOrCreate("live:a", Ttl, () => { rebuilds++; return -1; }));
        Assert.Equal(0, rebuilds); // the expired entry went, not the oldest live one
    }
}
