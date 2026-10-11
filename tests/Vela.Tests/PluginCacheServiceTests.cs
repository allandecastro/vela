using System;
using System.Threading;
using System.Threading.Tasks;
using Vela.Caching;
using Vela.Context;
using Vela.DI;
using Microsoft.Xrm.Sdk;
using Moq;
using Xunit;

namespace Vela.Tests;

public sealed class PluginCacheServiceTests
{
    [Fact]
    public void GetOrCreate_CachesValue_UntilExpiration()
    {
        PluginCacheService cache = new PluginCacheService();
        int calls = 0;

        int first = cache.GetOrCreate("account:score", TimeSpan.FromMilliseconds(40), () =>
        {
            calls++;
            return 42;
        });

        int second = cache.GetOrCreate("account:score", TimeSpan.FromMilliseconds(40), () =>
        {
            calls++;
            return 99;
        });

        Thread.Sleep(70);

        int third = cache.GetOrCreate("account:score", TimeSpan.FromMilliseconds(40), () =>
        {
            calls++;
            return 100;
        });

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Equal(100, third);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void GetOrCreate_CachesNull_WithoutThrowing()
    {
        PluginCacheService cache = new PluginCacheService();
        int calls = 0;

        // A factory may legitimately return null (a negative "not found" lookup cached for its TTL).
        string? first = cache.GetOrCreate<string?>("account:missing", TimeSpan.FromMinutes(1), () => { calls++; return null; });
        string? second = cache.GetOrCreate<string?>("account:missing", TimeSpan.FromMinutes(1), () => { calls++; return "rebuilt"; });

        Assert.Null(first);
        Assert.Null(second); // the cached null is returned, not re-evaluated and not thrown on
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Refresh_ReplacesExistingValue()
    {
        PluginCacheService cache = new PluginCacheService();
        int calls = 0;

        cache.GetOrCreate("tenant:config", TimeSpan.FromMinutes(1), () =>
        {
            calls++;
            return "v1";
        });

        string refreshed = cache.Refresh("tenant:config", TimeSpan.FromMinutes(1), () =>
        {
            calls++;
            return "v2";
        });

        string resolved = cache.GetOrCreate("tenant:config", TimeSpan.FromMinutes(1), () =>
        {
            calls++;
            return "v3";
        });

        Assert.Equal("v2", refreshed);
        Assert.Equal("v2", resolved);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        PluginCacheService cache = new PluginCacheService();
        int calls = 0;

        cache.GetOrCreate("feature:flags", TimeSpan.FromMinutes(1), () =>
        {
            calls++;
            return "A";
        });

        bool removed = cache.Invalidate("feature:flags");
        string value = cache.GetOrCreate("feature:flags", TimeSpan.FromMinutes(1), () =>
        {
            calls++;
            return "B";
        });

        Assert.True(removed);
        Assert.Equal("B", value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Invalidate_ReturnsFalse_WhenEntryMissing()
    {
        PluginCacheService cache = new PluginCacheService();

        bool removed = cache.Invalidate("missing:key");

        Assert.False(removed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetOrCreate_ThrowsOnInvalidKey(string? key)
    {
        PluginCacheService cache = new PluginCacheService();

        Assert.Throws<ArgumentNullException>(() => cache.GetOrCreate(key!, TimeSpan.FromMinutes(1), () => 1));
    }

    [Fact]
    public void GetOrCreate_ThrowsOnNonPositiveTtl()
    {
        PluginCacheService cache = new PluginCacheService();

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetOrCreate("ttl:key", TimeSpan.Zero, () => 1));
    }

    [Fact]
    public void GetOrCreate_ThrowsOnNullFactory()
    {
        PluginCacheService cache = new PluginCacheService();

        Assert.Throws<ArgumentNullException>(() => cache.GetOrCreate<int>("factory:key", TimeSpan.FromMinutes(1), null!));
    }

    [Fact]
    public void GetOrCreate_ThrowsOnTypeMismatchForSameKey()
    {
        PluginCacheService cache = new PluginCacheService();
        cache.GetOrCreate("shared:key", TimeSpan.FromMinutes(1), () => "value");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrCreate<int>("shared:key", TimeSpan.FromMinutes(1), () => 5));

        Assert.Contains("contains", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetOrCreate_DoesNotCacheFailedFactory()
    {
        // A failed factory must not poison the slot: the thrown Lazy is evicted, so a later call rebuilds.
        PluginCacheService cache = new PluginCacheService();
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrCreate<int>("token:key", TimeSpan.FromMinutes(1), () =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            }));

        int recovered = cache.GetOrCreate("token:key", TimeSpan.FromMinutes(1), () =>
        {
            calls++;
            return 123;
        });

        Assert.Equal(123, recovered);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        PluginCacheService cache = new PluginCacheService();
        cache.GetOrCreate("clear:a", TimeSpan.FromMinutes(1), () => 1);
        cache.GetOrCreate("clear:b", TimeSpan.FromMinutes(1), () => 2);

        cache.Clear();

        Assert.False(cache.Invalidate("clear:a"));
        Assert.False(cache.Invalidate("clear:b"));
    }

    [Fact]
    public void GetOrCreate_WithManyDistinctKeys_KeepsPerKeyValues()
    {
        // Many distinct keys each resolve and cache their own value independently (each key owns its Lazy slot;
        // the slot is reclaimed with the entry on expiry/invalidate, so nothing accumulates beyond live entries).
        PluginCacheService cache = new PluginCacheService();

        for (int i = 0; i < 500; i++)
        {
            int captured = i;
            int value = cache.GetOrCreate($"key:{captured}", TimeSpan.FromMinutes(1), () => captured);
            Assert.Equal(captured, value);
        }

        // Re-reading every key must hit the cache (no factory invocation increments the value).
        for (int i = 0; i < 500; i++)
        {
            int value = cache.GetOrCreate($"key:{i}", TimeSpan.FromMinutes(1), () => -1);
            Assert.Equal(i, value);
        }
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentAccessToSameKey_InvokesFactoryOnce()
    {
        // Concurrent callers of the same key block on the slot's Lazy, so the factory runs exactly once.
        PluginCacheService cache = new PluginCacheService();
        int calls = 0;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);

        Task<int>[] tasks = new Task<int>[16];
        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                start.Wait();
                return cache.GetOrCreate("contended:key", TimeSpan.FromMinutes(1), () =>
                {
                    Interlocked.Increment(ref calls);
                    return 7;
                });
            });
        }

        start.Set();
        int[] results = await Task.WhenAll(tasks);

        Assert.All(results, value => Assert.Equal(7, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CreateDefault_ResolvesSameGlobalCacheAcrossExecutions()
    {
        ILocalPluginContext firstContext = CreateContext();
        ILocalPluginContext secondContext = CreateContext();

        IPluginCacheService first = SimpleServiceResolver.CreateDefault(firstContext).GetRequiredService<IPluginCacheService>();
        IPluginCacheService second = SimpleServiceResolver.CreateDefault(secondContext).GetRequiredService<IPluginCacheService>();

        // Each execution gets its own organization-scoped view, backed by the one process-wide store: an entry made
        // in one execution is seen by the next one in the same organization.
        string key = "global:" + Guid.NewGuid().ToString("N");
        first.GetOrCreate(key, TimeSpan.FromMinutes(1), () => 7);
        Assert.Equal(7, second.GetOrCreate(key, TimeSpan.FromMinutes(1), () => 8));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginCacheService(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginCacheService(-1));
    }

    [Fact]
    public void GetOrCreate_BoundsEntryCount_EvictingOldestInsertedEntries()
    {
        // A small cap makes the bound observable: inserting distinct keys past the cap evicts the oldest-inserted
        // ones, so the live count never exceeds the cap and the earliest keys have to be rebuilt on next access.
        PluginCacheService cache = new PluginCacheService(maxEntries: 3);
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        for (int i = 0; i < 10; i++)
        {
            int captured = i;
            cache.GetOrCreate("key:" + captured, ttl, () => captured);
        }

        // The three most recently inserted keys are still cached (factory not re-run).
        int rebuilds = 0;
        for (int i = 7; i < 10; i++)
        {
            int captured = i;
            int value = cache.GetOrCreate("key:" + captured, ttl, () => { rebuilds++; return -1; });
            Assert.Equal(captured, value);
        }

        Assert.Equal(0, rebuilds);

        // The oldest key was evicted, so its factory runs again.
        int oldRebuilds = 0;
        cache.GetOrCreate("key:0", ttl, () => { oldRebuilds++; return 0; });
        Assert.Equal(1, oldRebuilds);
    }

    [Fact]
    public void GetOrCreate_SweepsExpiredEntries_BeforeEvictingLiveOnes()
    {
        // With expired entries present, the over-cap sweep drops those first, so the live entries survive.
        PluginCacheService cache = new PluginCacheService(maxEntries: 3);

        cache.GetOrCreate("live:a", TimeSpan.FromMinutes(5), () => 1);
        cache.GetOrCreate("live:b", TimeSpan.FromMinutes(5), () => 2);
        cache.GetOrCreate("stale:a", TimeSpan.FromMilliseconds(20), () => 3);
        Thread.Sleep(50); // stale:a is now expired but still resident (lazy expiry)

        // This insert pushes count over the cap; the sweep should drop the expired stale:a, sparing the live ones.
        cache.GetOrCreate("live:c", TimeSpan.FromMinutes(5), () => 4);

        int rebuilds = 0;
        Assert.Equal(1, cache.GetOrCreate("live:a", TimeSpan.FromMinutes(5), () => { rebuilds++; return -1; }));
        Assert.Equal(2, cache.GetOrCreate("live:b", TimeSpan.FromMinutes(5), () => { rebuilds++; return -1; }));
        Assert.Equal(4, cache.GetOrCreate("live:c", TimeSpan.FromMinutes(5), () => { rebuilds++; return -1; }));
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public void MaxEntries_DefaultsToTheDocumentedConstant()
    {
        Assert.Equal(PluginCacheService.DefaultMaxEntries, new PluginCacheService().MaxEntries);
        Assert.Equal(7, new PluginCacheService(7).MaxEntries);
    }

    private static ILocalPluginContext CreateContext()
    {
        Mock<ILocalPluginContext> context = new Mock<ILocalPluginContext>();
        Mock<IPluginExecutionContext> executionContext = new Mock<IPluginExecutionContext>();
        Mock<IOrganizationService> organizationService = new Mock<IOrganizationService>();
        Mock<ITracingService> tracingService = new Mock<ITracingService>();

        context.SetupGet(c => c.PluginExecutionContext).Returns(executionContext.Object);
        context.SetupGet(c => c.Service).Returns(organizationService.Object);
        context.SetupGet(c => c.TracingService).Returns(tracingService.Object);
        context.SetupGet(c => c.Logger).Returns(NullPluginTelemetryLogger.Instance);
        context.SetupGet(c => c.ManagedIdentityService).Returns((IManagedIdentityService?)null);

        return context.Object;
    }
}
