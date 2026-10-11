# Cookbook (common recipes)

Short sketches of the patterns you reach for most. Each links to its guide.

## Single-step update plugin

See [Write a plugin](plugins.md).

```csharp
public sealed partial class AccountPostUpdate(string unsecureConfig, string secureConfig)
    : EntityPluginBase<Account>(unsecureConfig, secureConfig)
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .Handle(OnAccountUpdated);

    private void OnAccountUpdated(OnAccountUpdatedContext ctx) { /* ctx.Request.Target ... */ }
}
```

## Several steps in one class

```csharp
protected override void RegisterPluginSteps()
{
    RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);
    RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdated);
}
```

Two steps on the **same** trigger need something that tells them apart (filter, images, relationship scope) or a
`WithStepKey("...")`. See [Several steps in one class](plugins.md#several-steps-in-one-class).

## Bound Custom API

See [Write a Custom API](custom-apis.md).

```csharp
public sealed class RecalculateApi(string unsecureConfig, string secureConfig)
    : CustomApiBase(unsecureConfig, secureConfig)
{
    protected override void RegisterCustomApi() =>
        RegisterCustomApi("zava_Recalculate", Recalculate)
            .Description("Recalculates the bound account.")
            .AsAction()
            .BoundTo<Account>();

    private void Recalculate(ILocalPluginContext ctx) { /* ... */ }
}
```

## Dependency injection and caching

Register in `ConfigureServices` and resolve with `ctx.GetRequiredService<T>()`. See
[Dependency injection](dependency-injection.md) and [Caching](caching.md).

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context) =>
    services.AddScoped<IPricingService>(r => new PricingService(r.GetRequiredService<IOrganizationService>()));
```

## Test without a connection

See [Testing with Vela.Testing](testing.md).
