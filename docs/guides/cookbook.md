# Cookbook (common recipes)

1. **Single-step update plugin**
   - `RegisterPluginSteps()` + `RegisterSteps().OnUpdate().Stage(...).Mode(...).FilteringAttributes(...)`
2. **Multi-step router plugin**
   - `RegisterPluginSteps()` + repeated `RegisterSteps().On...().Handler(...)`
3. **Bound Custom API**
   - `RegisterCustomApi("...", Handler).AsAction().BoundTo<TEntity>()`
4. **Unbound typed Custom API**
   - `RegisterCustomApi("...").Request(...).Response(...)` + typed `Execute(...)`
5. **DI in handlers**
   - register in `ConfigureServices(...)`, consume via `ctx.GetRequiredService<T>()`, cache shared data with `IPluginCacheService`
