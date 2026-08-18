# Caching (TTL + refresh)

Use the built-in `IPluginCacheService` for cross-execution, process-level caching:

```csharp
using Vela.Caching;

IPluginCacheService cache = ctx.GetRequiredService<IPluginCacheService>();

// Read-through cache with TTL
string configJson = cache.GetOrCreate(
    "tenant:feature-config",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.UserService));

// Force refresh when needed
string refreshed = cache.Refresh(
    "tenant:feature-config",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.UserService));
```

Supported operations:
- `GetOrCreate(...)`
- `Refresh(...)`
- `Invalidate(key)` and `Clear()`

The cache surface is synchronous only: the plugin pipeline runs synchronously, and the cache locks
with `Monitor` (the `lock` statement), the most clearly sandbox-supported synchronization primitive.

Recommended cache usage patterns:

1. **Read-through with TTL**: use `GetOrCreate(...)` for values read frequently and updated occasionally.
2. **Targeted refresh**: use `Refresh(key, ttl, factory)` when you know data changed now.
3. **Targeted invalidation**: use `Invalidate(key)` for one entry (safer than global clear).
4. **Global reset**: use `Clear()` only for maintenance/debug scenarios.

Key design tips:

- Build stable keys with tenant/org + business identifier.
- Keep TTL explicit per use case (short for volatile data, longer for reference data).
- Cache pure/read-only results; avoid caching mutable workflow state.
