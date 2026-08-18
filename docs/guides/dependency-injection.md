# Dependency injection (lightweight DI)

Override `ConfigureServices` in your plugin:

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    services.AddTransient<IMyService>(_ => new MyService(context.UserService));
}
```

Pattern used here: a lightweight **Dependency Injection / Service Locator hybrid** (composition in `ConfigureServices`, resolution via `ctx.GetRequiredService<T>()` at usage sites).  
Value: smaller handlers, easier unit tests, cleaner separation domain/infrastructure, and consistent dependency wiring.

Built-in registrations: `ILocalPluginContext`, `IPluginExecutionContext`, `IOrganizationService`, `ITracingService`, `ILogger`, `IPluginCacheService`

You can resolve registered services from handlers with:

```csharp
IMyService service = ctx.GetRequiredService<IMyService>();
```
