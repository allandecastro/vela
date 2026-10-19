using System;
using Vela.Caching;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// The injected IPluginCacheService is process-wide but scoped by organization: a user key cannot collide across
// organizations, and Clear() drops only the executing organization's entries.
public sealed class CacheScopeTests
{
    // One builder per test: its cache stands for the worker process's, shared by every execution it builds.
    private readonly PluginContextBuilder _builder = new PluginContextBuilder();

    private IPluginCacheService CacheFor(Guid organizationId) =>
        _builder.WithOrganizationId(organizationId).Build().GetRequiredService<IPluginCacheService>();

    [Fact]
    public void The_same_user_key_holds_one_value_per_organization()
    {
        string key = "scope-test:" + Guid.NewGuid().ToString("N");
        IPluginCacheService orgA = CacheFor(Guid.NewGuid());
        IPluginCacheService orgB = CacheFor(Guid.NewGuid());

        Assert.Equal("A", orgA.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "A"));
        Assert.Equal("B", orgB.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "B")); // not A's value
        Assert.Equal("A", orgA.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "rebuilt"));
    }

    [Fact]
    public void A_later_execution_in_the_same_organization_reuses_the_entry()
    {
        Guid org = Guid.NewGuid();
        string key = "scope-test:" + Guid.NewGuid().ToString("N");

        CacheFor(org).GetOrCreate(key, TimeSpan.FromMinutes(5), () => 1);

        Assert.Equal(1, CacheFor(org).GetOrCreate(key, TimeSpan.FromMinutes(5), () => 2));
    }

    [Fact]
    public void Invalidate_and_Refresh_stay_inside_the_organization()
    {
        string key = "scope-test:" + Guid.NewGuid().ToString("N");
        IPluginCacheService orgA = CacheFor(Guid.NewGuid());
        IPluginCacheService orgB = CacheFor(Guid.NewGuid());
        orgA.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "A");
        orgB.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "B");

        Assert.True(orgB.Invalidate(key));
        Assert.Equal("A", orgA.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "rebuilt"));

        Assert.Equal("A2", orgA.Refresh(key, TimeSpan.FromMinutes(5), () => "A2"));
        Assert.Equal("B2", orgB.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "B2"));
    }

    [Fact]
    public void Clear_drops_only_the_executing_organizations_entries()
    {
        string key = "scope-test:" + Guid.NewGuid().ToString("N");
        IPluginCacheService orgA = CacheFor(Guid.NewGuid());
        IPluginCacheService orgB = CacheFor(Guid.NewGuid());
        orgA.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "A");
        orgB.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "B");

        orgA.Clear();

        Assert.Equal("A-rebuilt", orgA.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "A-rebuilt"));
        Assert.Equal("B", orgB.GetOrCreate(key, TimeSpan.FromMinutes(5), () => "B-rebuilt"));
    }

    [Fact]
    public void A_blank_key_is_still_rejected()
    {
        IPluginCacheService cache = CacheFor(Guid.NewGuid());

        Assert.Throws<ArgumentNullException>(() => cache.GetOrCreate(" ", TimeSpan.FromMinutes(1), () => 1));
        Assert.Throws<ArgumentNullException>(() => cache.Invalidate(""));
    }

    [Fact]
    public void RemoveByPrefix_removes_only_matching_keys()
    {
        PluginCacheService cache = new PluginCacheService();
        cache.GetOrCreate("org:a:1", TimeSpan.FromMinutes(1), () => 1);
        cache.GetOrCreate("org:a:2", TimeSpan.FromMinutes(1), () => 2);
        cache.GetOrCreate("org:b:1", TimeSpan.FromMinutes(1), () => 3);

        Assert.Equal(2, cache.RemoveByPrefix("org:a:"));
        Assert.Equal(3, cache.GetOrCreate("org:b:1", TimeSpan.FromMinutes(1), () => 0));
        Assert.Equal(9, cache.GetOrCreate("org:a:1", TimeSpan.FromMinutes(1), () => 9));
    }

    // On Dataverse the service provider serves no PluginCacheService, so every execution views the process-wide cache.
    [Fact]
    public void Without_a_harness_cache_every_execution_views_the_process_wide_cache()
    {
        Guid org = Guid.NewGuid();
        string key = "scope-test:" + Guid.NewGuid().ToString("N");
        IPluginCacheService PlatformCache() => new LocalPluginContext(
            new WithoutHarnessCache(new PluginContextBuilder().WithOrganizationId(org).BuildServiceProvider()))
            .GetRequiredService<IPluginCacheService>();

        PlatformCache().GetOrCreate(key, TimeSpan.FromMinutes(5), () => 1);

        Assert.Equal(1, PlatformCache().GetOrCreate(key, TimeSpan.FromMinutes(5), () => 2));
        PlatformCache().Invalidate(key);
    }

    // A harness clock in local or unspecified time is read as the same instant as UTC would be, so a TTL is
    // measured the same way whichever DateTimeKind the test's clock returns.
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void A_harness_cache_expires_on_its_clock_whatever_its_kind(DateTimeKind kind)
    {
        DateTime utc = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        DateTime now = kind == DateTimeKind.Local ? utc.ToLocalTime() : DateTime.SpecifyKind(utc, kind);
        PluginCacheService cache = PluginCacheService.OnClock(() => now);

        cache.GetOrCreate("k", TimeSpan.FromMinutes(5), () => 1);
        now = now.AddMinutes(4);
        Assert.Equal(1, cache.GetOrCreate("k", TimeSpan.FromMinutes(5), () => 2));
        now = now.AddMinutes(2);
        Assert.Equal(3, cache.GetOrCreate("k", TimeSpan.FromMinutes(5), () => 3));
    }

    private sealed class WithoutHarnessCache(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(PluginCacheService) ? null : inner.GetService(serviceType);
    }
}
