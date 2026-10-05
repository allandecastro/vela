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
private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
{
    var target = ctx.Request.Target;
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
`IPluginCacheService`, `Func<Guid?, IOrganizationService>`.

> [!TIP]
> `GetRequiredService<T>()` throws if the service isn't registered; use `GetService(typeof(T))` (returns
> `null`) when a dependency is optional.

## System vs user calls in a service

The built-in `IOrganizationService` is the **initiating user's** service: calls run with the caller's
privileges (so a service that reads or writes on the user's behalf respects their security role). When a
service needs **elevated / system** access instead, do not reach for a second `IOrganizationService`
registration - there is only one `IOrganizationService` token and it is the user. Inject
**`ILocalPluginContext`** and pick the scope you need from it:

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    // A service that must bypass the caller's privileges: give it the context, not a bare service.
    services.AddScoped<IQuotaEnforcer>(r => new QuotaEnforcer(r.GetRequiredService<ILocalPluginContext>()));
}

public sealed class QuotaEnforcer(ILocalPluginContext ctx) : IQuotaEnforcer
{
    public void Enforce(EntityReference account)
        => ctx.SystemService.Update(/* runs as SYSTEM, ignores the caller's role */ ...);
}
```

`ctx` exposes every scope: `ctx.UserService` (initiating user), `ctx.CallingUserService` (the step's
"run in user's context" user), `ctx.SystemService` (SYSTEM), and `ctx.AsSystem()` / `ctx.AsUser(id)` /
`ctx.CreateOrganizationService(userId)` (a specific user, or SYSTEM when the id is `null`). The same id-based
factory is injectable directly as `Func<Guid?, IOrganizationService>` (pass `null` for SYSTEM) when a service
needs to switch users at call time rather than once at construction.

The rule of thumb: inject a bare `IOrganizationService` for ordinary user-scoped work; inject
`ILocalPluginContext` (or the `Func<Guid?, IOrganizationService>` factory) when the service needs a scope other
than the caller. Injecting the context keeps the choice explicit at the call site instead of hiding an elevated
service behind the same `IOrganizationService` type.

## Why

Smaller handlers, cleaner separation of domain vs infrastructure, and - because you inject the
`IOrganizationService` and friends - the same handler runs unchanged against the in-memory
[test harness](testing.md), where you register fakes in `ConfigureServices`.
