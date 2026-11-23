# Vela.Testing

**Unit-test Dataverse plugins with confidence** - no Dataverse connection, no mocking library required.

`Vela.Testing` runs plugins and Custom APIs written with
[`Vela.Plugins`](https://www.nuget.org/packages/Vela.Plugins) through a simulated Dataverse pipeline over a stateful
in-memory Dataverse. It has no dependency on a mocking framework.

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
        // The Dataverse pipeline in memory. The write fires every registered step and cascades.
        var pipeline = PluginPipeline.FromAssemblyOf<AccountPostCreate>();
        pipeline.Service.Create(new Account { Name = "Zava" });

        Assert.Single(pipeline.GetAll("task"));
    }
}
```

Custom APIs and message-only plugins run through the pipeline with `pipeline.Service.Execute(new DoThingRequest { ... })`
(or `new OrganizationRequest("zava_DoThing") { ["Amount"] = 5 }` for a message with no request type).

## What you get

- **Pipeline simulation** - `PluginPipeline` discovers your plugins and runs the Dataverse pipeline in memory:
  stages, rank, filtering attributes, images, cascade, the platform's transaction scope, async steps after the
  outermost commit, reads and Custom APIs through their steps. Inspect `pipeline.ExecutionLog` to see what fired.
- **Assertions** - `pipeline.Trace` and `pipeline.LogCapture` record the traces, logs and metrics of the whole cascade.
- **Stateful in-memory Dataverse** - `InMemoryOrganizationService`: CRUD round-trips, `RetrieveMultiple` (QueryExpression,
  QueryByAttribute, FetchXML with joins and aggregates), `Upsert`, `Assign`/`SetState`/`Merge`, N:N associations,
  bulk and batch requests, file blocks, metadata reads, system columns, alternate keys, auto-numbers and an
  opt-in security model.

## Documentation

Full guide: **[Testing with Vela.Testing](https://allandecastro.github.io/vela/guides/testing.html)** -
repo: **https://github.com/allandecastro/vela**
