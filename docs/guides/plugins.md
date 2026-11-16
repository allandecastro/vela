# Write a plugin

This page covers the plugin side of Vela: how to declare steps, which images and request types a handler gets, the
context helpers, errors, tracing and the sandbox rules. It assumes a solution created with
[`vela init`](scaffold.md) and [early-bound models](models.md); [`vela add plugin`](add.md) generates the file to
start from. A Custom API uses a different base class: see [Write a Custom API](custom-apis.md).

A plugin is a class deriving from `EntityPluginBase<TEntity>` that declares its steps in `RegisterPluginSteps()`.
Each step is one fluent chain that ends in `Handle(...)`:

```csharp
namespace Zava.Plugins.Account.Plugins
{
    using Vela.Context;
    using Vela.Plugin;
    using Zava.Shared.Models.Generated;

    // partial: the generated step context (OnAccountUpdatedContext) nests in this class (VELA014).
    public sealed partial class AccountPostUpdate(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)                 // required on Create/Update: fire only when Name is sent
                .WithDescription("Traces account renames.")
                .WithPreImage(a => a.Name)             // the previous Name
                .Handle(OnAccountUpdated);             // bound by method group

        private void OnAccountUpdated(OnAccountUpdatedContext ctx)
        {
            Account target = ctx.Request.Target;       // typed Account, no cast
            ctx.Trace("Name changed from {0} to {1}", ctx.PreImage?.Name, target.Name);
        }
    }
}
```

The `using` directives sit inside the namespace so that `Account` resolves to the model and not to the
`Zava.Plugins.Account` namespace (see [Namespaces and usings](scaffold.md#namespaces-and-usings)).

Next steps: test the plugin with [`PluginPipeline`](testing.md#simulate-the-pipeline-cascade), which runs its steps
as Dataverse does (`PluginContext` is for edge cases, see [Which one to use](testing.md#which-one-to-use)), then
deploy it with [`vela plan` and `vela apply`](plan-apply.md).

## Choosing a step

| You want to | Stage and mode | Notes |
| --- | --- | --- |
| Reject a request early, before the transaction | `PreValidation().Sync()` | Throw to cancel. A top-level PreValidation step runs outside the transaction. |
| Set defaults, normalize or derive columns of the row being saved | `PreOperation().Sync()` | Write `ctx.Request.Target`; it is saved with the row. |
| Write related records in the same transaction | `PostOperation().Sync()` | A failure rolls the whole operation back. |
| Do slow or non-critical work after the commit | `PostOperation().Async()` | Runs in its own execution; a Target write has no effect ([VELA018](../reference/analyzers.md#vela018)). |
| Compare with the previous values | add `WithPreImage(...)` | Never on Create. |
| Read the saved values | add `WithPostImage(...)` | PostOperation only, never on Delete. |
| React to a message with no typed trigger (a Custom API, an unmodelled message) | `On<TRequest>()` (or a marker, `OnMessage<TMarker>()`) | See [Other messages](#other-messages). A message with a typed trigger is registered only through it ([VELA023](../reference/analyzers.md#vela023)). |
| Change a record's state | `OnUpdate().FilterOn(x => x.StateCode)` | See [State changes](#state-changes). |

### State changes

`SetStateRequest` is [deprecated](https://learn.microsoft.com/dotnet/api/microsoft.crm.sdk.messages.setstaterequest)
in favour of an Update of `statecode`, so react to a state change with an Update step filtered on `StateCode`. That
step also runs, at PreOperation and PostOperation, for a caller that still sends `SetStateRequest`.
`On<SetStateRequest>()` (registered on `SetStateDynamicEntity`) is only for logic that needs that request's own
inputs (`EntityMoniker`, `State`, `Status`). A step on `SetState` never fires
([VELA022](../reference/analyzers.md#vela022)).

## Writing the Target: which stage persists

Setting a column on the Target (`ctx.Request.Target.X = ...`) reaches the database only while the main operation
is still ahead:

- **PreValidation / PreOperation (Sync):** the value is saved with the row. Set defaults, normalize and derive
  columns here.
- **PostOperation (Sync):** the row is already written, so the change is not saved. Call `ctx.Service.Update(...)`,
  and pass data to later steps with a shared variable.
- **Async:** no effect at all ([VELA018](../reference/analyzers.md#vela018)). Call `ctx.Service.Update(...)`.

## The step chain

The builder enforces this order (the type-state makes any other order a compile error):

1. **Message**: a typed trigger such as `OnCreate()`, `OnUpdate()`, `OnDelete()`, `OnAssign()`, `OnMerge()`
   (see [Messages](#messages)), `On<TRequest>()`, or `OnMessage<TMarker>()`. There is no string form: a message is
   always named by a type.
2. **Stage**: `PreValidation()`, `PreOperation()` or `PostOperation()`. The stage decides which images the rest of
   the chain offers.
3. **Mode**: `Sync()`, or `Async()` on PostOperation only. `Async(deleteJobOnSuccess: true)` deletes the system job
   once it succeeds (Dataverse `asyncautodelete`).
4. **Modifiers**, in any order, each at most once ([VELA003](../reference/analyzers.md#vela003); `Disabled()` is
   exempt):
   - `FilterOn(a => a.Col, ...)`: **required on `Create`, `Update`, `CreateMultiple` and `UpdateMultiple`**
     ([VELA020](../reference/analyzers.md#vela020)), not offered on other messages. The step fires only when the
     request sends one of the columns: the columns sent on Update, the initial columns on Create.
   - `WithPreImage(...)` / `WithPostImage(...)`: see [Images](#images).
   - `WithDescription("...")`, `WithExecutionOrder(n)`, `WithStepKey("...")`, `WithImpersonation(userId)`,
     `WithSupportedDeployment(...)`, `WithUnsecureConfig("...")`, `WithSecureConfigFrom("Name")`, `Disabled()`,
     `SkipWhenDepthAbove(n)`, and `ForRelationship(...)` on `OnAssociate()` / `OnDisassociate()`.
5. **Handler**: `Handle(...)` closes the chain. A chain that stops earlier is flagged by
   [VELA002](../reference/analyzers.md#vela002), and a `static` handler by
   [VELA004](../reference/analyzers.md#vela004). `vela add` names the handler `Handle{Message}` (`HandleUpdate`)
   unless you pass `--handler`.

> [!NOTE]
> Column selectors (`FilterOn`, image selectors, typed query helpers) take an early-bound column such as
> `a => a.Name`. [VELA001](../reference/analyzers.md#vela001) flags a selector on a base `Entity` member
> (`a => a.LogicalName`) or a nested path (`a => a.PrimaryContactId.Id`), and the runtime rejects them too.
> `vela models` names the `Id` override's column, so `a => a.Id` selects the primary key.

### FilterOn on a bulk write

- A single `OnCreate()` / `OnUpdate()` step also runs under `CreateMultiple` / `UpdateMultiple`, once per row whose
  Target carries a filtered column.
- An `OnCreateMultiple()` / `OnUpdateMultiple()` step fires once for the batch if at least one row carries a
  filtered column (not at all if none do), and receives **every** row of `ctx.Request.Targets`, including the rows
  without it: the filter gates the batch, not each row. Re-check each row (`target.Contains(...)`) before acting on
  it.

`FilterOn` fires the step when a column is **sent**, not when its value changes: see
[Reacting only to real changes](#reacting-only-to-real-changes-haschanged).

### Execution order (rank)

`WithExecutionOrder(n)` sets the step's rank (`sdkmessageprocessingstep.rank`, default `1`): lower ranks run first
among the steps on the same message, entity, stage and mode. The rank is not part of a step's identity, so changing
it updates the deployed step in place.

```csharp
// Same trigger and filter: each step names its key, and the rank orders them.
RegisterStep().OnUpdate().PreOperation().Sync()
    .FilterOn(a => a.Name).WithExecutionOrder(10).WithStepKey("normalize-name")
    .Handle(NormalizeName);

RegisterStep().OnUpdate().PreOperation().Sync()
    .FilterOn(a => a.Name).WithExecutionOrder(20).WithStepKey("validate-name")
    .Handle(ValidateName);
```

Steps sharing a rank run in an order Dataverse does not define. `vela plan` and `vela apply` warn when a step you
deploy shares its rank with another step in the environment (fatal under `--strict`).

### Relationship scope

Dataverse fires `OnAssociate()` / `OnDisassociate()` steps for **every** relationship, whatever entity the plugin is
declared on: these messages have no primary entity. `ForRelationship("schema_name", ...)` runs the handler only for
the named relationships; any other one is skipped with a trace line. The scope is enforced at run time and is not
deployed.

```csharp
RegisterStep().OnAssociate().PostOperation().Sync()
    .ForRelationship("account_contacts")
    .Handle(OnContactLinked);
```

On these steps `ctx.PrimaryEntityName` is `"none"` and `ctx.PrimaryEntityId` is `Guid.Empty`, at every stage and
mode. The plugin's table (the `T` of `EntityPluginBase<T>`) does not filter them, and `ctx.Request.Target` is the
record the caller passed, at either end of the link: associating contacts from an account gives `Target` = the
account and `RelatedEntities` = the contacts, so a contact plugin's handler runs with an account `Target`. See
[Associate and disassociate table rows](https://learn.microsoft.com/power-apps/developer/data-platform/org-service/entity-operations-associate-disassociate)
for the requests themselves.

`ctx.Sides()` splits the link by the plugin's table instead of by the caller's direction: `ThisSide` holds the
records of `T`, `OtherSide` the records at the other end, and `Relationship` the relationship. When `Target` is a
`T`, it is this side and every related record is the other side; otherwise the related records that are a `T` are
this side, and `Target` with the remaining ones is the other side. On a self-referential relationship (both ends
are `T`), `Target` is this side. On a relationship that does not involve `T` (a step without `ForRelationship`),
`ThisSide` is empty.

[!code-csharp[](../samples/Vela.DocSamples/RelationshipSidesSample.cs#plugin)]

### Depth limit per step

The plugin-level depth guard (constructor argument `maxDepth`, default `PluginBase.DefaultMaxDepth` = 8) throws when
an execution is deeper (see [Preventing recursion](#preventing-recursion)). `SkipWhenDepthAbove(n)` replaces it for
one step: above depth `n` that step's handler is **skipped** with a trace line instead of failing the operation.
`n` must be at least 1. It is runtime-only: not deployed, not in `vela plan`.

```csharp
RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.Revenue)
    .SkipWhenDepthAbove(2)
    .Handle(OnRevenueChanged);
```

### Impersonation

`WithImpersonation(Guid systemUserId)` sets the step's `impersonatinguserid`: `ctx.Service` (and the injected
`IOrganizationService`) then act as that user, while `ctx.InitiatingUserId` stays the caller. Without it the step
runs as the calling user. `Guid.Empty` is rejected. `vela plan` and `vela apply` diff and write the field like any
other.

### Description, state and configuration

- `WithDescription("...")` is the step description makers see. `vela apply` appends a `[vela:stepkey=...]` line
  that keeps the step's identity if an admin renames the step; leave it in place. Dataverse stores 256 characters
  including that line, and `vela plan` rejects a longer description before any write.
- Steps are registered enabled. `Disabled()` registers a dormant step (kill switch, phased rollout); `vela apply`
  sets the state and `vela plan` shows the transition.
- Both configuration strings reach the constructor `(string unsecureConfig, string secureConfig)` and the protected
  `UnsecureConfig` / `SecureConfig` properties (never null):
  - `WithUnsecureConfig("...")` is compiled into the assembly, stored unencrypted and printed by `vela plan` and
    `vela inspect`. Never put a secret in it.
  - `WithSecureConfigFrom("PaymentApiKey")` only **names** a secret. The value is supplied at deployment
    (`vela apply --secure-config PaymentApiKey=@payment-key.txt`, or the `VELA_SECURE_CONFIG_PaymentApiKey`
    environment variable), so it never lands in git or in the assembly. Plan output shows only a digest. With no
    value given, `vela apply` never writes, clears or deletes the environment's secure configuration; an empty
    value (`PaymentApiKey=`) clears it. See [Plan and apply](plan-apply.md).

## Several steps in one class

A plugin class can declare several steps, each bound to its own handler:

```csharp
protected override void RegisterPluginSteps()
{
    RegisterStep().OnCreate().PostOperation().Sync()
        .FilterOn(a => a.Name, a => a.Telephone1)
        .WithPostImage(a => a.Name, a => a.Telephone1)
        .Handle(OnCreatePost);

    RegisterStep().OnUpdate().PostOperation().Sync()
        .FilterOn(a => a.Name, a => a.Telephone1)
        .WithPreImage(a => a.Name, a => a.Telephone1)
        .Handle(OnUpdatePost);
}

private void OnCreatePost(OnCreatePostContext ctx) => ctx.Trace("Created: {0}", ctx.PostImage?.Name);

private void OnUpdatePost(OnUpdatePostContext ctx) => ctx.Trace("Was: {0}", ctx.PreImage?.Name);
```

Several steps may also share a **trigger** (message, stage, mode and entity) when something tells them apart: their
filtering attributes, their images, their relationship scope (`ForRelationship`) or an explicit
`WithStepKey("...")`. Each is deployed as its own Dataverse step, and at run time the executing step
(`OwningExtension`) selects the handler. Two steps on one trigger that differ in none of these (for example only by
rank) are rejected at registration, and [VELA008](../reference/analyzers.md#vela008) flags them at build: give one a
`WithStepKey(...)`.

The handler is bound as a method group, so its context type is checked at build time: binding an `OnUpdate()`
step to a handler taking `ICreatePostContext<Account>` is a [VELA015](../reference/analyzers.md#vela015) error.

## Images

An image is a snapshot of the record that Dataverse hands the step. The builder offers only the images the message
and stage support, so an invalid image does not compile:

| Message | Pre-image | Post-image | Image taken from (`messagepropertyname`) |
| --- | --- | --- | --- |
| `Create` | no | PostOperation | `Id` |
| `CreateMultiple` | no | PostOperation | `Ids` |
| `Update` | every stage | PostOperation | `Target` |
| `UpdateMultiple` | every stage | PostOperation | `Targets` |
| `Delete` | every stage | no | `Target` |
| `Assign` | every stage | PostOperation | `Target` |
| `Merge` | every stage | PostOperation | `Target` (master), `SubordinateId` (subordinate) |
| other typed triggers | no | no | |

Through `OnMessage<TMarker>()` and `On<TRequest>()` both images are offered, and the same rule table is checked when
the step is built: a post-image before PostOperation, a pre-image on Create or CreateMultiple, a post-image on Delete
and any image on `RouteTo` or `SendFromTemplate` throw. Images on `Send`, `DeliverIncoming` and `DeliverPromote`
register against `EmailId`, on `SetState` and `SetStateDynamicEntity` against `EntityMoniker`, and on any other
message against `Target`. `vela plan` checks the same table before writing anything, and
[VELA007](../reference/analyzers.md#vela007) reports what it can see at build.

- **PreValidation pre-images.** A top-level PreValidation step runs outside the transaction and its image is read
  without a lock, so the row can change before the main operation. Use it for validation; a read that must match
  the write belongs at PreOperation. A nested PreValidation step (depth above 1) runs in the parent transaction,
  and its image includes that transaction's uncommitted writes. On a Delete, PreValidation is the last stage that
  still sees the record's cascaded children.
- **Bulk messages.** Dataverse delivers one image per target, only in `Pre/PostEntityImagesCollection`, next to the
  images of other steps and of the platform: always read by alias. Use `ctx.GetPreImage(index, alias)` /
  `ctx.GetPostImage(index, alias)`, `ctx.PreImagesAs<T>()` / `ctx.PostImagesAs<T>()`, or the generated context's
  `ctx.PreImages` / `ctx.PostImages`, aligned with `ctx.GetTargets()`.
- **Merge.** `WithPreImage` / `WithPostImage` capture the master, and `WithSubordinatePreImage` /
  `WithSubordinatePostImage` the record merged away, read as `ctx.SubordinatePreImage` / `ctx.SubordinatePostImage`.
  The subordinate's post-image is the deactivated record, with `masterid` set. Every alias on a step must be unique:
  Dataverse accepts a duplicate alias across master and subordinate, then delivers only one image.

```csharp
RegisterStep().OnMerge().PreOperation().Sync()
    .WithPreImage(a => a.Name)
    .WithSubordinatePreImage(a => a.Name, a => a.Telephone1)
    .Handle(OnMerging);
```

### Typed step context (read images as `ctx.PreImage` / `ctx.PostImage`)

Every **named** handler on a typed trigger (and on `On<TRequest>()`, `OnAsyncOnly<TRequest>()` and the `On<Name>()`
extensions `vela models` generates) gets a generated context named `{Handler}Context`, nested in the plugin class.
It derives from the message context (`UpdatePostContext<Account>` for a PostOperation Update) and, on Create,
Update, Delete, Assign and Merge, exposes `ctx.PreImage` / `ctx.PostImage` typed to **exactly the captured
columns**. Reading a column the step did not capture is a compile error. On CreateMultiple / UpdateMultiple it
exposes the per-target lists `ctx.PreImages` / `ctx.PostImages`.

```csharp
public sealed partial class AccountAudit(string unsecureConfig, string secureConfig)
    : EntityPluginBase<Account>(unsecureConfig, secureConfig)
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage(a => a.Revenue)
            .WithPostImage(a => a.Name, a => a.Revenue)
            .Handle(OnUpdated);

    private void OnUpdated(OnUpdatedContext ctx)
    {
        var before = ctx.PreImage;   // { Revenue }
        var after = ctx.PostImage;   // { Name, Revenue }
        // before?.Name does not compile: Name is not captured in the pre-image.
    }
}
```

- The plugin class (and any enclosing type) must be `partial`: [VELA014](../reference/analyzers.md#vela014) reports
  it with a one-click fix, and `vela init` / `vela add` scaffold it that way.
- The handler signature is stable: add or remove an image and only the reads change.
- The handler's context must match its step ([VELA015](../reference/analyzers.md#vela015)), and a handler with a
  generated context binds exactly one step ([VELA016](../reference/analyzers.md#vela016)).
- A capture with no column list (`WithPreImage()`) keeps the full entity. A lambda handler, or a column list the
  generator cannot read, keeps the plain context; [VELA021](../reference/analyzers.md#vela021) says why.
- You may type the parameter as the plain message context (`IUpdatePostContext<Account>`) instead: then
  `ctx.PreImage` is the full early-bound entity, and a column it did not capture reads as `null`
  ([VELA017](../reference/analyzers.md#vela017) flags such reads).

### Typed images (compile-safe projections)

Outside a generated context, read a projection with `ctx.PreImageAs<T>()` / `ctx.PostImageAs<T>()` (pass an alias
for a custom one). The generator names the projection after the handler: `OnUpdated_PreImage`,
`OnUpdated_PostImage`, plus `_{Alias}` for a custom alias; a lambda handler gets `{Message}{Stage}_PreImage`. On the
`On<TRequest>()` / `OnMessage<TMarker>()` paths the request or marker type names it, minus a `Request` suffix
(`On<acme_DoThingRequest>()` gives `acme_DoThingPostOp_PostImage`). See
[Typed image projection generator](../reference/analyzers.md#typed-image-projection-generator).

#### Need the whole entity (for a service)?

A projection exposes only the captured columns. To hand the whole image to a service that takes `Account`, cast
it explicitly or read it untyped:

```csharp
private void OnUpdated(OnUpdatedContext ctx)
{
    _normalizer.Normalize((Account)ctx.PreImage);   // same as ctx.GetPreImage<Account>()
}
```

The cast is lossless (id, logical name and every attribute of the image) and returns `null` for an absent image.
It is explicit on purpose: past it, an uncaptured column compiles again and reads as `null`.

## Typed request and response

The handler context depends on the stage:

- PreValidation / PreOperation: `I{Message}PreContext<TEntity>` with a typed `ctx.Request` and no `ctx.Response`
  (the platform has not produced it yet, so reading it does not compile).
- PostOperation: `I{Message}PostContext<TEntity>` with `ctx.Request` and `ctx.Response`.

For `Create`, `Update` and `SendEmailFromTemplate`, `ctx.Request` is a Vela request (`Vela.Messages.CreateRequest<T>`,
`UpdateRequest<T>`, `SendEmailFromTemplateRequest<T>`) whose `Target` is the early-bound `T`. Other messages use the
SDK request: `ctx.Request.Target` is an `EntityReference` (Delete, Retrieve, Associate, Merge, Assign, the access
and queue messages) and `ctx.Request.Targets` the `EntityCollection` of CreateMultiple / UpdateMultiple.
`ctx.GetTarget<T>()` reads any entity target early-bound.

| Trigger | `ctx.Request` | Primary record |
| --- | --- | --- |
| `OnCreate()` | `CreateRequest<T>` | `ctx.Request.Target` (`T`), `ctx.Response.id` |
| `OnUpdate()` | `UpdateRequest<T>` | `ctx.Request.Target` (`T`) |
| `OnDelete()` | `DeleteRequest` | `ctx.Request.Target` |
| `OnCreateMultiple()` | `CreateMultipleRequest` | `ctx.Request.Targets`, `ctx.Response.Ids` |
| `OnUpdateMultiple()` | `UpdateMultipleRequest` | `ctx.Request.Targets` |
| `OnRetrieve()` | `RetrieveRequest` | `ctx.Request.Target`, `ctx.Response.Entity` |
| `OnRetrieveMultiple()` | `RetrieveMultipleRequest` | `ctx.Request.Query` |
| `OnAssociate()` / `OnDisassociate()` | `AssociateRequest` / `DisassociateRequest` | `ctx.Request.Target`, `ctx.Request.Relationship` |
| `OnMerge()` | `MergeRequest` | `ctx.Request.Target`, `ctx.Request.SubordinateId` |
| `OnAssign()` | `AssignRequest` | `ctx.Request.Target`, `ctx.Request.Assignee` |
| `OnGrantAccess()` / `OnModifyAccess()` / `OnRevokeAccess()` | the SDK request | `ctx.Request.Target` |

## Messages

Typed triggers exist for:

- **CRUD and bulk:** `Create`, `Update`, `Delete`, `CreateMultiple`, `UpdateMultiple`, `Retrieve`, `RetrieveMultiple`.
- **Relationships, sharing, ownership:** `Associate`, `Disassociate`, `Merge`, `Assign`, `GrantAccess`,
  `ModifyAccess`, `RevokeAccess`, `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`.
- **Sales:** `QualifyLead` (lead), `WinOpportunity` / `LoseOpportunity` (opportunity), `WinQuote` and
  `GenerateQuoteFromOpportunity` (quote), `ConvertSalesOrderToInvoice` (salesorder).
- **Queues** (queueitem): `AddToQueue`, `RemoveFromQueue`, `PickFromQueue`, `RouteTo`, `ReleaseToQueue`.
- **Email** (email): `SendEmail`, `SendEmailFromTemplate`, `DeliverIncomingEmail`, `DeliverPromoteEmail`.
- **Membership:** `AddMembersTeam`, `RemoveMembersTeam` (team), `AddListMembersList`, `AddMemberList`,
  `RemoveMemberList` (list).
- **Case and contract:** `CloseIncident` (incident), `CancelContract`, `CloneContract` (contract).
- **Marketing:** `AddItemCampaign`, `RemoveItemCampaign` (campaign).
- **Pricing and goals:** `LockSalesOrderPricing` (salesorder), `LockInvoicePricing` (invoice), `Recalculate` (goal),
  and the global `UnlockSalesOrderPricing`, `UnlockInvoicePricing`.
- **Scheduling:** `ImmediateBook` (appointment), `Reschedule` (appointment, serviceappointment,
  recurringappointmentmaster).
- **Fax and templates:** `SendFax` (fax), `SendTemplate` (template).
- **Product kits:** `AddProductToKit` (product), and the global `RemoveProductFromKit`.

A message with a table in parentheses runs on that table only: register its trigger from an `EntityPluginBase<T>` on
that table (the builder rejects any other). That is what tells `OnWinQuote()` from `OnWinOpportunity()`, which share
`Win`. The queue messages run on `queueitem` whatever their `Target`. On all of these `ctx.PrimaryEntityId` is empty
unless the request's `Target` is a record of that table (`Recalculate`): read the record from `ctx.Request`
(`ctx.Request.EmailId`, `ctx.Request.TeamId`, ...).

A **global** message (`Associate`, `Disassociate`, `UnlockSalesOrderPricing`, `UnlockInvoicePricing`,
`RemoveProductFromKit`) has no per-table filter: its step registers on no table, whatever the plugin's `T`, and
`ctx.PrimaryEntityName` is `none`.

`OnAssign()` is entity-bound and fires on reassignment, with the new owner in `ctx.Request.Assignee`. An Update of
`ownerid` fires Update steps only, never Assign.

**One way per message.** A message with a typed trigger is registered through it. `On<TRequest>()` and
`OnAsyncOnly<TRequest>()` are only for messages without one: `On<UpdateRequest>()` throws when the step is
registered, naming the trigger to use, and [VELA023](../reference/analyzers.md#vela023) reports it at build with a
code fix to `OnUpdate()`. `OnMessage<UpdateRequest>()` is rejected the same way.

**A trigger is named after the SDK request; the step registers on the sdkmessage.** Where they differ, Dataverse only
knows the sdkmessage, and the handler sees it in `ctx.MessageName`:

| Trigger | sdkmessage |
| --- | --- |
| `SendEmail`, `SendFax`, `SendTemplate` | `Send` |
| `SendEmailFromTemplate` | `SendFromTemplate` |
| `DeliverIncomingEmail` / `DeliverPromoteEmail` | `DeliverIncoming` / `DeliverPromote` |
| `WinOpportunity`, `WinQuote` / `LoseOpportunity` | `Win` / `Lose` |
| `CloseIncident` / `CancelContract` / `CloneContract` | `Close` / `Cancel` / `Clone` |
| `AddMembersTeam` / `RemoveMembersTeam` | `AddMembers` / `RemoveMembers` |
| `AddListMembersList` / `AddMemberList` / `RemoveMemberList` | `AddListMembers` / `AddMember` / `RemoveMember` |
| `AddItemCampaign` / `RemoveItemCampaign` | `AddItem` / `RemoveItem` |

The test harness (`PluginContext.For<T>().OnMessage("...")`, `WithRequest(...)`) translates the same way. The
translation covers every SDK request whose name is not an sdkmessage, so `On<CloseQuoteRequest>()` registers on
`Close` too, and `On<SetStateRequest>()` on `SetStateDynamicEntity` (see [State changes](#state-changes)). A request
type outside the table states its sdkmessage with `[SdkMessage("...")]` (`Vela.Messages`).

**Messages without a trigger.** `ReviseQuote`, `FulfillSalesOrder`, `ConvertQuoteToSalesOrder`,
`GenerateSalesOrderFromOpportunity`, `GenerateInvoiceFromOpportunity` and the three `Get*ProductsFromOpportunity`
accept no custom step (Dataverse fails with 0x80044187), so they have no trigger. Neither do `RenewContract`,
`RenewEntitlement`, `QualifyMemberList`, `CopyCampaignResponse`, `ExecuteWorkflow`, `SetProcess`, `SendBulkMail`,
`ReassignObjectsOwner`, `InitializeFrom`, `ConvertProductToKit` and `ConvertKitToProduct`, for the same reason.
Global messages such as `WhoAmI` cannot carry an entity-bound step either; call them from a handler
(`service.Execute(new WhoAmIRequest())`).

`vela init --samples` generates an `AccountMessageReference.cs` cheat sheet with one handler per message, to copy
the shape you need. It is an `abstract` class, so it compiles but is never deployed.

### Other messages

For a message without a typed trigger, when you have a request type (an SDK request, or one generated by
[`vela models --messages` / `--custom-apis`](models.md)), use `On<TRequest>()`. The message name comes from the
request's `RequestName`, and the handler gets its generated `{Handler}Context` (a
`MessagePreContext<TRequest, TEntity>` / `MessagePostContext<TRequest, TEntity>`) with a typed `ctx.Request`:

```csharp
protected override void RegisterPluginSteps() =>
    RegisterStep().On<CalculateAccountScoreRequest>()
        .PostOperation().Sync()
        .Handle(OnScore);

private void OnScore(OnScoreContext ctx)
{
    int weight = ctx.Request.Weight;   // typed, no cast
}
```

Without a request type, generate one (`vela models --messages <name>` / `--custom-apis <name>`), or use a marker type,
`OnMessage<TMarker>()` (a class marked `[CustomMessage("...")]`, namespace `Vela.Constants`), and bind either
`.Handle(OnX)` with `void OnX(ILocalPluginContext ctx)`, or `.Handle<TRequest>(OnX)` with
`IMessagePreContext<TRequest>` / `IMessagePostContext<TRequest>`. The two-argument forms
`IMessagePreContext<TRequest, TEntity>` / `IMessagePostContext<TRequest, TEntity>` add `ctx.PreImage` /
`ctx.PostImage`. At PostOperation `ctx.Response` is a base `OrganizationResponse` (read `ctx.Response.Results[...]`).
These paths offer no `FilterOn`.

An untyped `ILocalPluginContext` handler has no `ctx.Request`: read the record with `ctx.GetTarget<T>()`,
`ctx.GetTargetRef()` (an `EntityReference` target) or `ctx.GetRequest<TRequest>()`.

## Read and write records

Read with the typed helpers rather than `new ColumnSet(true)` ([VELA013](../reference/analyzers.md#vela013) flags
all-columns reads in a plugin):

```csharp
using Microsoft.Xrm.Sdk.Query;   // ConditionOperator
using Vela.Context;              // Retrieve<T>, RetrieveByKey, UpsertByKey
using Vela.Querying;             // Query.For<T>()

Account account = ctx.Service.Retrieve<Account>(id, a => a.Name, a => a.CreditLimit);
Account byKey = ctx.Service.RetrieveByKey<Account>(Account.ByAccountNumberKey("A-001"), a => a.Name);
Contact? first = Query.For<Contact>()
    .Where(c => c.ParentCustomerId, ConditionOperator.Equal, id)
    .Select(c => c.FullName)
    .FirstOrDefault(ctx.Service);
```

`Retrieve<T>` sends a real Retrieve (the steps registered on Retrieve run) and needs at least one column. See
[Reading one record by id](queries.md#reading-one-record-by-id) and [Typed queries](queries.md) for the full
query builder.

To write by alternate key, `service.UpsertByKey(account, Account.ByAccountNumberKey("A-001"))` upserts a copy of
`account` keyed by exactly that key (`account` itself is not modified and must not have an id) and returns
`(Created, Target)`. `UpsertRequest<TEntity>` / `UpsertResponse` (`Vela.Messages`) are the request form. See
[alternate keys](models.md#what-the-generated-table-classes-contain).

Two more helpers work on records:

- `entity.Clone()` (`using Vela.Querying;`): a create-ready copy. It resets the id and drops the primary key, the
  audit columns (`createdon`, `createdby`, `modifiedon`, `modifiedby`, `versionnumber`), `RowVersion`,
  `EntityState`, `KeyAttributes`, `FormattedValues` and `RelatedEntities`, and deep-copies everything else (nested
  records of a collection column are cloned with their id reset). Business columns such as `ownerid` are kept.
- `service.UploadFile(...)` / `service.DownloadFile(...)` / `service.DeleteFile(...)` (`using Vela.Files;`): the
  File and Image column block protocol, in blocks of at most 4 MB. A download whose size differs from the announced
  size throws.

## Context helpers

- **Services.** `ctx.Service` (the current user: the impersonated user if the step has one, otherwise the caller),
  `ctx.SystemService` and `ctx.AsUser(userId)`. Every call through them is traced (see [Tracing](#tracing)).
  `ctx.Service` accepts early-bound entities directly. `ctx.GetRequiredService<T>()` resolves a registered service:
  see [Dependency injection](dependency-injection.md).
- **Target.** `ctx.GetTarget<T>()`, `ctx.GetTargetRef()`, `ctx.GetTargets()` / `ctx.TryGetTargets(...)`.
- **Images.** `ctx.GetPreImage<T>()` / `ctx.GetPostImage<T>()` (optional alias), and the bulk
  `ctx.GetPreImage(index, alias)`.
- **Image merged with the Target.** `ctx.GetPreImageMerged()` / `ctx.GetPostImageMerged()` (typed:
  `GetPreImageMerged<Account>()`): the image's captured columns overlaid with the Target's. A column in neither is
  absent, not the stored value. The result is a detached copy: writing to it does not persist. On a bulk step,
  `ctx.GetPreImageMerged(index)` / `ctx.GetPostImageMerged(index)` merge one row's Target with its image.
- **Columns** (`using Vela.Querying;`). `target.Contains(x => x.Name)` (all of),
  `target.ContainsAny(x => x.A, x => x.B)` (any of), `entity.GetFormattedValue(x => x.StatusCode)` (the display
  value, or `null`).
- **Changes.** `ctx.HasChanged<Account>(x => x.Name)` / `ctx.HasAnyChanged<Account>(...)`: see below.
- **Shared variables.** `ctx.Tag`, `ctx.GetSharedVariable("key")`, `ctx.TryGetSharedVariable<T>("key", out var v)`,
  including those of parent contexts. A PreValidation step's shared variables reach the later stages only through
  the parent context, which these helpers walk.
- **Configuration and identity.** `ctx.GetEnvironmentVariable("adc_X")` / `ctx.GetEnvironmentVariableAs<T>(...)`
  (see [Environment variables](environment-variables.md)), `ctx.AcquireManagedIdentityToken(scopes)` (see
  [Managed identity](managed-identity.md)).
- **Caller.** `ctx.IsApplicationUser`, `ctx.EnvironmentId` (a string), `ctx.TenantId`, `ctx.InitiatingUserAgent`,
  `ctx.IsPortalsClientCall`, `ctx.PortalsContactId`.
- **Logging.** `ctx.LogInformation(...)`, `ctx.LogWarning(...)`, `ctx.LogError(ex, ...)`, `ctx.LogMetric(...)`: see
  [Telemetry](telemetry.md).

### Reacting only to real changes: HasChanged

`FilterOn` fires the step when a column is **sent**; a caller can send its current value. `HasChanged` compares the
Target with a registered pre-image:

```csharp
RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.CreditLimit)
    .WithPreImage(a => a.CreditLimit)          // HasChanged compares against this
    .Handle(OnCreditLimit);

private void OnCreditLimit(OnCreditLimitContext ctx)
{
    if (ctx.HasChanged<Account>(a => a.CreditLimit))
    {
        // runs only when the value really changed
    }
}
```

- It needs an entity Target (Create and Update; Delete, Assign and SetState carry an `EntityReference` and throw)
  and a pre-image holding the column, or it throws. A column absent from the Target counts as unchanged.
- It works at any stage that has a registered pre-image (PreValidation, PreOperation and PostOperation). It never
  compares with a post-image, which already holds the new value, and it is unavailable on Create, which has no
  prior state.
- Strings compare exactly (case included) and multi-select choices as sets.

[VELA009](../reference/analyzers.md#vela009) flags a `HasChanged` with no pre-image or on an uncaptured column.

## Errors

- Throw `InvalidPluginExecutionException("...")` or `PluginBusinessException("...")` for a user-facing failure.
  `PluginBusinessException(message, errorCode)` adds a code that an SDK caller reads from the fault's
  `ErrorDetails["SubErrorCode"]` (the top-level code stays `0x80040265`; the Web API drops it). For a message in the
  user's language, see [Localized messages](localization.md).
- A `FaultException<OrganizationServiceFault>` from a service call is rethrown unchanged, so the caller gets the
  fault's own code (a duplicate key stays `0x80040237`).
- Any other exception reaches the caller as `An unexpected error occurred. Correlation id: {id}.` and is traced in
  full. [VELA012](../reference/analyzers.md#vela012) flags such throws. Override
  `FormatUnexpectedErrorMessage(context, exception)` for a more detailed message.
- Don't swallow an exception in a synchronous step: the operation would commit without the work. A swallowed failed
  **write** fails the operation anyway (`0x8009000C`); a swallowed failed read is harmless. Catch only for
  deliberate best-effort work, and trace it. [VELA027](../reference/analyzers.md#vela027) flags an empty `catch`
  block.

## Tracing

- Whether a trace is persisted is the environment's **Plugin Trace Log** setting (`Off` / `Exception` / `All`), not
  code; there is no runtime verbosity switch (the sandbox has no environment variables to set). In code you control
  each plugin class's automatic traces (below) and what you trace yourself. `ctx.Trace(...)` also goes to the
  platform's Application Insights logger at Information level.
- The runtime traces an `Executing ...` line and `Entering handler '<Name>' for message '<Message>'.` for each named
  handler (`protected override bool AutoTraceHandlerEntry => false;` silences the latter).
- Each Dataverse call through the context services is traced as one line when it starts and one when it ends,
  metadata only (`protected override bool AutoTraceServiceCalls => false;` keeps only failed calls, on one line):

  ```text
  Dataverse.Update start scope=User depth=1 entity=account; id=3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
  Dataverse.Update ok 42 ms
  ```

- An execution snapshot is traced at start and on failure, listing message, stage, ids and the **names** of
  parameters, images and columns, never their values. To include values (and a success snapshot) while debugging:
  `protected override TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.Full;`. Snapshots go to the
  trace log only, never to Application Insights.
- `ctx.TraceExecutionSnapshot("phase")` traces a snapshot at a point you choose, with names, ids and counts only,
  like the automatic one; pass `TraceSnapshotDetail.Full` to include the values. `ctx.TraceMethod("details")` traces
  a light checkpoint (method, message, stage, depth).

> [!WARNING]
> A full snapshot writes record values into the Plugin Trace Log. Keep it out of plugins handling sensitive data
> when the Plugin Trace Log is `All`.

**Keep the trace budget for your own lines.** Dataverse persists only about 10 KB of trace text per execution and
drops the rest. A metadata snapshot is about 20 lines and each Dataverse call adds two, so a plugin that makes many
calls can turn the automatic traces down (`TraceSnapshotDetail.None` writes no snapshot, failures included):

```csharp
protected override TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.None;
protected override bool AutoTraceServiceCalls => false;
protected override bool AutoTraceHandlerEntry => false;
```

The `Executing ...` and `Execution completed in N ms.` lines and the exception traces are always written. An override
applies to one plugin class and the classes that derive from it; to quiet every plugin of a project, put the
overrides on your own abstract base classes (for example a `QuietEntityPluginBase<TEntity>` deriving from
`EntityPluginBase<TEntity>`) and derive your plugins from them.

## Preventing recursion

A step that writes to its own table can fire itself again. Stop that in the step's logic:

- **Write in a pre-stage.** A column set on the Target in PreValidation / PreOperation is saved with the row: no
  extra Update to recurse on.
- **Filter on the columns that matter**, and leave them out of the step's own write.
- **Check for a real change** with [`HasChanged`](#reacting-only-to-real-changes-haschanged).
- **Flag your own write** with a shared variable. The cascaded execution sees it through its parent context:

  ```csharp
  private const string Recalculating = "zava_recalculating";

  private void OnUpdated(OnUpdatedContext ctx)
  {
      if (ctx.GetSharedVariable(Recalculating) != null)
      {
          return; // caused by our own write below
      }

      ctx.PluginExecutionContext.SharedVariables[Recalculating] = true;
      ctx.Service.Update(new Account { Id = ctx.PrimaryEntityId, CreditLimit = Recalculate(ctx) });
  }
  ```

Don't build logic on `ctx.Depth`: Microsoft
[advises against it](https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.iexecutioncontext.depth), because the
same operation can arrive through a deeper path. Dataverse aborts a chain that reaches depth 8 within an hour as an
infinite loop. The base classes' `maxDepth` guard (default 8) is a backstop that gives a clearer trace, not
recursion prevention. `PluginPipeline` caps the cascade at the same depth by default (`WithMaxDepth` changes it).

## Respect the sandbox

- **No state on the plugin.** The sandbox caches a plugin instance and calls it concurrently, so a writable field
  or a mutable collection on the class leaks data between executions
  ([VELA011](../reference/analyzers.md#vela011)). Keep per-execution state in locals or services.
- **No parallel work.** Don't start threads or tasks, and don't block on one
  ([VELA024](../reference/analyzers.md#vela024)).
- **No filesystem.** `File`, `Directory`, `FileStream`, `FileInfo` and `DirectoryInfo` throw at run time
  ([VELA019](../reference/analyzers.md#vela019)). Use `MemoryStream` or embedded resources. `System.IO.Path` string
  helpers are fine.
- **Outbound HTTP** is limited to what the administrator allows. Prefer a [managed identity](managed-identity.md)
  token to a stored secret, set a short `Timeout` and turn keep-alive off, as Microsoft recommends
  ([timeout](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins),
  [keep-alive](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-keepalive-false-interacting-external-hosts-plugin)).
  Vela warns on an `HttpClient` whose `Timeout` is never set ([VELA026](../reference/analyzers.md#vela026)) and on a
  call to an IP address instead of a named host ([VELA025](../reference/analyzers.md#vela025); see the
  [web access restrictions](https://learn.microsoft.com/power-apps/developer/data-platform/access-web-services)),
  in the plugin and in the services of its assembly:

  ```csharp
  string token = ctx.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });
  using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
  http.DefaultRequestHeaders.ConnectionClose = true;
  http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
  ```

  No TLS setup is needed: the sandbox runs plugins on .NET Framework 4.8 with `SecurityProtocol` at
  `SystemDefault`. Don't set `ServicePointManager.SecurityProtocol`; it is process-wide. The scaffolded plugin
  project already references `System.Net.Http`.
- **Time limit.** Microsoft states that "a Dataverse message operation must complete within **2 minutes**",
  including all its registered plug-ins, and advises limiting a plug-in to about 2 seconds
  ([Time and resource constraints](https://learn.microsoft.com/power-apps/developer/data-platform/analyze-performance#time-and-resource-constraints)).
  Offload slow work to an async step or outside Dataverse.
- **Retry only what can be retried.** Service-protection limits don't apply to data operations from a plugin, and a
  failed call inside a transaction cannot be replayed (`RetryPolicy` runs the operation once when
  `ctx.IsInTransaction` is true). `RetryPolicy.Execute(ctx, ...)` is for calls that leave the sandbox; each wait is
  capped at 30 s, and `maxElapsed` bounds the total:

  ```csharp
  Account remote = RetryPolicy.Execute(ctx,
      () => otherEnvironment.Retrieve<Account>(id, a => a.Name),
      retryOnTimeout: true,
      maxElapsed: TimeSpan.FromSeconds(20));
  ```

## See also

- [Testing with Vela.Testing](testing.md): run the plugin's steps with `PluginPipeline`.
- [Plan and apply](plan-apply.md): deploy the steps.
- [Typed queries](queries.md), [Dependency injection](dependency-injection.md), [Caching](caching.md),
  [Localized messages](localization.md).
- [Escape hatches](escape-hatches.md): drop to the raw SDK.
- [Analyzers](../reference/analyzers.md): every VELA rule cited on this page.
