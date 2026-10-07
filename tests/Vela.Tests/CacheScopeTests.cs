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
    private static IPluginCacheService CacheFor(Guid organizationId) =>
        new PluginContextBuilder().WithOrganizationId(organizationId).Build().GetRequiredService<IPluginCacheService>();

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
}
