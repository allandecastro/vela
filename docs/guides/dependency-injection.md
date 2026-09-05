# Dependency injection (lightweight DI)

Vela ships a small **DI / service-locator hybrid**: compose services in `ConfigureServices`, resolve them
in your handler with `ctx.GetRequiredService<T>()`. It keeps handlers small and testable without pulling a
full container into the plugin sandbox.

Override `ConfigureServices` in your plugin (the [plugin](plugins.md) base classes call it once per
execution, before your handler runs):

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    services.AddScoped<IPricingService>(r => new PricingService(context.UserService, r.GetRequiredService<IPluginCacheService>()));
    services.AddTransient<IAuditWriter>(_ => new AuditWriter(context.UserService));
}
```

Resolve registered services from a handler:

```csharp
protected override void Execute(ILocalPluginContext ctx)
{
    var target = ctx.GetTarget<Account>();
    var pricing = ctx.GetRequiredService<IPricingService>();
    target["creditonhold"] = pricing.IsOverLimit(target);
}
```

## Lifetimes

A plugin runs in a fresh instance per pipeline invocation, so there is **no singleton lifetime** - the two
lifetimes are scoped to a single execution:

| Method | Lifetime | Use for |
| --- | --- | --- |
| `AddScoped<T>(factory)` | **one instance per execution** - resolved once, then reused within the same invocation | services with state or setup cost you want to share across the handler |
| `AddTransient<T>(factory)` | **a new instance every resolve** | cheap, stateless helpers |

The factory receives the resolver (`IPluginServiceResolver`), so a service can depend on other registered
services: `r => new PricingService(context.UserService, r.GetRequiredService<IPluginCacheService>())`.

## Built-in registrations

These are always available to resolve without registering them yourself:

`ILocalPluginContext`, `IPluginExecutionContext`, `IOrganizationService`, `ITracingService`, `ILogger`,
`IPluginCacheService`.

> [!TIP]
> `GetRequiredService<T>()` throws if the service isn't registered; use `GetService(typeof(T))` (returns
> `null`) when a dependency is optional.

## Why

Smaller handlers, cleaner separation of domain vs infrastructure, and - because you inject the
`IOrganizationService` and friends - the same handler runs unchanged against the in-memory
[test harness](testing.md), where you register fakes in `ConfigureServices`.
