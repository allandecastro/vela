using System;
using System.Threading;
using System.Threading.Tasks;
using Vela.Caching;
using Vela.Context;
using Vela.DI;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void GetOrCreate_WithManyDistinctKeys_KeepsPerKeyValuesAndBoundsLocks()
    {
        // Regression for the unbounded per-key lock pool: many distinct keys must still
        // resolve correctly (and not leak a lock per key). The striped pool serves them all.
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
        // Keys sharing a stripe must still serialize per-key creation: exactly one factory call.
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

        Assert.Same(first, second);
    }

    private static ILocalPluginContext CreateContext()
    {
        Mock<ILocalPluginContext> context = new Mock<ILocalPluginContext>();
        Mock<IPluginExecutionContext> executionContext = new Mock<IPluginExecutionContext>();
        Mock<IOrganizationService> organizationService = new Mock<IOrganizationService>();
        Mock<ITracingService> tracingService = new Mock<ITracingService>();

        context.SetupGet(c => c.PluginExecutionContext).Returns(executionContext.Object);
        context.SetupGet(c => c.UserService).Returns(organizationService.Object);
        context.SetupGet(c => c.TracingService).Returns(tracingService.Object);
        context.SetupGet(c => c.Logger).Returns(NullLogger.Instance);
        context.SetupGet(c => c.ManagedIdentityService).Returns((IManagedIdentityService?)null);
        context.Setup(c => c.CreateOrganizationService(It.IsAny<Guid?>())).Returns(organizationService.Object);

        return context.Object;
    }
}
