# Vela.Testing

**Unit-test Dataverse plugins with confidence** - no Dataverse connection, no mocking library required.

`Vela.Testing` runs plugins and Custom APIs written with
[`Vela.Plugins`](https://www.nuget.org/packages/Vela.Plugins) against a fake execution context and a stateful
in-memory Dataverse. It has no dependency on a mocking framework; you can still inject your own
`IOrganizationService` when you need behaviour the built-in fakes don't cover.

## Install

```powershell
dotnet add package Vela.Testing
```

## Example

```csharp
using Vela.Testing;
using Xunit;

public class AccountPostCreateTests
{
    [Fact]
    public void Creates_A_Follow_Up_Task()
    {
        // One plugin against a fake context: the trigger is derived from the plugin's single step.
        var crm = new InMemoryOrganizationService();
        PluginContext.For<Account>()
            .WithTarget(new Account { Name = "Zava" })
            .WithOrganizationService(crm)
            .Execute<AccountPostCreate>();

        Assert.Single(crm.GetAll("task"));
    }

    [Fact]
    public void Runs_The_Pipeline()
    {
        // The Dataverse pipeline in memory: the write fires every registered step and cascades.
        var pipeline = PluginPipeline.FromAssemblyOf<AccountPostCreate>();
        pipeline.Service.Create(new Account { Name = "Zava" });

        Assert.Single(pipeline.GetAll("task"));
    }
}
```

Custom APIs and message-only plugins use `PluginContext.ForMessage("vela_DoThing")`, or
`pipeline.Service.Execute(new OrganizationRequest("vela_DoThing") { ... })` for the full pipeline.

## What you get

- **Fluent context builder** - `PluginContext.For<T>()` / `ForMessage(...)` with `OnCreate/OnUpdate/OnDelete/OnMessage`,
  `Stage(...)`, `Sync()`/`Async()`, `Depth(...)`, `WithTarget`, `WithPreImage`/`WithPostImage`, `WithTargets` (bulk),
  `WithInputParameter`, `WithSharedVariable`, and `TraceCapture` / `LogCapture` for asserting traces and logs.
  Run with `Execute(plugin)` or `Execute<TPlugin>()`, or `Build()` a `LocalPluginContext`.
- **Test doubles** - `FakePluginExecutionContext` (the full `IPluginExecutionContext2..7` chain),
  `HookOrganizationService` / `HookOrganizationServiceFactory` (delegate hooks `OnCreate`/`OnRetrieve`/...; an unset
  hook throws), `RecordingTracingService` and `RecordingLogger`.
- **Stateful in-memory CRM** - `InMemoryOrganizationService`: CRUD round-trips, `RetrieveMultiple` (QueryExpression,
  QueryByAttribute, FetchXML with joins and aggregates), `Upsert`, `Assign`/`SetState`/`Merge`, N:N associations,
  bulk and batch requests, file blocks, metadata reads, system columns, alternate keys, auto-numbers and an
  opt-in security model.
- **Pipeline simulation** - `PluginPipeline` discovers your plugins and runs the Dataverse pipeline in memory:
  stages, rank, filtering attributes, images, cascade, the platform's transaction scope, async steps after the
  outermost commit, reads and Custom APIs through their steps. Inspect `pipeline.ExecutionLog` to see what fired.

## Documentation

Full guide: **[Testing with Vela.Testing](https://allandecastro.github.io/vela/guides/testing.html)** -
repo: **https://github.com/allandecastro/vela**
