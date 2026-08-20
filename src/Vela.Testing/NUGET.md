<img src="https://raw.githubusercontent.com/allandecastro/vela/main/branding/png/vela-chevron.png" width="72" align="left" alt="Vela" />

# Vela.Testing

**Unit-test Dataverse plugins with confidence** — no Dataverse connection, no mocking library required.

<br clear="left" />

`Vela.Testing` builds a fake plugin execution context for plugins and Custom APIs written with
[`Vela.Plugins`](https://www.nuget.org/packages/Vela.Plugins), using a fluent, strongly-typed builder
that mirrors the registration DSL. It has **no dependency on any mocking framework** — you can still
inject your own `IOrganizationService` when you need behaviour the built-in fakes don't cover.

## Install

```powershell
dotnet add package Vela.Testing
```

## Example

```csharp
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Testing;
using Xunit;

public class AccountPostUpdateTests
{
    [Fact]
    public void Renames_The_Account()
    {
        // Arrange + act: build a fake context and run the plugin.
        var plugin = PluginContext.For<Account>()
            .OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithTarget(new Account { Name = "Zava" })
            .Execute<AccountPostUpdate>();

        // Assert on the plugin's own state, output parameters, traces, or service calls.
    }
}
```

Custom APIs and message-only plugins use `PluginContext.ForMessage("vela_DoThing")`.

## What you get

- **Fluent builder** — `OnCreate/OnUpdate/OnDelete`, `Stage(...)`, `Mode(...)`, `Depth(...)`, `WithTarget`,
  `WithPreImage`/`WithPostImage`, `WithInputParameter`/`WithOutputParameter`, `WithTargets` (bulk), and more.
- **Hand-written test doubles** — `FakePluginExecutionContext` (implements the full
  `IPluginExecutionContext2..7` chain, so plugins reading newer context members work),
  `FakeOrganizationServiceFactory`, `FakeOrganizationService` (delegate hooks `OnCreate`/`OnRetrieve`/…),
  and `RecordingTracingService` (`TraceCapture`) for asserting emitted traces.
- **Stateful in-memory CRM** — `InMemoryOrganizationService` makes Create/Retrieve/Update/Delete
  round-trips just work, with a real `RetrieveMultiple` (filters, link-entities, ordering, TopCount, paging,
  Distinct, and FetchXML — a common subset). `Execute(...)` also runs `Create`/`Update`/`Delete`/`Retrieve`/
  `RetrieveMultiple` requests, `CreateMultiple`/`UpdateMultiple`, and `ExecuteMultiple`/`ExecuteTransaction`
  (transactional rollback).
  Opt-in platform behaviours: `WithAlternateKey(...)` (duplicate-key throws) and `WithAutoNumber(...)`
  (populates a field on create). FetchXML aggregates (count/sum/avg/min/max + group-by) are supported too.
  Create/Update stamp the standard system columns (`createdon`/`modifiedon`/`createdby`/`modifiedby`, owner
  columns, `statecode`/`statuscode`, `versionnumber`); pin timestamps with `WithClock(...)` and make ids
  reproducible with `WithIdSequence()` (deterministic sequential GUIDs, asserted via `SequentialIds.At(n)`).
- **Bring your own mock** — `WithOrganizationService(...)` accepts any `IOrganizationService`.
- **Pipeline simulation** — `PluginPipeline` auto-discovers your plugins from the assembly and runs the
  Dataverse pipeline in memory: writes through `pipeline.Service` fire the registered steps (stages, order,
  filtering attributes, pre/post images) and **cascade** (a plugin that writes triggers more plugins).
  Synchronous failures roll back; async steps run after commit; Custom APIs run via `Service.Execute(...)`.
  Inspect `pipeline.ExecutionLog` to see what fired.

## Documentation

Full guide with examples: **[Testing with Vela.Testing](https://allandecastro.github.io/vela/guides/testing.html)** —
or the repo: **https://github.com/allandecastro/vela**
