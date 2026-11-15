# Testing with Vela.Testing

`Vela.Testing` runs plugins and Custom APIs **without a Dataverse connection** and **without a mocking library**.
It has two levels:

- `PluginPipeline` runs **the Dataverse pipeline in memory**: writes fire the registered steps, cascade, and roll
  back as on the platform.
- `PluginContext` builds a fake execution context and runs **one plugin** against it.

`PluginPipeline` always runs on its own `InMemoryOrganizationService`. `PluginContext` serves a hook-based fake
`IOrganizationService` by default; pass an `InMemoryOrganizationService` (a stateful in-memory CRM) or your own
service with `WithOrganizationService` when the plugin reads or writes data.

```powershell
dotnet add package Vela.Testing
```

## Which one to use

Test with `PluginPipeline` by default. It runs what Dataverse runs for a request: every registered step with its
filtering attributes and images, in stage and execution order across plugins, the cascade of the plugins' own writes,
the rollback of a synchronous failure, and the asynchronous steps after the commit. Several steps of one plugin on the
same trigger, told apart by their filter, images or step key, are tested this way: `PluginContext` does not choose
between them.

Use `PluginContext` for the edge cases the pipeline does not easily produce, as a unit test of one handler:

- a `Depth` of 2 or more, or a crafted `ParentContext`;
- shared variables set by another plugin;
- an invalid Custom API input, to exercise the API's own checks (`.WithoutRequestValidation()`);
- one specific error branch of a handler.

## Simulate the pipeline (cascade)

`PluginPipeline` simulates the Dataverse pipeline **in memory**: writes through its service auto-fire the
registered plugins (pre/post stages, execution order, filtering attributes, pre/post images), and a plugin that
writes triggers further plugins (**cascade**).
Registrations are **discovered automatically** from the assembly via `GetStepDefinitions()`. A registration
that would never fire fails the setup instead of leaving the tests green: `FromAssembly` throws when a step or
Custom API names a type it cannot resolve, and `Register<T>()` throws for a type that declares no step and no
Custom API.

```csharp
var pipeline = PluginPipeline.FromAssemblyOf<AccountPostCreate>()
    .WithAutoNumber("task", "ticketnumber", "T-", 3);

pipeline.Service.Create(new Account { Name = "Zava" });   // fires Create-account plugins, then cascades

Assert.Single(pipeline.GetAll("task"));        // assert on the resulting CRM state
Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("AccountPostCreate")); // and on what ran
```

An `ExecutionLog` entry means the step was **invoked by the platform**, not that its handler ran. Vela's runtime
can still skip the handler: a `.ForRelationship(...)` step is invoked for every relationship, and
`.SkipWhenDepthAbove(n)` or the plugin-level depth guard stop it past a depth. Such an entry has `HandlerInvoked`
set to `false` and `SkipReason` set to the line the runtime traced. (An execution that fails before reaching the
handler, in the plugin's constructor or its service registration, also has `HandlerInvoked` `false`, with no
`SkipReason`.)

```csharp
pipeline.Service.Associate("account", accountId, new Relationship("account_partners"), contacts);

PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("AccountContactsPlugin"));
Assert.False(entry.HandlerInvoked);   // invoked, but outside its ForRelationship scope
Assert.Contains("ForRelationship", entry.SkipReason);
```

- **Transactions follow the platform's
  [stage model](https://learn.microsoft.com/power-apps/developer/data-platform/event-framework#event-execution-pipeline).** The PreValidation stage
  of the initial operation runs **before** the database transaction (`IsInTransaction` is `false`), so a write a
  PreValidation plugin makes commits on its own even if the operation fails later. The transaction opens before
  PreOperation; any operation a plugin triggers from inside it (including that operation's own PreValidation stage,
  at depth 2) joins it, and a synchronous failure anywhere rolls the whole transaction back. A plugin's own
  operations reach their PreValidation steps with the extra input `InputParameters["FromSandbox"] = true`. Reads
  (`Retrieve`/`RetrieveMultiple`) are not transactional: their steps see `IsInTransaction` = `false`. A plugin that
  catches the failure of one of its **writes** cannot carry on: its next service call fails with "There is no active
  transaction" (`-2147220911`) and returning normally fails the operation with "ISV code reduced the open transaction
  count" (`0x8009000C`, `-2146893812`), as on the platform. A caught failed **read** (a Retrieve of a record that
  does not exist) does not end the transaction. `ExecuteTransaction` runs all its children inside one transaction.
- Synchronous-plugin exceptions **roll the operation back** (snapshot/restore); **asynchronous** steps run
  inline once the outermost operation (or `ExecuteTransaction` batch) commits, and never when it rolls back, as on
  Dataverse where a rolled-back operation creates no async job. An asynchronous step's exception does **not**
  surface to the caller (matching Dataverse, where async steps are decoupled system jobs) - it is recorded on
  `pipeline.AsyncStepFailures` so you can still assert an async step failed, and the step's own writes are rolled
  back. `ctx.IsInTransaction` is false for async steps, read steps and a depth-1 PreValidation step, true otherwise
  (a PreValidation step inside an `ExecuteTransaction` is in its transaction).
- **Execution-context ids.** `CorrelationId` is one id per top-level operation (an `ExecuteTransaction` batch shares
  one, each `ExecuteMultiple` child gets its own), kept by the async steps it queues. `RequestId` is one id per call
  into the pipeline; `OperationId` equals it on sync steps and is the async step's own job id on async steps.
  `IsolationMode` is 2 (sandbox) and `SecondaryEntityName` is "none", as on Dataverse.
- The steps work on a copy of the entity you pass to `Service.Create`/`Update`: your object never receives the
  created id or a plugin's change to `Target`. Read the id from the return value of `Create`. Cascade depth is capped at the
  Dataverse default of 8 (`WithMaxDepth` changes it; the one-hour window of the platform's loop detection is not
  modelled), on top of the plugin's own depth guard.
- **Asynchronous step context.** As on the platform, an async step's context has an `OwningExtension` with
  the step's id but no `Name` (a sync step's carries its name), and its post images include
  `AsynchronousStepPrimaryName` (the record's primary name and id).
- **Execution-context identity.** The plugin's `ctx` reports the configured caller: `UserId`,
  `InitiatingUserId`, `BusinessUnitId` and `OrganizationId` come from `WithCurrentUser(...)` /
  `WithCurrentBusinessUnit(...)` / `WithOrganizationId(...)`, consistent with what `WhoAmI` and the
  `createdby`/`ownerid` stamping report (not random values).
- **Step registration is honoured.** A `.Disabled()` step never fires. The plugin is constructed with the step's
  `WithUnsecureConfig(...)` value and, for `WithSecureConfigFrom("Name")`, the value you supply with
  `pipeline.WithSecureConfig("Name", "value")` (what `vela apply --secure-config Name=value` provides in
  production; unset configuration is passed as `null`). A step registered with `WithImpersonation(userId)` runs with
  `ctx.UserId` set to that user, and its `ctx.Service` acts as that user (privileges under `WithSecurity`, audit
  stamping); `InitiatingUserId` stays the caller.
- **User vs SYSTEM service.** `ctx.Service` acts as `ctx.UserId` and is subject to the security model when
  `WithSecurity` is used; `ctx.SystemService` is SYSTEM and is not (audit columns still record the calling user). A
  plugin fired by a write made through `ctx.SystemService` runs as SYSTEM: its own `ctx.Service` is unrestricted too.
- **`ParentContext` and shared variables.** A PreOperation/PostOperation step of a `Create`, `Update` or `Delete`
  has the operation's own stage-30 (MainOperation) context as `ParentContext`, as on the platform; the context of the
  plugin whose write triggered the operation is one level further up (`ParentContext.ParentContext`), and a PreValidation
  step's `ParentContext` is that calling plugin's context directly. Shared variables set by a PreValidation step are
  **not** in the later steps' own `SharedVariables`: they are visible only through `ParentContext.SharedVariables`.
  A cascaded operation inherits the shared variables of the plugin that issued it. To avoid
  re-entrancy, see [Preventing recursion](plugins.md#preventing-recursion).
- **Not-found faults.** Retrieving or updating a record that does not exist throws
  `FaultException<OrganizationServiceFault>` with the platform `ObjectDoesNotExist` code (`-2147220969`), like a
  real `IOrganizationService`, so a plugin that catches that fault code is exercised faithfully.
- **Custom APIs** run through `Service.Execute(new OrganizationRequest("vela_MyApi"){ ["x"] = 1 })`, as the full
  pipeline: the steps registered on the API's message (`On<vela_MyApiRequest>()`) fire at PreValidation,
  PreOperation and PostOperation around the API's own plugin (the MainOperation stage), as far as its
  `AllowedCustomProcessingSteps(...)` allows (a step it does not allow fails the call, since Dataverse would refuse to
  register it). The request is checked before any step runs, PreValidation included, with the platform's faults: a
  missing required input throws `0x80040203` ("Required field 'X' is missing for RequestName='...'"), an input of the
  wrong type or an undeclared input throws `0x80040315` ("Input field type 'Int32' does not match expected type
  'String' for field 'X'", "Unrecognized request parameter: X"). The main operation runs at stage 30 under a step
  named `CustomApi '<uniquename>' implementation` (its `OwningExtension`). A bound API's context has the bound table
  as `PrimaryEntityName` and the Target id as `PrimaryEntityId`; a global API's main operation sees `"none"`.
- **Bulk (`CreateMultiple`/`UpdateMultiple`)** fire steps the way Dataverse merges the two messages: a
  `CreateMultiple`/`UpdateMultiple` request runs your single `Create`/`Update` steps **once per row**, and a
  step registered with `OnCreateMultiple()`/`OnUpdateMultiple()` runs **once with the whole batch** (read it
  via `ctx.BulkTargets`, with per-row pre/post images on `ctx.BulkPreEntityImagesCollection` /
  `ctx.BulkPostEntityImagesCollection`, which also hold the plugin's other steps' images and the platform's
  `PreBusinessEntity` / `PostBusinessEntity`, as on Dataverse: read images by alias). The merge
  runs both ways, so a bulk step also fires for a plain `Create`/`Update` (a one-row batch). The whole batch is
  one transaction: a synchronous failure rolls every row back.
- **Owner, state, `Assign` and `SetState`** follow what the platform does:
  - An `Update` is **one** execution, even when it mixes `ownerid` or `statecode`/`statuscode` with other fields.
    An `ownerid` Update never fires the `Assign` steps.
  - An `Update` that sets `statecode` also fires the `SetStateDynamicEntity` steps, nested at depth + 1 between the
    Update's PreOperation and PostOperation. Their `Status` is -1 when the Update sets no `statuscode`; the record
    then lands on the state's default status, and the Update's Target stays as sent.
  - `Execute(new AssignRequest { ... })` fires the `Assign` steps (PreValidation, PreOperation), then an `ownerid`
    Update at the same depth that fires the Update steps at PreOperation and PostOperation only, then the `Assign`
    PostOperation steps.
  - `Execute(new SetStateRequest { ... })` fires the `SetStateDynamicEntity` steps the same way around a
    `statecode`/`statuscode` Update. Steps registered on `SetState` never fire, on the platform or in the harness.
  - Status -1 (or a missing status) resolves to the state's default status: state 0 gets status 1, any other state
    status 2, unless you declare it with `WithDefaultStatus("table", state, status)`.
- **Shared variables / `tag`.** A client passes the `tag` via a named request (not the shortcut methods, per
  the platform); the pipeline routes `Execute(CreateRequest/UpdateRequest/...)` through the registered steps
  and exposes it as a shared variable. Read it with `ctx.Tag` (or `ctx.GetSharedVariable("key")`):

  ```csharp
  // plugin: read the client tag
  private void OnAccountCreated(OnAccountCreatedContext ctx)
  {
      if (ctx.Tag == "bulk-import") { /* skip enrichment for imports */ }
  }

  // test: pass it on the request
  var request = new CreateRequest { Target = new Account { Name = "Zava" } };
  request["tag"] = "bulk-import";
  pipeline.Service.Execute(request);
  ```

  For a single-plugin unit test, set it on the context builder instead:
  `PluginContext.For<Account>()....WithTag("bulk-import")`.
- **Bypass parameters** on an `Execute(...)` request suppress steps the way the platform does:
  `BypassCustomPluginExecution = true` skips every step; `BypassBusinessLogicExecution = "CustomSync"` /
  `"CustomAsync"` skips by execution mode; and `BypassBusinessLogicExecutionStepIds` skips specific steps
  (matched against the step **key**: pass a comma-separated string or a collection of keys). The data is still
  written; only the plugins are skipped. They apply at any depth: a request's effective bypass is the one it
  inherits (from an enclosing request or an `ExecuteMultiple`/`ExecuteTransaction` batch) plus its own, so a plugin
  that writes with `BypassCustomPluginExecution = true` does not re-enter itself.
- **Associate/Disassociate** fire their registered plugins whatever the entity (they are global messages, as on
  the platform: a step declared on `account` also fires for an association made from `contact`), and the N:N
  intersect rows persist when the relationship is declared with `WithManyToMany(...)` (or derived from registered
  metadata); the store has no live relationship metadata otherwise.
- **Upsert and alternate keys.** `Execute(new UpsertRequest { ... })` matches the target by id or alternate key
  (declared with `WithAlternateKey(...)`): an existing record runs the Update steps, a new one runs the Create steps,
  and the response reports `RecordCreated` and `Target`. `Update`/`Delete` addressed by `KeyAttributes` resolve the
  key first, so their steps see the real record id. The `KeyAttributes` must be exactly one declared key, as on
  Dataverse: any other set of columns faults with `0x80060889`, and a declared key that matches nothing with
  `0x80060891`.
- **Reads run through the pipeline.** `Retrieve`/`RetrieveMultiple` (the shortcut methods and the
  `RetrieveRequest`/`RetrieveMultipleRequest` messages) fire their registered steps. A PreValidation/PreOperation step
  that replaces `InputParameters["ColumnSet"]`, `["Target"]` or `["Query"]` changes what is read, and a PostOperation
  step sees the result as `OutputParameters["BusinessEntity"]` / `["BusinessEntityCollection"]` (the platform's keys)
  and can replace or change it. The same applies to any other message: a synchronous step's changes to the inputs
  reach the main operation and its changes to the outputs reach the caller (the caller's request object is not
  modified).
- **Returned records have the platform's shape.** A read always returns the primary key column (`{table}id`,
  `activityid` for activities, or the `PrimaryIdAttribute` of registered metadata), a lookup carries the referenced
  record's primary name in `EntityReference.Name` and in `FormattedValues`, and a choice carries its label in
  `FormattedValues` when its options are registered with `WithEntityMetadata(...)`.
- **Isolation.** The pipeline works on copies of what you send: after `Create`, your entity's `Id` is still empty
  (use the returned id) and a plugin's change to the Target is not visible on your object, as with a real service.
  Like the platform, which caches plugin instances, each step (and each Custom API) runs on **one plugin instance
  reused across executions**, so a plugin that keeps state in fields misbehaves in the test as it would in
  production. A modelled message whose store simulation fails (an invalid `Merge`, a missing record) fails even when
  steps are registered on it; only a message the store does not model at all runs its steps around a no-op.
- Use `PluginPipeline.Empty().Register<MyPlugin>()` to scope a test to specific plugins.
- **Pipeline configuration.** `PluginPipeline` forwards the store's configuration (`WithSecurity`,
  `WithStrictSecurity`, `WithAlternateKey`, `WithAutoNumber`, `WithDefaultStatus`, relationships, metadata,
  ownership, clock and id sequence), and adds `Seed(...)`, `WithSecureConfig(name, value)`, `WithMaxDepth(n)`,
  `WithManagedIdentityService(service)` (for every plugin and Custom API it runs, nested executions included; see
  [Managed identity](managed-identity.md#testing)) and `WithService<T>(instance)`
  ([Replacing services](#replacing-services)).
  Assert with `GetAll(table)`, `ExecutionLog`, `AsyncStepFailures`, `Trace` and `LogCapture` (the whole cascade's
  `ctx.Log*` entries).

## Run a plugin against a fake context

`PluginContext` is for the edge cases listed in [Which one to use](#which-one-to-use). The fluent builder is
strongly typed. The trigger (message/stage/mode) comes from the plugin's own registration, so you only state what
tells its steps apart.

```csharp
using Vela.Constants;
using Vela.Context;
using Vela.Testing;

// Construct the plugin (a Vela plugin takes its unsecure and secure configuration), run it, then assert.
AccountPostUpdate plugin = new AccountPostUpdate(string.Empty, string.Empty);
PluginContext.For<Account>()
    .WithTarget(new Account { Id = accountId, Name = "Zava" })
    .WithPreImage(new Account { Name = "Old name" })
    .Execute(plugin);

// ...or build a LocalPluginContext for direct assertions (restate the trigger to override the default):
LocalPluginContext ctx = PluginContext.For<Account>()
    .OnUpdate().Stage(PluginStages.PostOperation).Sync()
    .WithTarget(new Account { Name = "Zava" })
    .Build();
```

`Execute`, `Build<MyPlugin>()` and `Build(plugin)` fill the parts of the trigger you did not set from the registered
steps that match the parts you did set (and the primary entity): `.OnCreate()` alone is enough when the plugin has
one `Create` step. When several steps still match (a `Create` step at PreOperation and another at PostOperation),
or none does, they throw an `InvalidOperationException` that lists the registered steps
(`Create/PreOperation/Sync on account [step-key]`); add `.Stage(...)` or `.Sync()`/`.Async()` to pick one. A trigger
you state in full is passed to the plugin as is. The plugin-less `Build()` keeps whatever trigger you set.

`ctx.PrimaryEntityId` is the `Target`'s id (an entity or an `EntityReference`), and `Guid.Empty` without a Target or
on a global message such as `Associate`. `WithPrimaryEntity(name, id)` sets it explicitly.

`.Execute<AccountPostUpdate>()` constructs the plugin for you (parameterless constructor, or the
`(string unsecureConfig, string secureConfig)` constructor with empty configuration) and returns it.

**Several steps on one trigger.** When two steps of a plugin share the message, stage and mode and are told apart
by their filter, images or step key, test them with [`PluginPipeline`](#simulate-the-pipeline-cascade). The runtime picks the
handler from the executing step (`OwningExtension`), whose name ends with the step key; the pipeline fires the steps
whose filter the write matches and stamps each one's `OwningExtension`. `PluginContext` does not choose between them.

**Custom APIs / message-only plugins:** `PluginContext.ForRequest(new DoThingRequest { Amount = 5 })` takes the
typed request (an SDK request, or one generated by `vela models --messages` / `--custom-apis`): the message and the
input parameters come from it, so the test states no message name or parameter key. On an entity builder,
`.WithRequest(request)` does the same. The message is the sdkmessage the platform runs the request on
(`CloseQuote` runs as `Close`). The untyped `PluginContext.ForMessage("vela_DoThing").WithInputParameter("amount", 5)`
stays for a message with no request type. Running a Custom API checks its request against its definition first,
with the faults `PluginPipeline` raises: a missing required input (`0x80040203`), an input of the wrong type or an
undeclared one (`0x80040315`), a bound API without its `Target`. A test that feeds the API an invalid request on
purpose, to exercise the API's own checks, turns this off with `.WithoutRequestValidation()`. The API's context
has the primary entity of its binding, as in `PluginPipeline`: `"none"` for a global API, the bound table (and the
Target id for an entity-bound one) otherwise; `WithPrimaryEntity(...)` overrides it.

**What PluginContext does not apply.** `PluginContext` runs the handler of the step the trigger selects; it checks
the message, stage, mode, entity and relationship scope, as the runtime dispatch does. It does not apply the rest of
the registration: `FilterOn`, the registered images (it carries only the images you add), `.Disabled()`, the step's
configuration and its impersonation. It traces a warning in `TraceCapture`, prefixed `Vela.Testing:`, when an
`Update` Target holds none of the step's filtering attributes or when a registered image alias is missing from the
context. To assert that a step fires, or does not, use [`PluginPipeline`](#simulate-the-pipeline-cascade), which applies
the whole registration.

The builder also exposes: `WithInputParameter`/`WithOutputParameter`, `WithSharedVariable`, `WithTag`,
`WithUserId`/`WithInitiatingUserId`, `WithTargets(...)` and `WithBulkPreImages`/`WithBulkPostImages` (bulk
`CreateMultiple`/`UpdateMultiple`), `WithParentContext`, `WithManagedIdentityService`,
`WithService` (see [Replacing services](#replacing-services)),
`WithEnvironmentId`/`WithTenantId`/`AsApplicationUser`, `TraceCapture` (a `RecordingTracingService`) and
`LogCapture` (a `RecordingLogger` that records `ctx.Log*` entries and `ctx.LogMetric` metrics). `FakePluginExecutionContext` implements the full `IPluginExecutionContext2..7`
chain, so plugins reading newer context members work.

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

`WithService` does not take the platform services the plugin context reads from the platform rather than from the
resolver: `IManagedIdentityService`, `ITracingService`, the telemetry `ILogger`, `IOrganizationService`,
`IOrganizationServiceFactory` and the execution context. It throws an `ArgumentException` naming how to replace that
service instead:

| Service | `PluginContext` | `PluginPipeline` |
| --- | --- | --- |
| `IManagedIdentityService` | `WithManagedIdentityService` | `WithManagedIdentityService` |
| `ITracingService` | `WithTracingService` | recorded in `Trace` |
| `ILogger` (telemetry) | `WithLogger` | recorded in `LogCapture` |
| `IOrganizationService`, `IOrganizationServiceFactory` | `WithOrganizationService`, `WithUserService`, `WithSystemService` | the pipeline's in-memory store |
| `IPluginExecutionContext` (and `2..7`) | the builder's context methods | built by the pipeline for each step |

## Test doubles (no Moq)

| Type | Purpose |
| --- | --- |
| `FakePluginExecutionContext` | mutable execution context (full interface chain) |
| `HookOrganizationService` | delegate hooks `OnCreate`/`OnRetrieve`/... (an unset hook throws `NotSupportedException` naming it) |
| `HookOrganizationServiceFactory` | hands out user/system services |
| `RecordingTracingService` | captures `ctx.Trace(...)` for assertions |
| `InMemoryOrganizationService` | **stateful** CRM (see below) |

Inject any service yourself with `.WithOrganizationService(...)`: Moq, your own fake, etc.

## Stateful in-memory CRM

`InMemoryOrganizationService` stores records so Create/Retrieve/Update/Delete round-trips just work.

```csharp
var crm = new InMemoryOrganizationService()
    .WithAlternateKey("account", "accountnumber")      // duplicate key -> throws
    .WithAutoNumber("task", "ticketnumber", "T-", 3)    // auto-populated on create: T-001, T-002, ...
    .WithCurrentUser(currentUserId);                    // for ConditionOperator.EqualUserId

var id = crm.Create(new Entity("account") { ["name"] = "Zava" });
crm.Update(new Entity("account") { Id = id, ["name"] = "Renamed" });
Entity stored = crm.Retrieve("account", id, new ColumnSet("name"));
```

To run a plugin against a stateful store, pass the service on the builder and assert on the resulting CRM
state:

```csharp
PluginContext.For<Account>()
    .WithTarget(new Account { Id = accountId, Name = "Zava" })
    .WithOrganizationService(crm)
    .Execute(new ProvisionAccountPlugin(string.Empty, string.Empty));

Assert.Single(crm.GetAll("task"));
```

Given an `InMemoryOrganizationService`, `ctx.Service` is the store as its configured user, and `ctx.SystemService`
is the store as SYSTEM: under `WithSecurity(...)` the first is checked against the user's roles and the second is
not, as on a real org.

### Relationships

For 1:N, set the lookup via `Seed`/`Update` (`account.primarycontactid = new EntityReference(...)`). A lookup
is just **data**: the child carries an `EntityReference` to its parent, and that alone drives queries,
`link-entity` joins and filtering. You only need to *declare* the relationship with
`WithParentalRelationship(...)` / `WithReferentialRelationship(...)` (see the
[security model](security-model.md)) when the test exercises a **cascade or inherited access**; a plain lookup
with no declared relationship is correct data, not a broken test.

For N:N, declare the relationship once with `WithManyToMany(...)` and use `Associate`/`Disassociate`: they
create/delete the intersect rows, so `RetrieveMultiple` joins through them like Dataverse:

```csharp
var crm = new InMemoryOrganizationService()
    .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

crm.Associate("account", accountId, new Relationship("account_contacts"),
    new EntityReferenceCollection { new EntityReference("contact", contactId) });

// The account's contacts, joined through the intersect entity:
var q = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
var link = q.AddLink("account_contact", "contactid", "contactid", JoinOperator.Inner);
link.LinkCriteria.AddCondition("accountid", ConditionOperator.Equal, accountId);
crm.RetrieveMultiple(q);
```

`Associate` is idempotent (an existing pair is not duplicated); an undeclared relationship throws. With
early-bound types there is a strongly-typed overload, where the table and column names come from the types and
the property accesses:

```csharp
crm.WithManyToMany<AccountContact, Account, Contact>("account_contacts", ac => ac.AccountId, ac => ac.ContactId);
```

`WithManyToMany(...)` is **optional** when the relationship is carried by registered metadata: declare it on a
`FakeEntityMetadata` via `.ManyToMany(schema, intersect, e1, e1Attr, e2, e2Attr)` and `Associate`/`Disassociate`
derive the intersect mapping from it.

### Bulk & batch

`Execute(...)` handles `Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple` requests,
`CreateMultipleRequest`/`UpdateMultipleRequest`, and `ExecuteMultipleRequest`/`ExecuteTransactionRequest`
(ExecuteMultiple honours `ContinueOnError`/`ReturnResponses`; ExecuteTransaction rolls the whole batch back if
any request throws).

### File / image columns

The block protocol works over an in-memory blob store: `InitializeFileBlocksUpload` -> `UploadBlock` ->
`CommitFileBlocksUpload` (returns a `FileId`; the File column then stores that fileid),
`InitializeFileBlocksDownload` -> `DownloadBlock` (round-trips the content), and `DeleteFile`. These messages
have no strongly-typed request class in the referenced SDK, so call them with a plain
`OrganizationRequest("InitializeFileBlocksUpload") { Parameters = { ... } }`. An Image column that holds bytes
inline is downloaded directly (its bytes are the thumbnail); a plain `byte[]`/fileid attribute already
round-trips through Create/Update/Retrieve.

Dataverse only downloads an Image column that stores full images: otherwise (`account.entityimage`, or a column
created without "Can store full images") `InitializeFileBlocksDownload` fails with `0x80040217`. The harness cannot
tell an Image column from inline bytes on its own, so register the column to get that check:

```csharp
svc.WithEntityMetadata(FakeEntityMetadata.For("account")
    .PrimaryId("accountid")
    .Image("new_img", canStoreFullImage: false)
    .Build());
// svc.DownloadFile(target, "new_img") now throws FaultException<OrganizationServiceFault> (0x80040217).
```

On the platform the setting in force when the image was set decides; the harness reads it at download time.

### Standard system columns

Create/Update auto-populate the standard columns the platform sets for you, so plugins that read or filter on
them behave like they do online:

- **Audit:** `createdon`/`createdby` on Create, `modifiedon`/`modifiedby` on Create and every Update, stamped
  with the calling user (`WithCurrentUser(...)`). When unset, a stable non-empty default user
  (`InMemoryOrganizationService.DefaultCallingUser`) is used instead of the empty GUID.
- **Owner (user/team-owned tables):** `ownerid`, `owninguser` *or* `owningteam`, and `owningbusinessunit`
  (from `WithCurrentBusinessUnit(...)`). A caller-supplied `ownerid` is honoured and the owning columns are
  derived from it. Assign by `Update`-ing `ownerid` to a `team` reference (`owningteam` follows) or via
  `Execute(new AssignRequest { Target = ..., Assignee = new EntityReference("team", id) })`.
- **Organization (org-owned tables):** `organizationid` (from `WithOrganizationId(...)`), no owner columns.
- **State:** `statecode` (0) and `statuscode` (1) default on Create so plugins that read them see an active
  record; a caller-supplied value is honoured.
- **Row version:** `versionnumber` is stamped on Create and bumped on every Update (a monotonic counter).
- **Backdating:** a caller-supplied `overriddencreatedon` sets `createdon` to that value on Create, like a
  data migration online.
- **Timestamps** use `DateTime.UtcNow` by default; pin them with `WithClock(() => fixedInstant)` to assert
  `createdon`/`modifiedon` deterministically. The same clock expires the plug-in cache (`IPluginCacheService`):
  each harness has a cache of its own, so move the clock past a TTL instead of sleeping (see
  [Caching](caching.md#test-a-ttl)).
- **Ids** are random `Guid`s by default; make them reproducible with `WithIdSequence()` (deterministic
  `00000000-0000-0000-0000-000000000001`, `...0002`, ...; assert with `SequentialIds.At(n)`) or
  `WithIdSequence(() => myGuid)` for a custom generator. Virtual tables get none of these system columns.

The **ownership type** is resolved per table: an early-bound row is inferred (a generated class exposes an
`OwnerId` property only for user/team-owned tables); late-bound rows default to user-owned. Declare it
explicitly to override inference or to model the special table types:

```csharp
var crm = new InMemoryOrganizationService()
    .WithCurrentUser(userId)
    .WithCurrentBusinessUnit(businessUnitId)
    .WithOrganizationId(organizationId)
    .AsOrganizationOwned("savedquery")  // audit + organizationid, no owner columns
    .AsVirtual("zava_externaldata")     // virtual table: no system columns at all
    .AsVirtual("zava_readonly", readOnly: true)  // read-only external provider: Create/Update/Delete are rejected
    .AsElastic("zava_telemetry");       // elastic is a storage type, not ownership: audit + owner columns
```

### Table metadata

Plugins that read metadata (`RetrieveEntityRequest`, `RetrieveAllEntitiesRequest`, `RetrieveAttributeRequest`)
work once you register the tables you need. Build the metadata with `FakeEntityMetadata` (it handles the SDK's
read-only metadata setters for you):

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

**Or derive it from the early-bound type**, with no hand-written metadata. `FakeEntityMetadata.From<TEntity>()`
reflects over a class generated by `vela models`: each `[AttributeLogicalName]` property becomes an attribute
(its CLR type maps to the `AttributeTypeCode`), generated option-set enums become picklists, the conventional
`{logicalname}id` column is the primary id, a `name` column is the primary name, ownership is inferred from an
`ownerid` column, and max length is recovered from the generated `[StringLength]` annotation.
`WithEntityMetadata<Account>()` is the one-liner shorthand:

```csharp
var crm = new InMemoryOrganizationService().WithEntityMetadata<Account>();   // zero metadata, zero connection
```

It returns the same builder, so chain overrides for the facets the early-bound type can't carry: select an
attribute with `On(...)` then set the facet:

```csharp
.WithEntityMetadata(
    FakeEntityMetadata.From<Account>()
        .On("name").MaxLength(160)
        .On("primarycontactid").Targets("contact")
        .Build());
```

Override-only (the early-bound type doesn't carry them; set with `On(...)` + a facet setter, or `For(...)`):
lookup targets, required level, numeric range, and any non-conventional primary-name column. Skipped entirely
(no faithful single mapping): multi-select choices, file/image columns, and partylists.

### Queries

`RetrieveMultiple` runs `QueryExpression`, `QueryByAttribute` and `FetchExpression` (FetchXML, common subset):

```csharp
var query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
var link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
link.EntityAlias = "acct";
link.Columns = new ColumnSet("name");
query.Criteria.AddCondition("fullname", ConditionOperator.BeginsWith, "J");

Entity contact = crm.RetrieveMultiple(query).Entities.Single();
object name = contact.GetAttributeValue<AliasedValue>("acct.name").Value; // "Zava"
```

Supported: filters (And/Or, operators incl. Between, Begins/Ends/Like, date operators, EqualUserId,
CompareColumns), conditions on linked entities (records-not-in-a-set), link-entities
(Inner/LeftOuter/Exists/In/MatchFirstRowUsingCrossApply, aliased columns, criteria, nested), Any/All filters,
ordering (incl. linked columns), TopCount, paging, Distinct, total record count, column aliases, and
aggregates (count/countcolumn/sum/avg/min/max + group-by, also on linked columns and over joins).

Null and paging follow what Dataverse does: `ne`, `not-in`, `not-like`, `not-begin-with` and
`not-end-with` match a row whose column is null, while the comparisons, `not-between`, `like` (even `'%'`) and
`ne-userid` do not. Distinct compares text case-insensitively. A query with no page size returns a page of at most
5,000 rows with `MoreRecords`, and no page is larger. The engine pages by page number: the paging cookie is not read,
and Dataverse also returns the same page again when the page number is not incremented.

FetchXML is parsed into the equivalent `QueryExpression` and runs through the same engine path, so a query and
its `ToFetchXml()` return the same rows. The parser is strict: anything it does not model throws
`NotSupportedException` naming the element or attribute, never silently ignored. A condition `<value>` is read
as the type of the column it is compared with, in the invariant culture (`2026-10-09` on a date column, `10.50`
on a money column, `{guid}` on a lookup, `1` on a boolean), and a value that is not valid for that type throws
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

Rejected: `datasource` (retained data), `rowaggregate`, `link-type='natural'`, week/fiscal-period/fiscal-year date
grouping, and every other element or attribute.

### Typed query builder

`Query.For<T>()` builds the same `QueryExpression`, fully typed, and runs against the in-memory CRM like any query.
See [Typed queries](queries.md).

## Not supported (by design)

Query hints, NoLock, `FilterHint=union`, faithful paging cookies. `Associate`/`Disassociate` persist intersect
rows once the relationship is declared (`WithManyToMany(...)` or registered metadata), but a 1:N relationship
is modelled by `Update`-ing the lookup column. For anything else, inject your own `IOrganizationService`.

The harness does **not** simulate the server-side **data effect** of non-CRUD "modelled" messages
(`QualifyLead`, `WinOpportunity`, `SendEmail`, `CloseIncident`, and the like): it fires the steps registered on
those messages so you can test your own logic, but the record changes the platform would make are not produced.
Like the platform, it fires the steps of the sdkmessage the request runs as (`Send` for a `SendEmailRequest`), on
the table the message runs on (queueitem for `AddToQueue`, whatever its `Target`), with an empty
`ctx.PrimaryEntityId`, so a step registered on the wrong message or table does not fire in a test either.
The faults the harness raises itself carry the platform `ErrorCode`: not found (`0x80040217`), duplicate key
(`0x80040237`), missing privilege (`0x80040220`, also for a query on a table the caller cannot read, at the root or in
a link-entity), a record out of the caller's reach on `Retrieve` (`0x80048306`), aggregate limit (`0x8004E023`), the transaction faults above and
the Custom API input checks. An `ExecuteMultiple` child fault keeps its inner fault's code; any other exception
surfaces as an `OrganizationServiceFault` with no `ErrorCode`. There is no formula/rollup/calculated-column, business-rule, or
duplicate-detection simulation.

For the full picture across the whole framework (runtime, CLI and this test harness), see
[Limitations](../reference/limitations.md).
