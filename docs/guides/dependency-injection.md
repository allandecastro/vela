---
_description: "The sandbox-safe container: registration, lifetimes, the built-in services and the user a service runs as"
---

# Dependency injection

Vela ships a small resolver for plugins and Custom APIs: register services in `ConfigureServices` (or once for the
whole assembly in an `IServiceRegistrar`), then resolve them in a handler with `ctx.GetRequiredService<T>()`. It
keeps handlers small and testable without loading a full container into the plugin sandbox. This page covers
registration, lifetimes, the built-in services and how a service picks the user it runs as. It assumes you have
written a [plugin](plugins.md) or a [Custom API](custom-apis.md).

## Register services in a plugin

Override `ConfigureServices` in your plugin or Custom API. The base class calls it once per execution, before your
handler runs:

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    services.AddScoped<IPricingService>(r => new PricingService(context.Service, r.GetRequiredService<IPluginCacheService>()));
    services.AddTransient<IAuditWriter>(_ => new AuditWriter(context.Service));
}
```

Resolve the registered services from a handler. This one derives a column at PreOperation, so the value is saved
with the row without an extra `Update` (see
[Writing the Target: which stage persists](plugins.md#writing-the-target-which-stage-persists)):

```csharp
private void OnAccountUpdated(OnAccountUpdatedContext ctx)
{
    Account target = ctx.Request.Target;
    IPricingService pricing = ctx.GetRequiredService<IPricingService>();
    target.CreditOnHold = pricing.IsOverLimit(target);   // saved with the row (pre-stage)
}
```

## Share services across the assembly

When several plugins and Custom APIs need the **same** services, declare them once for the whole assembly instead
of repeating `ConfigureServices` in each class (or adding a shared base type just to carry it). Implement
`IServiceRegistrar` (namespace `Vela.DI`) and point the assembly at it with an assembly-level attribute:

```csharp
// ProjectServices.cs (or any file, at assembly scope)
[assembly: VelaServiceRegistrar(typeof(ProjectServices))]

public sealed class ProjectServices : IServiceRegistrar
{
    public void Register(SimpleServiceResolver services, ILocalPluginContext context)
    {
        services.AddScoped<IPricingService>(r => new PricingService(context.Service, r.GetRequiredService<IPluginCacheService>()));
        services.AddScoped<IAuditWriter>(_ => new AuditWriter(context.Service));
    }
}
```

- The registrar runs for every plugin and Custom API in the assembly, **before** each one's `ConfigureServices`, so
  a plugin can still override a shared registration locally (the last registration wins).
- The registrar type must be stateless with a public parameterless constructor, like a plugin (the sandbox reuses
  instances). It is resolved once per assembly and cached, so the attribute lookup does not repeat on each
  execution. Only one `[assembly: VelaServiceRegistrar]` is allowed per assembly.
- `vela init` generates this registrar, empty, in each plugin project: `ProjectServices.cs`, with its assembly
  attribute. Add the shared registrations to its `Register` (see [Scaffold a solution](scaffold.md)).

Registration follows the same failure contract as the handler: an exception thrown by the registrar or by
`ConfigureServices` is traced and reaches the caller as described in [Errors](plugins.md#errors), and the transient
services already created are still disposed. Tracing the failure is best-effort: a tracing service that throws
never replaces the original error.

## Lifetimes

The sandbox caches a plugin instance and reuses it, concurrently, across executions in the same process, so a
singleton would leak state between unrelated executions (and users). There is therefore **no singleton lifetime**.
Both lifetimes are scoped to a single execution:

| Method | Lifetime | Use for |
| --- | --- | --- |
| `AddScoped<T>(factory)` | **One instance per execution**: created on the first resolve, then reused within the same execution. Disposed at the end of the execution when it implements `IDisposable`. | Services with state or setup cost that the handler shares. |
| `AddTransient<T>(factory)` | **A new instance on every resolve.** Disposed at the end of the execution when it implements `IDisposable`. | Cheap, stateless helpers. |

Vela disposes what your factories create. A factory must therefore not return an `IDisposable` instance that lives
longer than the execution, such as an `HttpClient` kept in a static field: register a service that wraps it
instead. The built-in services below are never disposed by the resolver.

The factory receives the resolver (`IPluginServiceResolver`), so a service can depend on other registered
services: `r => new PricingService(context.Service, r.GetRequiredService<IPluginCacheService>())`. A factory that
resolves its own type, directly or through a cycle, fails with an `InvalidPluginExecutionException`.

## Built-in services

These are always available without registering them yourself:

- `ILocalPluginContext` and `IPluginExecutionContext`.
- `IOrganizationService`: the current user's service (see [System and user calls](#system-and-user-calls-in-a-service)).
- `ITracingService`, and `ILogger` (`Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`, the platform's Application Insights
  logger; see [Telemetry](telemetry.md)).
- `IPluginCacheService` (see [Caching](caching.md)), `IEnvironmentVariableReader` (see
  [Environment variables](environment-variables.md)) and `IMessageService` (see
  [Localized messages](localization.md)).
- `IManagedIdentityService`, when the runtime provides it (see [Managed identity](managed-identity.md)).

The built-in resolver and `AddPluginRuntime` (for a `Microsoft.Extensions.DependencyInjection` container) register
the same set. `AddPluginRuntime` is for tests and hosts outside the sandbox: inside a plugin, use the built-in
resolver. `Vela.Plugins` references only the dependency-injection abstractions, and Vela does not support or test
the full container inside a plugin package.

> [!TIP]
> `GetRequiredService<T>()` throws if the service isn't registered. Use `GetService(typeof(T))`, which returns
> `null`, when a dependency is optional.

## System and user calls in a service

The built-in `IOrganizationService` is the **current user's** service (`ctx.Service`: the step's impersonated user
when it has one, otherwise the caller), so its calls run with that user's privileges. SYSTEM is never injected
implicitly, and there is only one `IOrganizationService` registration. When a service needs another scope, inject
`ILocalPluginContext` and pick the scope there:

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    // A service that must bypass the caller's privileges gets the context, not a bare service.
    services.AddScoped<IQuotaEnforcer>(r => new QuotaEnforcer(r.GetRequiredService<ILocalPluginContext>()));
}

public sealed class QuotaEnforcer(ILocalPluginContext ctx) : IQuotaEnforcer
{
    public void Enforce(EntityReference account)
    {
        var update = new Account { Id = account.Id, CreditOnHold = true };
        ctx.SystemService.Update(update);   // runs as SYSTEM, whatever the caller's role
    }
}
```

The context exposes three scopes: `ctx.Service` (the current user), `ctx.SystemService` (SYSTEM) and
`ctx.AsUser(id)` (a given user; `ctx.AsUser(ctx.InitiatingUserId)` for the user who started the operation).

Rule of thumb: inject `IOrganizationService` for ordinary user-scoped work, and `ILocalPluginContext` when the
service needs another scope, so the elevation stays visible at the call site.

## Test with replaced services

Because a handler gets its services from the resolver, the same handler runs unchanged in the
[test harness](testing.md). A test replaces any service the plugin registers with `WithService<T>(instance)`,
without changing the plugin (see [Replacing services](testing.md#replacing-services)).

## See also

- [Write a plugin](plugins.md): the context helpers available in a handler.
- [Caching](caching.md), [Environment variables](environment-variables.md), [Localized messages](localization.md),
  [Managed identity](managed-identity.md), [Telemetry](telemetry.md): the built-in services.
