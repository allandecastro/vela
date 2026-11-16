---
_description: "A TTL cache scoped per organization: what is safe to cache and how to test a TTL"
---

# Caching

`IPluginCacheService` (namespace `Vela.Caching`) is a built-in in-memory cache with a time to live (TTL). It keeps a
value across executions on the same sandbox worker, so a plugin or Custom API does not reload configuration or
reference data on every run. This page covers the API, what is safe to cache, and how to test a TTL. The cache is
resolved like any built-in service: see [Dependency injection](dependency-injection.md).

## Read through the cache

```csharp
using Vela.Caching;

IPluginCacheService cache = ctx.GetRequiredService<IPluginCacheService>();

// Returns the cached value, or runs the factory and caches its result for 5 minutes.
string configJson = cache.GetOrCreate(
    "featureconfig:settings",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.SystemService));

// Replaces the cached value now, from the factory.
string refreshed = cache.Refresh(
    "featureconfig:settings",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.SystemService));
```

| Method | Use it to |
| --- | --- |
| `GetOrCreate(key, ttl, factory)` | Read a value that is read often and changes rarely. |
| `Refresh(key, ttl, factory)` | Reload a value you know has just changed. |
| `Invalidate(key)` | Drop one entry. Returns whether an entry was removed. |
| `Clear()` | Drop every entry of the organization (maintenance and debugging only). |

The TTL must be greater than zero. Concurrent callers of the same key wait for a single run of the factory, and a
factory that throws caches nothing (the next call runs it again). The
surface is synchronous only: the Dataverse pipeline runs synchronously, and the cache synchronizes with plain monitors
(the `lock` statement), the most clearly sandbox-supported primitive.

## Scope and lifetime

- **Per organization.** The store is process-wide, but the injected `IPluginCacheService` scopes every key to the
  executing organization: the same key holds one value per organization, and `Invalidate` and `Refresh` never touch
  another organization's entry. `Clear()` drops this organization's entries only, including the framework's own
  cached environment variables and labels. A `new PluginCacheService()` you create yourself is not scoped.
- **Shared within the organization.** Every plugin and Custom API on the worker sees the same keys, so prefix them
  with your feature name.
- **Best-effort.** An entry lives only as long as the sandbox worker process, and the store holds at most 1,024
  entries (`PluginCacheService.DefaultMaxEntries`): past that, expired entries and then the oldest ones are evicted.
  Treat the cache as a performance optimization: the factory must always be able to reload the value.

## What to cache

Cache only data that is the same for every user and changes rarely: configuration rows, reference tables,
metadata. A value read through `ctx.Service` is not one of them. `ctx.Service` acts as the current user, so a
`RetrieveMultiple` returns only the rows that user may read. Cache that result under a key such as
`account:related-contacts:{id}` and the next user in the organization gets the first user's privilege-filtered
answer, whatever their own access. To read shared data, use `ctx.SystemService` (SYSTEM sees every row) and make
sure the cached value never ends up in front of a user who could not read it. If the value really depends on the
user, add `ctx.UserId` to the key, and keep in mind that the entry stays stale for its whole TTL.

Key and TTL tips:

- Build stable keys from a feature prefix and a business identifier (the organization scope is added for you).
- Set the TTL per use case: short for volatile data, longer for reference data.
- Cache read-only results, not mutable workflow state.

## Test a TTL

In the Vela.Testing harnesses the plugin gets a cache of its own: one per `PluginPipeline`, kept across its
executions, and one per `PluginContext` builder, kept across the builder's `Execute` calls. A test never sees
another test's entries. The cache expires on the harness's clock, so a test moves time forward instead of sleeping:

```csharp
DateTime now = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
PluginPipeline pipeline = PluginPipeline.Empty().WithClock(() => now).Register<SettingsPlugin>();

pipeline.Service.Create(new Account { Name = "a" });   // loads and caches for 5 minutes
now = now.AddMinutes(6);
pipeline.Service.Create(new Account { Name = "b" });   // past the TTL: loads again
```

`PluginContext.For<T>()...WithClock(() => now)` does the same for one plugin: execute, move `now`, execute again
on the same builder. See [Testing with Vela.Testing](testing.md).

## See also

- [Dependency injection](dependency-injection.md): `IPluginCacheService` is a built-in service.
- [Environment variables](environment-variables.md) and [Localized messages](localization.md): built-in services
  that cache through it (`cacheFor`).
