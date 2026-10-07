# Dependency injection (lightweight DI)

Vela ships a small **DI / service-locator hybrid**: compose services in `ConfigureServices`, resolve them
in your handler with `ctx.GetRequiredService<T>()`. It keeps handlers small and testable without pulling a
full container into the plugin sandbox.

Override `ConfigureServices` in your plugin (the [plugin](plugins.md) base classes call it once per
execution, before your handler runs):

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    services.AddScoped<IPricingService>(r => new PricingService(context.Service, r.GetRequiredService<IPluginCacheService>()));
    services.AddTransient<IAuditWriter>(_ => new AuditWriter(context.Service));
}
```

Resolve registered services from a handler. Deriving a column onto the record is a **PreOperation** move so it
persists with the row the platform is about to save - no extra `Update`, no recursion (see
[Writing the Target: which stage persists](plugins.md#writing-the-target-which-stage-persists)):

```csharp
private void OnAccountUpdated(IUpdatePreContext<Account> ctx)
{
    var target = ctx.Request.Target;
    var pricing = ctx.GetRequiredService<IPricingService>();
    target["creditonhold"] = pricing.IsOverLimit(target);   // persists in-transaction (pre-stage)
}
```

## Share services across the assembly

When several plugins and Custom APIs need the **same** services, declare them once for the whole assembly
instead of repeating `ConfigureServices` in each class (or inventing a shared base type just to carry it).
Implement `IServiceRegistrar` and point the assembly at it with an assembly-level attribute:

```csharp
// AssemblyInfo.cs (or any file, at assembly scope)
[assembly: VelaServiceRegistrar(typeof(MyServices))]

public sealed class MyServices : IServiceRegistrar
{
    public void Register(SimpleServiceResolver services, ILocalPluginContext context)
    {
        services.AddScoped<IPricingService>(r => new PricingService(context.Service, r.GetRequiredService<IPluginCacheService>()));
        services.AddScoped<IAuditWriter>(_ => new AuditWriter(context.Service));
    }
}
```

The registrar runs for every plugin and Custom API in the assembly, **before** each one's `ConfigureServices`,
so a plugin can still override a shared registration locally (the resolver is last-registration-wins). The
registrar type must be stateless with a public parameterless constructor (the sandbox reuses instances); it is
resolved once per assembly and cached, so the attribute lookup never repeats on the execution path. Only one
`[assembly: VelaServiceRegistrar]` is read per assembly.

Registration runs under the same failure contract as the handler: an exception thrown by the registrar or by
`ConfigureServices` is traced, surfaced as an `InvalidPluginExecutionException` (a `PluginBusinessException`
keeps its own message), and the transient services already created are still disposed.

## Lifetimes

A plugin runs in a fresh instance per pipeline invocation, so there is **no singleton lifetime** - the two
lifetimes are scoped to a single execution:

| Method | Lifetime | Use for |
| --- | --- | --- |
| `AddScoped<T>(factory)` | **one instance per execution** - resolved once, then reused within the same invocation | services with state or setup cost you want to share across the handler |
| `AddTransient<T>(factory)` | **a new instance every resolve** | cheap, stateless helpers |

The factory receives the resolver (`IPluginServiceResolver`), so a service can depend on other registered
services: `r => new PricingService(context.Service, r.GetRequiredService<IPluginCacheService>())`.

## Built-in registrations

These are always available to resolve without registering them yourself:

`ILocalPluginContext`, `IPluginExecutionContext`, `IOrganizationService` (the current user), `ITracingService`,
`ILogger`, `IPluginCacheService`, `IEnvironmentVariableReader`, `IMessageService` (and `IManagedIdentityService`
when the runtime provides it). The built-in resolver and `AddPluginRuntime` register the same set.

> [!TIP]
> `GetRequiredService<T>()` throws if the service isn't registered; use `GetService(typeof(T))` (returns
> `null`) when a dependency is optional.

## System vs user calls in a service

The built-in `IOrganizationService` is the **current user's** service (`ctx.Service`: the step's impersonated user
when it has one, otherwise the caller): calls run with that user's privileges (so a service that reads or writes on the user's behalf respects their security role). When a
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

`ctx` exposes three scopes: `ctx.Service` (the current user), `ctx.SystemService` (SYSTEM) and `ctx.AsUser(id)`
(a specific user; `ctx.AsUser(ctx.InitiatingUserId)` for the user who started the operation).

The rule of thumb: inject a bare `IOrganizationService` for ordinary user-scoped work; inject
`ILocalPluginContext` when the service needs a scope other than the current user. Injecting the context keeps the choice explicit at the call site instead of hiding an elevated
service behind the same `IOrganizationService` type.

## Why

Smaller handlers, cleaner separation of domain vs infrastructure, and - because you inject the
`IOrganizationService` and friends - the same handler runs unchanged against the in-memory
[test harness](testing.md), where you register fakes in `ConfigureServices`.
