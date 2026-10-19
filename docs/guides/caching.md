# Caching (TTL + refresh)

Use the built-in `IPluginCacheService` for cross-execution, process-level caching:

```csharp
using Vela.Caching;

IPluginCacheService cache = ctx.GetRequiredService<IPluginCacheService>();

// Read-through cache with TTL
string configJson = cache.GetOrCreate(
    "tenant:feature-config",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.Service));

// Force refresh when needed
string refreshed = cache.Refresh(
    "tenant:feature-config",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.Service));
```

Supported operations:
- `GetOrCreate(...)`
- `Refresh(...)`
- `Invalidate(key)` and `Clear()`

**Scope.** The store is process-wide (it survives across executions on the same sandbox worker), but the
injected `IPluginCacheService` scopes every key to the executing organization: the same key holds one value per
organization, and `Invalidate`/`Refresh` never touch another organization's entry. `Clear()` drops this
organization's entries only (the framework's own cached environment variables and labels included). Within an
organization, keys are shared by every plugin and Custom API in the worker, so prefix them with your feature
name. A `new PluginCacheService()` you create yourself is not scoped.

The cache surface is synchronous only: the plugin pipeline runs synchronously, and the cache locks
with `Monitor` (the `lock` statement), the most clearly sandbox-supported synchronization primitive.

## What to cache

Cache only data that is the same for every user and changes rarely: configuration rows, reference tables,
metadata. A value read through `ctx.Service` is not one of them. `ctx.Service` acts as the current user, so a
`RetrieveMultiple` returns only the rows that user may read. Cache that result under a key such as
`account:related-contacts:{id}` and the next user in the organization gets the first user's privilege-filtered
answer, whatever their own access. To read shared data, use `ctx.SystemService` (SYSTEM sees every row) and make
sure the cached value never ends up in front of a user who could not read it. If the value really depends on the
user, add `ctx.UserId` to the key, and keep in mind that the entry stays stale for its whole TTL.

Recommended cache usage patterns:

1. **Read-through with TTL**: use `GetOrCreate(...)` for values read frequently and updated occasionally.
2. **Targeted refresh**: use `Refresh(key, ttl, factory)` when you know data changed now.
3. **Targeted invalidation**: use `Invalidate(key)` for one entry (safer than global clear).
4. **Organization reset**: use `Clear()` only for maintenance/debug scenarios.

Key design tips:

- Build stable keys from a feature prefix + business identifier (the organization scope is added for you).
- Keep TTL explicit per use case (short for volatile data, longer for reference data).
- Cache pure/read-only results; avoid caching mutable workflow state.

## Test a TTL

In the Vela.Testing harnesses the plug-in gets a cache of its own: one per `PluginPipeline`, kept across its
executions, and one per `PluginContext` builder, kept across the builder's `Execute` calls. A test never sees
another test's entries. It expires on the harness's clock, so a test moves time forward instead of sleeping:

```csharp
DateTime now = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
PluginPipeline pipeline = PluginPipeline.Empty().WithClock(() => now).Register<SettingsPlugin>();

pipeline.Service.Create(new Account { Name = "a" });   // loads and caches for 5 minutes
now = now.AddMinutes(6);
pipeline.Service.Create(new Account { Name = "b" });   // past the TTL: loads again
```

`PluginContext.For<T>()...WithClock(() => now)` does the same for one plugin: execute, move `now`, execute again
on the same builder.

## See also

- [Write a plugin](plugins.md) - `IPluginCacheService` is a built-in context service.
- [Dependency injection](dependency-injection.md).
