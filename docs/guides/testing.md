---
_description: "PluginPipeline (the default) runs the whole in-memory pipeline; PluginContext runs one plugin, for edge cases"
---

# Testing with Vela.Testing

`Vela.Testing` runs plugins and Custom APIs **without a Dataverse connection** and **without a mocking library**.
This page covers its two ways of running a plugin, the in-memory Dataverse they run against, and what the harness
does not simulate. It assumes you have a plugin to test: see [Write a plugin](plugins.md) and
[Write a Custom API](custom-apis.md).

- `PluginPipeline` runs **the Dataverse pipeline in memory**: a write fires the registered steps, cascades, and rolls
  back as on the platform. It is the default.
- `PluginContext` builds a fake execution context and runs **one plugin** against it, for edge cases.

Both run against `InMemoryOrganizationService`, a stateful in-memory Dataverse. To test which records a user can
read or write, see [Security model in tests](security-model.md).

```powershell
dotnet add package Vela.Testing
```

`vela add test <Plugin>` writes a first `PluginPipeline` test for each step of a plugin, and a test for a Custom API
(see [Add a file](add.md)). The test project that `vela init` scaffolds already references `Vela.Testing`.

## Which one to use

Test with `PluginPipeline` by default. Registered with every plugin of the assembly
(`PluginPipeline.FromAssemblyOf<T>()`), it is an integration-style test of your plugins together. It runs what
Dataverse runs for a request: every registered step with its
filtering attributes and images, in stage and execution order across plugins, the cascade of the plugins' own writes,
the rollback of a synchronous failure, and the asynchronous steps after the commit. When several steps of one plugin
share a trigger and differ only by their filter, images or step key, test them with the pipeline:
`PluginContext` does not choose between them.

Use `PluginContext` for unit tests of one handler, in the edge cases the pipeline does not easily produce:

- a `Depth` of 2 or more, or a crafted `ParentContext`;
- shared variables set by another plugin;
- an invalid Custom API input, to exercise the API's own checks (`.WithoutRequestValidation()`);
- one specific error branch of a handler.

## Simulate the pipeline (cascade)

A plugin that creates a task when an account is created:

[!code-csharp[](../samples/Vela.DocSamples/TestingSample.cs#pipeline-plugin)]

is tested by creating an account through `pipeline.Service` and asserting on the resulting data and on what ran:

[!code-csharp[](../samples/Vela.DocSamples/TestingSample.cs#pipeline-test)]

Writes through `pipeline.Service` fire the registered steps (stages, execution order, filtering attributes, pre and
post images), and a plugin's own writes fire further steps (the **cascade**).

### Register the plugins

- `PluginPipeline.FromAssemblyOf<T>()` (or `FromAssembly(assembly)`) registers every Vela plugin and Custom API of
  the assembly. It throws when a step or Custom API names a type it cannot resolve, since that registration would
  never fire.
- `PluginPipeline.Empty().Register<MyPlugin>()` scopes a test to the plugins you name. `Register<T>()` throws for a
  type that declares no step and no Custom API, and for a type already registered (by an earlier `Register` or by
  `FromAssembly`), whose steps would otherwise fire twice.

### Configure the pipeline

`PluginPipeline` takes the same configuration as `InMemoryOrganizationService` (`WithSecurity`,
`WithStrictSecurity`, `WithAlternateKey`, `WithAutoNumber`, `WithDefaultStatus`, relationships, metadata, ownership,
clock and id sequence; see [In-memory Dataverse](#in-memory-dataverse)). It adds:

| Method | Purpose |
| --- | --- |
| `Seed(...)` | Writes the starting records into the store. No plugin fires. |
| `WithSecureConfig(name, value)` | The secure configuration of the steps registered with `WithSecureConfigFrom(name)`. |
| `WithMaxDepth(n)` | The cascade depth limit (default 8, the Dataverse default). |
| `WithManagedIdentityService(service)` | A managed-identity service for every plugin and Custom API the pipeline runs (see [Managed identity](managed-identity.md#testing)). |
| `WithService<T>(instance)` | A test double for a service the plugin registers (see [Replacing services](#replacing-services)). |
| `WithEnvironmentId(id)`, `WithTenantId(id)`, `AsApplicationUser()` | The `EnvironmentId`, `TenantId` and `IsApplicationUser` of every step's context (empty, `Guid.Empty` and `false` by default). |

### Assert on the result

| Member | What it holds |
| --- | --- |
| `GetAll(table)` | Every record of the table in the store. |
| `ExecutionLog` | One `PipelineExecutionEntry` per step invocation: plugin type, message, stage, mode, table, depth. |
| `AsyncStepFailures` | The exceptions thrown by asynchronous steps (they never reach the caller). |
| `Trace` | The trace lines of the whole cascade (a `RecordingTracingService`). |
| `LogCapture` | The `ctx.Log*` entries and metrics of the whole cascade (see [Telemetry](telemetry.md#testing)). |

An `ExecutionLog` entry means the platform **invoked** the step, not that its handler ran. Vela's runtime can still
skip the handler: a `.ForRelationship(...)` step is invoked for every relationship, and `.SkipWhenDepthAbove(n)` or
the plugin's depth guard stop it past a depth. Such an entry has `HandlerInvoked` set to `false` and `SkipReason`
set to the line the runtime traced. An execution that fails before reaching the handler (in the plugin's
constructor or its service registration) also has `HandlerInvoked` set to `false`, with no `SkipReason`.

```csharp
pipeline.Service.Associate("account", accountId, new Relationship("account_partners"), contacts);

PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("AccountContactsPlugin"));
Assert.False(entry.HandlerInvoked);   // invoked, but outside its ForRelationship scope
Assert.Contains("ForRelationship", entry.SkipReason);
```

### Transactions and rollback

The pipeline follows the platform's
[stage model](https://learn.microsoft.com/power-apps/developer/data-platform/event-framework#event-execution-pipeline):

- The PreValidation stage of the initial operation runs **before** the database transaction. A write a
  PreValidation plugin makes commits on its own, even if the operation fails later.
- The transaction opens before PreOperation. Any operation a plugin triggers from inside it joins it, including
  that operation's own PreValidation stage (at depth 2). A plugin's own operations reach their PreValidation steps
  with the extra input `InputParameters["FromSandbox"] = true`.
- A synchronous failure anywhere **rolls the whole transaction back**. `ExecuteTransaction` runs all its children
  inside one transaction.
- Reads (`Retrieve`, `RetrieveMultiple`) are not transactional.
- A plugin that catches the failure of one of its **writes** cannot carry on: its next service call fails with
  "There is no active transaction" (`-2147220911`), and returning normally fails the operation with "ISV code
  reduced the open transaction count" (`0x8009000C`, `-2146893812`). A caught failed **read** does not end the
  transaction: a missing record, a missing Read privilege, a record out of the role's depth. An `Update` checks
  Read on the record before Write, so an `Update` the caller cannot read fails with the read fault (`0x80040220`
  or `0x80048306`) and, caught, does not end the transaction either. `SetState`, `Merge` and a 1:N `Associate` or
  `Disassociate` also check Read on the record they write, but that failure, caught, ends the transaction like a
  failed write.
- **Asynchronous** steps run once the outermost operation (or `ExecuteTransaction` batch) commits, and never when
  it rolls back. An asynchronous step's exception does not reach the caller: it is recorded on
  `pipeline.AsyncStepFailures`. An asynchronous step runs outside any transaction, so the writes it made before
  failing stay, and the asynchronous steps they queued run.
- `ctx.IsInTransaction` is `false` for asynchronous steps, the steps of a top-level read, a depth-1 PreValidation
  step (a Custom API's included), the PreValidation step of an `Upsert`'s inner `Create` or `Update`, and the steps
  of an `Upsert`, `Create` or `Update` on an elastic table. It is `true` otherwise: a PreValidation step inside an `ExecuteTransaction` is
  in its transaction, and so are the steps of a read a plugin issues inside a write's transaction, at every stage.
- The cascade depth is capped at 8 (`PluginBase.DefaultMaxDepth`), on top of the plugin's own depth guard.
  `WithMaxDepth(n)` changes it. The platform's default is also 8 (see
  [`IExecutionContext.Depth`](https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.iexecutioncontext.depth)).
  The one-hour window of the platform's loop detection is not modelled.

### The execution context

- **Identity.** `UserId`, `InitiatingUserId`, `BusinessUnitId` and `OrganizationId` come from `WithCurrentUser(...)`,
  `WithCurrentBusinessUnit(...)` and `WithOrganizationId(...)`, consistent with the `createdby`/`ownerid` stamping
  and with `WhoAmI` called from the test.
- **User and SYSTEM services.** `ctx.Service` acts as `ctx.UserId` and is subject to the security model under
  `WithSecurity`. `ctx.SystemService` acts as SYSTEM and is not. A record written through `ctx.SystemService` has
  SYSTEM as `createdby`, `modifiedby` and `ownerid`, the calling user as `createdonbehalfby`, and the root business
  unit as `owningbusinessunit`. A plugin fired by a write made through `ctx.SystemService` runs as SYSTEM, so its
  own `ctx.Service` is unrestricted too.
- **`WhoAmI` inside a plugin** returns the SYSTEM user and the root business unit, on `ctx.Service` as well as on
  `ctx.SystemService`. Read the calling user from `ctx.UserId` or `ctx.InitiatingUserId` instead. With
  `PluginContext`, `ctx.Service` is the store itself, so `WhoAmI` on it returns the configured user.
- **Step registration.** A `.Disabled()` step never fires. The plugin is constructed with the step's
  `WithUnsecureConfig(...)` value and, for `WithSecureConfigFrom("Name")`, the value set with
  `pipeline.WithSecureConfig("Name", "value")` (what `vela apply --secure-config Name=value` provides in production).
  Unset configuration is passed as `null`. A step registered with `WithImpersonation(userId)` runs with `ctx.UserId`
  set to that user, and its `ctx.Service` acts as that user (privileges under `WithSecurity`, audit stamping);
  `InitiatingUserId` stays the caller.
- **Ids.** `CorrelationId` is one id per top-level operation (an `ExecuteTransaction` batch shares one, each
  `ExecuteMultiple` child gets its own), kept by the asynchronous steps it queues and by the writes they make. `RequestId` is one id per call
  into the pipeline. `OperationId` equals it on synchronous steps and is the job id on asynchronous steps.
  `IsolationMode` is 2 (sandbox) and `SecondaryEntityName` is `"none"`.
- **Asynchronous steps.** An asynchronous step's `OwningExtension` has the step's id but no `Name` (a synchronous
  step's carries its name), and its post images include `AsynchronousStepPrimaryName` (the record's primary name
  and id).
- **`ParentContext`.** A PreOperation or PostOperation step of a `Create`, `Update` or `Delete` has the operation's
  own stage-30 (MainOperation) context as `ParentContext`. The context of the plugin whose write triggered the
  operation is one level further up (`ParentContext.ParentContext`). A PreValidation step's `ParentContext` is that
  calling plugin's context directly, as it was when that plugin started: the shared variables the plugin set
  afterwards are not in it.
  - The Update steps fired inside an `Assign`, a `SetState` or a `Merge` have that message's stage-30 context as
    `ParentContext` (`Assign/30`, `SetStateDynamicEntity/30`, `Merge/30`).
  - An `Upsert` runs its `Create` or `Update` at its own depth, with the `Upsert/30` context (`PrimaryEntityName`
    `"none"`) as the parent of that operation's contexts.
  - The children of a cascaded `Delete` fire no PreValidation step. Their PreOperation and PostOperation steps run
    before the root's, at the root's depth, with the root's `Delete/30` context as `ParentContext`.
  - Under a `CreateMultiple` or `UpdateMultiple` request, a row's `Create` or `Update` step has the
    `CreateMultiple`/`UpdateMultiple` context of the same stage as `ParentContext`, and that context has the
    `CreateMultiple/30` or `UpdateMultiple/30` context as its own.
- **Shared variables.** Shared variables set by a PreValidation step are not in the later steps' own
  `SharedVariables`: they are visible only through `ParentContext.SharedVariables`.
  - A single `Create` or `Update` shares one collection between its PreOperation and PostOperation steps, the
    `CreateMultiple`/`UpdateMultiple` ones included.
  - Under a `CreateMultiple` or `UpdateMultiple` request, each row's `Create` or `Update` steps have their own
    collections: one at PreValidation, which no later step sees, and one shared by PreOperation and PostOperation.
  - A cascaded `Delete` child has its own collection.
  - An operation a plugin issues starts with its own collection. It finds the calling plugin's variables through its
    `ParentContext` chain, and nothing it sets reaches the calling plugin.

  To avoid re-entrancy, see [Preventing recursion](plugins.md#preventing-recursion).
- **The `tag`.** A client passes a `tag` on a request object (the shortcut methods such as `Create` take none), and
  the plugin reads it with `ctx.Tag` (or `ctx.GetSharedVariable("tag")`):

  ```csharp
  var request = new CreateRequest { Target = new Account { Name = "Zava" } };
  request["tag"] = "bulk-import";
  pipeline.Service.Execute(request);   // the plugin sees ctx.Tag == "bulk-import"
  ```

### Messages

- **Isolation.** The steps work on a copy of what you send. After `Create`, your entity's `Id` is still empty (read
  the id from the return value), and a plugin's change to `Target` is not visible on your object. As on the
  platform, which caches plugin instances, each step (and each Custom API) runs on **one plugin instance reused
  across executions**, so a plugin that keeps state in fields misbehaves in the test as it would in production.
- **Not found.** Retrieving or updating a record that does not exist throws
  `FaultException<OrganizationServiceFault>` with the `ObjectDoesNotExist` code (`0x80040217`, `-2147220969`).
- **Reads run through the pipeline.** `Retrieve` and `RetrieveMultiple` (the shortcut methods and the
  `RetrieveRequest`/`RetrieveMultipleRequest` messages) fire their registered steps. A PreValidation or PreOperation
  step that replaces `InputParameters["ColumnSet"]`, `["Target"]` or `["Query"]` changes what is read, and a
  PostOperation step sees the result as `OutputParameters["BusinessEntity"]` or `["BusinessEntityCollection"]` and
  can change it. The same holds for any message: a synchronous step's changes to the inputs reach the main
  operation, and its changes to the outputs reach the caller (the caller's request object is not modified).
- **Returned records.** A read always returns the primary key column (`{table}id`, `activityid` for activities, or
  the `PrimaryIdAttribute` of registered metadata). A lookup carries the referenced record's primary name in
  `EntityReference.Name` and in `FormattedValues`, and a choice carries its label in `FormattedValues` when its
  options are registered with `WithEntityMetadata(...)`.
- **Custom APIs** run through `Service.Execute(new OrganizationRequest("vela_MyApi") { ["x"] = 1 })` (or the typed
  request). The steps registered on the API's message fire at PreValidation, PreOperation and PostOperation around
  the API's own plugin (the MainOperation stage, under a step named `CustomApi '<uniquename>' implementation`), as
  far as its `AllowedCustomProcessingSteps(...)` allows: a step it does not allow fails the call. The request is
  checked before any step runs: a missing required input throws `0x80040203` ("Required field 'X' is missing for
  RequestName='...'"), and an input of the wrong type or an undeclared input throws `0x80040315`. The API's main
  operation and the steps on its message see the primary entity of its binding: `"none"` and an empty id for a
  global API, the bound table and the Target id for an entity-bound API, the bound table and an empty id for a
  collection-bound one. An asynchronous step's `PrimaryEntityId` is always empty. A global API's steps run whatever
  their plugin's table, as `vela plan` registers them without one. A step on a bound API declared on another table
  than the bound one fails the call, as `vela plan` refuses it. A message no registered Custom API defines (an API
  deployed from another assembly) runs on the table of its `Target`, or on `"none"` with no `Target`, as a global
  API's does.
- **Bulk (`CreateMultiple`/`UpdateMultiple`).** A bulk request runs your single `Create`/`Update` steps **once per
  row**, and a step registered with `OnCreateMultiple()`/`OnUpdateMultiple()` runs **once with the whole batch**
  (read it through `ctx.BulkTargets`, with per-row images in `ctx.BulkPreEntityImagesCollection` and
  `ctx.BulkPostEntityImagesCollection`, which also hold the plugin's other steps' images and the platform's
  `PreBusinessEntity`/`PostBusinessEntity`: read images by alias). The merge runs both ways, so a bulk step also
  fires for a plain `Create`/`Update` (a one-row batch). The whole batch is one transaction.
- **Owner and state.**
  - An `Update` is **one** execution, even when it mixes `ownerid` or `statecode`/`statuscode` with other columns.
    An `ownerid` Update never fires the `Assign` steps.
  - An `Update` that sets `statecode` also fires the `SetStateDynamicEntity` steps, nested at depth + 1 between the
    Update's PreOperation and PostOperation. Their `Status` is -1 when the Update sets no `statuscode`; the record
    then gets the state's default status, and the Update's Target stays as sent.
  - `Execute(new AssignRequest { ... })` fires the `Assign` steps (PreValidation, PreOperation), then an `ownerid`
    Update at the same depth that fires the Update steps at PreOperation and PostOperation only, then the `Assign`
    PostOperation steps.
  - `Execute(new SetStateRequest { ... })` fires the `SetStateDynamicEntity` steps the same way around a
    `statecode`/`statuscode` Update. Steps registered on `SetState` never fire.
  - `Execute(new MergeRequest { ... })` writes the master's `UpdateContent` through an Update that fires the Update
    steps at PreOperation and PostOperation, between the `Merge` PreOperation and PostOperation steps. The
    subordinate's own columns are not copied to the master, so no master Update runs when `UpdateContent` is null
    or empty. The subordinate gets `statecode` 1, `statuscode` 2 and `masterid`; merging it again faults with
    `0x80045304`.
  - Status -1 (or no status) resolves to the state's default status: status 1 for state 0, status 2 for any other
    state, unless you declare it with `WithDefaultStatus("table", state, status)`.
- **Bypass parameters.** On an `Execute(...)` request, `BypassCustomPluginExecution = true` skips every step,
  `BypassBusinessLogicExecution = "CustomSync"` or `"CustomAsync"` skips by execution mode, and
  `BypassBusinessLogicExecutionStepIds` skips specific steps, matched against the step **key** (a comma-separated
  string or a collection of keys). The data is still written. A request's effective bypass is the one it inherits
  (from an enclosing request or an `ExecuteMultiple`/`ExecuteTransaction` batch) plus its own, so a plugin that
  writes with `BypassCustomPluginExecution = true` does not re-enter itself.
- **Associate and Disassociate** fire their registered steps whatever the table (they are global messages: a step
  declared on `account` also fires for an association made from `contact`). The N:N intersect rows persist when the
  relationship is declared (see [Relationships](#relationships)).
- **Upsert and alternate keys.** `Execute(new UpsertRequest { ... })` matches the target by id or by an alternate key
  declared with `WithAlternateKey(...)`: an existing record runs the Update steps, a new one the Create steps, and
  the response reports `RecordCreated` and `Target`. The steps registered on `Upsert` itself fire only for a table
  declared elastic with `AsElastic(...)`: Dataverse has `Upsert` message filters for elastic tables only, so an
  `Upsert` step never fires for a standard table. On an elastic table, the `Upsert` runs outside any transaction and
  fires its own steps only, not the inner `Create` or `Update` steps; a plain `Create` or `Update` there also runs
  outside any transaction. An `Update`, `UpdateMultiple`, `Assign` or `Delete` addressed by
  `KeyAttributes` resolves the key first, on `InMemoryOrganizationService` as through `PluginPipeline`, so its steps
  see the real record id. The `KeyAttributes` must be exactly one declared key: any
  other set of columns faults with `0x80060889`, and a declared key that matches nothing with `0x80060891`.
- **Business messages** (`WinOpportunity`, `QualifyLead`, `CloseIncident`, `SendEmail`, `AddToQueue` and the like).
  The harness does not reproduce the server logic of these messages. The steps registered on them fire at
  PreValidation, PreOperation and PostOperation, with the typed `ctx.Request` and, in PostOperation, the
  `OutputParameters`, but the main operation is a no-op unless the store models the request. You test your plugins'
  reaction to the message, not the message itself. A message with no registered step that the store does not model
  throws `MessageNotModelledException`. A message the store models (an invalid `Merge`, a missing record) still
  fails as it would, even when steps are registered on it.

## Run a plugin against a fake context

`PluginContext` is for the edge cases listed in [Which one to use](#which-one-to-use). The fluent builder is
strongly typed. The trigger (message, stage, mode) comes from the plugin's own registration, so you state only what
the test needs:

[!code-csharp[](../samples/Vela.DocSamples/TestingSample.cs#context-test)]

To inspect the context itself, build a `LocalPluginContext` (state the trigger to override the default):

```csharp
LocalPluginContext ctx = PluginContext.For<Account>()
    .OnUpdate().Stage(PluginStages.PostOperation).Sync()
    .WithTarget(new Account { Name = "Zava" })
    .Build();
```

### How the trigger is resolved

`Execute(plugin)`, `Execute<TPlugin>()`, `Build(plugin)` and `Build<TPlugin>()` fill the parts of the trigger you
did not set from the registered steps that match the parts you did set (and the primary entity): `.OnCreate()` alone
is enough when the plugin has one `Create` step. When several steps still match (a `Create` step at PreOperation and
another at PostOperation), or none does, they throw an `InvalidOperationException` that lists the registered steps
(`Create/PreOperation/Sync on account [step-key]`); add `.Stage(...)` or `.Sync()`/`.Async()` to pick one. A trigger
you state in full is passed to the plugin as is. The plugin-less `Build()` keeps whatever trigger you set.

`Execute<TPlugin>()` constructs the plugin (parameterless constructor, or the
`(string unsecureConfig, string secureConfig)` constructor with `null` for both, as `PluginPipeline` passes for a step
with no configuration) and returns it.

`ctx.PrimaryEntityId` is the `Target`'s id (an entity or an `EntityReference`), and `Guid.Empty` without a Target or
on a global message such as `Associate`. `WithPrimaryEntity(name, id)` sets it explicitly.

### Custom APIs and message-only plugins

`PluginContext.ForRequest(new DoThingRequest { Amount = 5 })` takes a typed request (an SDK request, or one
generated by `vela models --messages` or `--custom-apis`): the message and the input parameters come from it. On an
entity builder, `.WithRequest(request)` does the same. The message is the SDK message the platform runs the request
on (`CloseQuote` runs as `Close`). For a message with no request type, use
`PluginContext.ForMessage("vela_DoThing").WithInputParameter("amount", 5)`.

A Custom API's request is checked against its definition first, with the faults `PluginPipeline` raises: a missing
required input (`0x80040203`), an input of the wrong type or an undeclared one (`0x80040315`), a bound API without
its `Target`. To feed the API an invalid request on purpose, turn this off with `.WithoutRequestValidation()`. The
API's context has the primary entity of its binding: `"none"` for a global API, the bound table (and the Target id
for an entity-bound one) otherwise. `WithPrimaryEntity(...)` overrides it.

### What PluginContext does not apply

`PluginContext` runs the handler of the step the trigger selects, checking the message, stage, mode, table and
relationship scope as the runtime dispatch does. It does not apply the rest of the registration: `FilterOn`, the
registered images (it carries only the images you add), `.Disabled()`, the step's configuration and its
impersonation. It traces a warning in `TraceCapture`, prefixed `Vela.Testing:`, when an `Update` Target holds none of
the step's filtering attributes or when a registered image alias is missing from the context. To assert that a step
fires, or does not, use [`PluginPipeline`](#simulate-the-pipeline-cascade).

For the same reason, `PluginContext` cannot test several steps of one plugin that share the message, stage and mode
and differ by filter, images or step key. The runtime picks the handler from the executing step
(`OwningExtension`, whose name ends with the step key), and only the pipeline stamps it on each step it fires.

### Builder reference

| Area | Methods |
| --- | --- |
| Trigger | `OnCreate`, `OnUpdate`, `OnDelete`, `OnMessage`, `Stage`, `Mode`, `Sync`, `Async`, `Depth` |
| Data | `WithTarget`, `WithTargets`, `WithPreImage`, `WithPostImage`, `WithBulkPreImages`, `WithBulkPostImages`, `WithInputParameter`, `WithOutputParameter`, `WithRequest`, `WithPrimaryEntity` |
| Context | `WithSharedVariable`, `WithTag`, `WithParentContext`, `WithUserId`, `WithInitiatingUserId`, `WithCorrelationId`, `WithOrganizationId`, `WithEnvironmentId`, `WithTenantId`, `AsApplicationUser`, `WithClock` |
| Services | `WithOrganizationService`, `WithUserService`, `WithSystemService`, `WithManagedIdentityService`, `WithTracingService`, `WithLogger`, `WithService` |
| Capture | `TraceCapture` (a `RecordingTracingService`), `LogCapture` (a `RecordingLogger` of `ctx.Log*` entries and `ctx.LogMetric` metrics) |

`FakePluginExecutionContext` implements the full `IPluginExecutionContext2` to `IPluginExecutionContext7` chain, so
plugins reading newer context members work.

By default, `PluginContext` serves a hook-based fake `IOrganizationService` (see [Test doubles](#test-doubles)).
Pass an `InMemoryOrganizationService` with `WithOrganizationService(crm)` when the plugin reads or writes data:
`ctx.Service` is then the store as its configured user, and `ctx.SystemService` the store as SYSTEM. Under
`WithSecurity(...)` the first is checked against the user's roles and the second is not.

## Replacing services

A plugin that registers its own services in `ConfigureServices` (or in an `[assembly: VelaServiceRegistrar]`, see
[Dependency injection](dependency-injection.md)) gets a test double from the test with `WithService<T>(instance)`,
on `PluginContext` and on `PluginPipeline`, without changing the plugin. The plugin resolves the test's instance for
`T`, the same one for the whole execution, whatever the registrar and `ConfigureServices` registered for that type.
On `PluginPipeline` it applies to every plugin and Custom API the pipeline runs, nested executions included.

A plugin that calls an HTTP API through the `HttpClient` it registers:

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
    => services.AddScoped(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
```

is tested against a fake `HttpMessageHandler`:

```csharp
sealed class FakeHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42") });
}

PluginPipeline pipeline = PluginPipeline.Empty()
    .Register<AccountScoringPlugin>()
    .WithService(new HttpClient(new FakeHandler()));
```

The same works for your own API client: `.WithService<IContactApi>(new FakeContactApi())`. Calling `WithService` again
for the same type replaces the earlier instance. It replaces registrations in Vela's built-in resolver
(`ctx.GetRequiredService<T>()`); a container the plugin builds itself (for example with `AddPluginRuntime`) is not
affected.

`WithService` does not take the platform services, which the plugin context reads from the platform rather than
from the resolver. It throws an `ArgumentException` naming the method to use instead:

| Service | `PluginContext` | `PluginPipeline` |
| --- | --- | --- |
| `IManagedIdentityService` | `WithManagedIdentityService` | `WithManagedIdentityService` |
| `ITracingService` | `WithTracingService` | recorded in `Trace` |
| `ILogger` (telemetry) | `WithLogger` | recorded in `LogCapture` |
| `IOrganizationService`, `IOrganizationServiceFactory` | `WithOrganizationService`, `WithUserService`, `WithSystemService` | the pipeline's in-memory store |
| `IPluginExecutionContext` (and `2` to `7`) | the builder's context methods | built by the pipeline for each step |

## Test doubles

| Type | Purpose |
| --- | --- |
| `FakePluginExecutionContext` | A mutable execution context (full interface chain). |
| `HookOrganizationService` | Delegate hooks `OnCreate`, `OnRetrieve`, ... (an unset hook throws `NotSupportedException` naming it). |
| `HookOrganizationServiceFactory` | Hands out the user and system services. |
| `RecordingTracingService` | Captures `ctx.Trace(...)` for assertions. |
| `RecordingLogger` | Captures `ctx.Log*` entries and metrics. |
| `InMemoryOrganizationService` | A stateful in-memory Dataverse (below). |

`WithOrganizationService(...)` also takes any other `IOrganizationService`: your own fake, or one from a mocking
library.

## In-memory Dataverse

`InMemoryOrganizationService` stores records, so Create, Retrieve, Update and Delete round-trip:

```csharp
var crm = new InMemoryOrganizationService()
    .WithAlternateKey("account", "accountnumber")      // a duplicate key throws
    .WithAutoNumber("task", "ticketnumber", "T-", 3)    // set on create: T-001, T-002, ...
    .WithCurrentUser(currentUserId);                    // also for ConditionOperator.EqualUserId

Guid id = crm.Create(new Account { Name = "Zava" });
crm.Update(new Account { Id = id, Name = "Renamed" });
Account stored = crm.Retrieve<Account>(id, a => a.Name);
```

`PluginPipeline` runs on its own store and takes the same configuration methods. `Seed(...)` writes starting
records without firing plugins, checking privileges or stamping system columns. `GetAll(table)` returns a table's
records.

### Relationships

For 1:N, set the lookup with `Seed` or `Update` (`account.primarycontactid = new EntityReference(...)`). A lookup is
just **data**: the child carries an `EntityReference` to its parent, and that alone drives queries, `link-entity`
joins and filtering. Declare the relationship with `WithParentalRelationship(...)`,
`WithReferentialRelationship(...)` or `WithRelationship(...)` only when the test exercises a **cascade or inherited
access** (see [Security model in tests](security-model.md)). Once declared, `Associate` and `Disassociate` over it
set and clear the child's lookup. Pass the relationship's real schema name to find it by that name in `Associate`,
`Disassociate`, a deep insert and a `RelatedEntitiesQuery`; two relationships cannot share a schema name. Without
one, the schema name is `{referenced}_{referencing}` (`account_contact`):

```csharp
var crm = new InMemoryOrganizationService()
    .WithParentalRelationship("account", "contact", "parentcustomerid", schemaName: "contact_customer_accounts");

crm.Associate("account", accountId, new Relationship("contact_customer_accounts"),
    new EntityReferenceCollection { new EntityReference("contact", contactId) });
```

For N:N, declare the relationship once with `WithManyToMany(...)` and use `Associate`/`Disassociate`: they create
and delete the intersect rows, so `RetrieveMultiple` joins through them as in Dataverse:

```csharp
var crm = new InMemoryOrganizationService()
    .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

crm.Associate("account", accountId, new Relationship("account_contacts"),
    new EntityReferenceCollection { new EntityReference("contact", contactId) });

// The account's contacts, joined through the intersect table:
var q = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
var link = q.AddLink("account_contact", "contactid", "contactid", JoinOperator.Inner);
link.LinkCriteria.AddCondition("accountid", ConditionOperator.Equal, accountId);
crm.RetrieveMultiple(q);
```

Associating a pair that is already associated faults with `0x80040237`, and the records must belong to the
relationship's two tables; an undeclared relationship throws. With early-bound
types, a typed overload takes the table and column names from the types and the property accesses:

```csharp
crm.WithManyToMany<AccountContact, Account, Contact>("account_contacts", ac => ac.AccountId, ac => ac.ContactId);
```

`WithManyToMany(...)` is optional when the relationship is in registered metadata: declare it on a
`FakeEntityMetadata` with `.ManyToMany(schema, intersect, e1, e1Attr, e2, e2Attr)`, and `Associate`/`Disassociate`
derive the intersect mapping from it.

### Bulk and batch requests

`Execute(...)` handles the `Create`, `Update`, `Delete`, `Retrieve` and `RetrieveMultiple` requests,
`CreateMultipleRequest`/`UpdateMultipleRequest`, and `ExecuteMultipleRequest`/`ExecuteTransactionRequest`.
`ExecuteMultiple` honours `ContinueOnError` and `ReturnResponses`; `ExecuteTransaction` rolls the whole batch back
if any request throws. When the failing request raised a platform fault, the `ExecuteTransaction` faults with an
`ExecuteTransactionFault` detail that carries that fault's `ErrorCode` and message and the request's
`FaultedRequestIndex`. A plugin's `InvalidPluginExecutionException` surfaces as itself, as it does for a single
request.

### File and image columns

The block protocol works over an in-memory blob store: `InitializeFileBlocksUpload`, `UploadBlock`,
`CommitFileBlocksUpload` (returns a `FileId`, which the File column then stores), `InitializeFileBlocksDownload`,
`DownloadBlock` (returns the content) and `DeleteFile`. These messages have no typed request class in the referenced
SDK, so call them with a plain `OrganizationRequest("InitializeFileBlocksUpload") { Parameters = { ... } }`, or with
Vela's file helpers (`service.DownloadFile(...)` and the other helpers of `Vela.Files`). An Image column that holds bytes inline is downloaded directly
(its bytes are the thumbnail); a plain `byte[]` or file id attribute round-trips through Create, Update and
Retrieve.

Dataverse downloads only an Image column that stores full images: otherwise (`account.entityimage`, or a column
created without "Can store full images") `InitializeFileBlocksDownload` fails with `0x80040217`. The harness cannot
tell an Image column from inline bytes on its own, so register the column to get that check:

```csharp
svc.WithEntityMetadata(FakeEntityMetadata.For("account")
    .PrimaryId("accountid")
    .Image("new_img", canStoreFullImage: false)
    .Build());
// svc.DownloadFile(target, "new_img") now throws FaultException<OrganizationServiceFault> (0x80040217).
```

On the platform, the setting in force when the image was set decides; the harness reads it at download time.

### System columns

Create and Update set the standard columns the platform sets, so plugins that read or filter on them behave as
they do online:

- **Audit:** `createdon`/`createdby` on Create, `modifiedon`/`modifiedby` on Create and every Update, stamped with
  the calling user (`WithCurrentUser(...)`). Without one, a stable non-empty default user
  (`InMemoryOrganizationService.DefaultCallingUser`) is used instead of the empty GUID. A value the caller supplies
  for these columns, or for `owningbusinessunit`, is ignored on Create and Update. To arrange a record with given
  audit values, `Seed(...)` it.
- **Owner (user- or team-owned tables):** `ownerid`, `owninguser` or `owningteam`, and `owningbusinessunit` (from
  `WithCurrentBusinessUnit(...)`). A caller-supplied `ownerid` is kept and the owning columns are derived from it.
  Assign by updating `ownerid` to a `team` reference (`owningteam` follows) or with
  `Execute(new AssignRequest { Target = ..., Assignee = new EntityReference("team", id) })`.
- **Organization (organization-owned tables):** `organizationid` (from `WithOrganizationId(...)`), no owner columns.
  On a table declared with `AsOrganizationOwned(...)` or registered as organization-owned by its metadata, a Create
  or Update that sets `ownerid` faults with `0x80041103`, and `Assign` faults with `0x80040800`.
- **State:** `statecode` (0) and `statuscode` (1) by default on Create, so plugins see an active record. A
  `statecode` supplied without a `statuscode` is ignored; supplied together, both are kept.
- **Row version:** `versionnumber` is set on Create and increased on every Update.
- **Backdating:** a caller-supplied `overriddencreatedon` sets `createdon` on Create, as in a data migration, and
  `overriddencreatedon` then holds the actual creation time.
- **Timestamps** use `DateTime.UtcNow`. Pin them with `WithClock(() => fixedInstant)` to assert
  `createdon`/`modifiedon`. The same clock expires the plugin cache (`IPluginCacheService`): each harness has its
  own cache, so move the clock past a TTL instead of sleeping (see [Caching](caching.md#test-a-ttl)).
- **Ids** are random `Guid`s. Make them reproducible with `WithIdSequence()` (`00000000-0000-0000-0000-000000000001`,
  `...0002`, ...; assert with `SequentialIds.At(n)`) or `WithIdSequence(() => myGuid)` for your own generator.

The **ownership type** is resolved per table, in this order: a declaration (below); the `OwnershipType` of metadata
registered with `WithEntityMetadata(...)`; the early-bound type of the row (a generated class has an `OwnerId`
property only for user- or team-owned tables), also through `PluginPipeline`; otherwise user-owned. Declare it to
override the inference or to model the special table types:

```csharp
var crm = new InMemoryOrganizationService()
    .WithCurrentUser(userId)
    .WithCurrentBusinessUnit(businessUnitId)
    .WithOrganizationId(organizationId)
    .AsOrganizationOwned("savedquery")            // audit columns and organizationid, no owner columns
    .AsVirtual("zava_externaldata")               // virtual table: no system columns at all
    .AsVirtual("zava_readonly", readOnly: true)   // read-only provider: Create, Update and Delete are rejected
    .AsElastic("zava_telemetry");                 // elastic is a storage type: audit and owner columns
```

`AsUserOwned(table)` declares a user-owned table explicitly.

### Table metadata

Plugins that read metadata (`RetrieveEntityRequest`, `RetrieveAllEntitiesRequest`, `RetrieveAttributeRequest`) work
once you register the tables they read. Build the metadata with `FakeEntityMetadata`, which handles the SDK's
read-only metadata setters:

```csharp
var crm = new InMemoryOrganizationService()
    .WithEntityMetadata(
        FakeEntityMetadata.For("account")
            .PrimaryId("accountid")
            .PrimaryName("name")
            .Ownership(OwnershipTypes.UserOwned)
            .Attribute("name", AttributeTypeCode.String)
            .Picklist("statuscode", (1, "Active"), (2, "Inactive"))
            .Build());

var response = (RetrieveEntityResponse)crm.Execute(
    new RetrieveEntityRequest { LogicalName = "account", EntityFilters = EntityFilters.All });
EntityMetadata account = response.EntityMetadata; // PrimaryIdAttribute, Attributes, OwnershipType, ...
```

Only what you register is returned; an unregistered table throws.

To derive the metadata from an early-bound type instead, use `FakeEntityMetadata.From<TEntity>()`, or the shorthand
`WithEntityMetadata<TEntity>()`. It reflects over a class generated by `vela models`:

- each `[AttributeLogicalName]` property becomes an attribute (its CLR type gives the `AttributeTypeCode`);
- generated choice enums become picklists;
- the `{logicalname}id` column is the primary id, and a `name` column the primary name;
- the ownership is inferred from an `ownerid` column: user-owned with one, organization-owned without;
- the maximum length comes from the generated `[StringLength]` annotation.

```csharp
var crm = new InMemoryOrganizationService().WithEntityMetadata<Account>();
```

`From<TEntity>()` returns the same builder, so you can set what the early-bound type does not carry: select an
attribute with `On(...)`, then set the facet:

```csharp
.WithEntityMetadata(
    FakeEntityMetadata.From<Account>()
        .On("name").MaxLength(160)
        .On("primarycontactid").Targets("contact")
        .Build());
```

Set only this way (`On(...)` and a facet setter, or `For(...)`): lookup targets, required level, numeric range, and a
primary-name column other than `name`. Not derived at all: multi-select choices, file and image columns, and party
lists.

### Queries

`RetrieveMultiple` runs `QueryExpression`, `QueryByAttribute` and `FetchExpression` (a common subset of FetchXML):

```csharp
var query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
var link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
link.EntityAlias = "acct";
link.Columns = new ColumnSet("name");
query.Criteria.AddCondition("fullname", ConditionOperator.BeginsWith, "J");

Entity contact = crm.RetrieveMultiple(query).Entities.Single();
object name = contact.GetAttributeValue<AliasedValue>("acct.name").Value; // "Zava"
```

[Typed queries](queries.md) (`Query.For<T>()`) build the same `QueryExpression` and run the same way.

Supported:

- filters (And/Or), operators including Between, Begins/Ends/Like, the date operators, EqualUserId and column
  comparisons;
- conditions on linked tables (for example, records not in a set);
- link-entities: Inner, LeftOuter, Exists, In, MatchFirstRowUsingCrossApply, Any/All/NotAny/NotAll, with aliased
  columns, criteria and nesting;
- ordering (linked columns included), TopCount, paging, Distinct, total record count and column aliases;
- aggregates (count, countcolumn, sum, avg, min, max, with group-by), on linked columns and over joins too.

Null values and paging follow Dataverse. A null never matches in a join or an Any/All filter: a row whose link
column is empty is joined to nothing. `ne`, `not-in`, `not-like`, `not-begin-with` and `not-end-with` match a row
whose column is null; the comparisons, `not-between`, `like` (even `'%'`) and `ne-userid` do not. Distinct compares
text without case. A query with no page size returns a page of at most 5,000 rows with `MoreRecords`, and no page is
larger. The engine pages by page number: the paging cookie is not read (Dataverse also returns the same page again
when the page number is not incremented).

FetchXML is parsed into the equivalent `QueryExpression` and runs through the same engine, so a query and its
`ToFetchXml()` return the same rows. The parser is strict: anything it does not model throws
`NotSupportedException` naming the element or attribute, and is never ignored silently. A condition `<value>` is
read as the type of the column it is compared with, in the invariant culture (`2026-10-09` on a date column,
`10.50` on a money column, `{guid}` on a lookup, `1` on a boolean); a value that is not valid for that type throws
instead of matching nothing.

| FetchXML | Supported |
| --- | --- |
| `<fetch>` | `aggregate`, `distinct`, `top`, `count`, `page`, `returntotalrecordcount`, `aggregatelimit`; accepted as no-op hints: `no-lock`, `latematerialize`, `options`, `useraworderby`, `paging-cookie`, `mapping`, `version`, `output-format` |
| `<entity>` | `name`; children `attribute`, `all-attributes`, `filter`, `order`, `link-entity` |
| `<attribute>` | `name`, `alias`; in an aggregate: `aggregate` (count/countcolumn/sum/avg/min/max), `groupby`, `dategrouping` (day/month/quarter/year), `distinct` (countcolumn), `usertimezone` (the store is UTC) |
| `<link-entity>` | `name`, `from`, `to`, `alias`, `link-type` (inner, outer, exists, in, matchfirstrowusingcrossapply, and any/not any/all/not all, also inside a `<filter>`); `visible`/`intersect` accepted; children as `<entity>` |
| `<filter>` | `type` (and/or); `hint`/`isquickfindfields` accepted; children `condition`, `filter`, `link-entity` |
| `<condition>` | `attribute`, `operator`, `value` / `<value>`, `entityname` (a linked column, selected or not), `valueof` (column comparison: eq/ne/gt/ge/lt/le); `uiname`/`uitype`/`uihidden` accepted |
| `<order>` | `attribute`, `descending`, `entityname` (root only); `alias` in an aggregate |

Rejected: `datasource` (retained data), `rowaggregate`, `link-type='natural'`, week, fiscal-period and fiscal-year
date grouping, and every other element or attribute.

## Error codes

The faults the harness raises itself carry the platform `ErrorCode`:

| Fault | Code |
| --- | --- |
| Record not found (`ObjectDoesNotExist`) | `0x80040217` |
| Duplicate alternate key | `0x80040237` |
| Missing privilege (`PrincipalPrivilegeDenied`), including a read of a table the caller cannot read, at the root or in a link-entity | `0x80040220` |
| Record out of the caller's reach on `Retrieve` | `0x80048306` |
| Aggregate limit exceeded | `0x8004E023` |
| Alternate key: not a declared key / no match | `0x80060889` / `0x80060891` |
| Custom API: missing required input / wrong type or undeclared input | `0x80040203` / `0x80040315` |
| No active transaction / open transaction count reduced | `-2147220911` / `0x8009000C` |

An `ExecuteMultiple` child fault keeps its inner fault's code. Any other exception surfaces as an
`OrganizationServiceFault` with no `ErrorCode`.

## What the harness does not simulate

- **The data effect of business messages** (`QualifyLead`, `WinOpportunity`, `SendEmail`, `CloseIncident` and the
  like). The harness fires the steps registered on them, so you can test your own logic, but it does not make the
  record changes the platform would make (see [Messages](#messages)). As on the platform, it fires the steps of the SDK message the request runs
  as (`Send` for a `SendEmailRequest`), on the table the message runs on (`queueitem` for `AddToQueue`, whatever its
  `Target`), with an empty `ctx.PrimaryEntityId`, so a step registered on the wrong message or table does not fire in
  a test either.
- **Query hints and paging cookies.** `no-lock` and the other hints are accepted and ignored, and paging uses the
  page number only.
- **Formula, rollup and calculated columns, business rules and duplicate detection.**
- **Custom workflow activities.** The harness runs no workflows, so a `CodeActivity` is not supported (see
  [Limitations](../reference/limitations.md#velatesting)).

For anything else, pass your own `IOrganizationService`. For the limits of the whole framework (runtime, CLI and
this harness), see [Limitations](../reference/limitations.md).
