# Cookbook (common recipes)

Quick sketches for the patterns you'll reach for most. Each links to the full guide.

## Single-step update plugin

Declare one step in `RegisterPluginSteps()`. See [Write a plugin](plugins.md).

```csharp
public class AccountPostUpdate : EntityPluginBase<Account>
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .WithFiltering(a => a.Name).Handle(OnAccountUpdated);

    private void OnAccountUpdated(IUpdatePostContext<Account> ctx) { /* ctx.Request.Target ... */ }
}
```

## Multi-step router plugin

Register several steps on one class and route each to its handler. See [Write a plugin](plugins.md).

```csharp
protected override void RegisterPluginSteps()
{
    RegisterStep().OnCreate().PostOperation().Sync().WithFiltering(a => a.Name).Handle(OnCreate);
    RegisterStep().OnUpdate().PostOperation().Sync().WithFiltering(a => a.Name).Handle(OnUpdate);
}
```

## Bound / unbound Custom API

See [Write a Custom API](custom-apis.md).

```csharp
protected override void RegisterCustomApi() =>
    RegisterCustomApi("zava_Recalculate", Handler).AsAction().BoundTo<Account>();
```

## Dependency injection + caching in a handler

Register in `ConfigureServices`, resolve via `ctx.GetRequiredService<T>()`, cache shared data with the
built-in TTL cache. See [Dependency injection](dependency-injection.md) and [Caching](caching.md).

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext ctx) =>
    services.AddScoped<IPricingService>(_ => new PricingService(ctx.UserService));
```

## Test any of the above without a connection

See [Testing with Vela.Testing](testing.md).
