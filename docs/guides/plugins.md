# Write a plugin

Create a class inheriting `EntityPluginBase<TEntity>` and declare steps in `RegisterPluginSteps()` with `RegisterStep()`:

```csharp
using Vela.Context;
using Vela.Constants;
using Vela.Plugin;
using Vela.Steps;
using Zava.Shared.Models.Generated;

namespace Zava.Plugins.Account
{
    public partial class AccountPostUpdate : EntityPluginBase<Account>
    {
        public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)            // Only trigger when Name changes
                .WithDescription("Handles account updates")
                .WithPreImage(a => a.Name)             // Include Name in pre-image
                .WithPostImage(a => a.Name)            // Include Name in post-image
                .Handle(OnAccountUpdated);             // stage-typed handler, bound by method group
        }

        // PostOperation Update step -> IUpdatePostContext<Account>: typed ctx.Request (and ctx.Response,
        // filled by the platform at this stage). ctx.Request.Target is the typed Account - no cast, no type argument.
        private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
        {
            var target = ctx.Request.Target;
            var preImage = ctx.GetPreImage<Account>();
            ctx.Trace($"Name changed from {preImage.Name} to {target.Name}");
        }
    }
}
```

You can register multiple steps in one plugin class and bind each step to a dedicated method:

```csharp
public partial class AccountStepRouter : EntityPluginBase<Account>
{
    public AccountStepRouter(string u, string s) : base(u, s) { }

    protected override void RegisterPluginSteps()
    {
        RegisterStep().OnCreate()
            .PostOperation()
            .Sync()
            .FilterOn(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)  // required on Create/Update
            .WithDescription("Handles account creation after commit.")
            .WithPostImage(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)
            .Handle(OnCreatePost);

        RegisterStep().OnUpdate()
            .PostOperation()
            .Sync()
            .FilterOn(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)  // required on Create/Update
            .WithDescription("Handles customer-facing account changes.")
            .WithPreImage(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)
            .WithPostImage(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)
            .Handle(OnUpdatePost);
    }

    // Each handler takes the stage-typed message context. Both steps are PostOperation, so they get the Post
    // context: ICreatePostContext exposes a typed CreateRequest + CreateResponse, IUpdatePostContext an
    // UpdateRequest + UpdateResponse. A PreValidation/PreOperation step would get I{Message}PreContext instead
    // (ctx.Request only - no ctx.Response, which the platform has not filled yet). It is still an
    // ILocalPluginContext, so images, services and tracing remain available.
    private void OnCreatePost(ICreatePostContext<Account> ctx)
    {
        var created = ctx.Request.Target;                       // typed Account, no cast, no type argument
        var postImage = ctx.PostImage;                          // typed Account?, no type argument (null if unregistered)
        ctx.Trace($"Account created: {postImage?.Name} / {postImage?.EmailAddress1}");
    }

    private void OnUpdatePost(IUpdatePostContext<Account> ctx)
    {
        var preImage = ctx.PreImage;                            // typed Account? for the default alias
        var postImage = ctx.PostImage;                          // for a custom alias, use ctx.GetPreImage<Account>("alias")
        ctx.Trace($"Account updated: {preImage?.Name} -> {postImage?.Name}");
    }
}
```

Each step in a multi-step plugin must have a **distinct trigger** (message + stage + mode + entity) and its own
`Handle(...)` (bound by method group, not a string). Two steps with the same trigger can't be told apart at
runtime, so `Build()` rejects them - put the shared logic in one step (combine the filtering attributes) or use a
different stage/mode. Because the handler is bound as a method group, the message and the handler's context type
are checked at compile time: binding an `OnUpdate()` step to a `void Handle(ICreatePostContext<Account> ctx)` method
does not compile.

## Do / Don't (plugins)

**Do**
- Use `RegisterPluginSteps()` + `RegisterStep().On...()` for single-step and multi-step plugins.
- Use `ctx.GetRequiredService<T>()` for dependency resolution in handlers.
- Read the primary record via `ctx.Request.Target` on the typed message contexts (already early-bound); `GetTarget<T>()` stays for the message-agnostic `OnMessage(...)` handlers where the request does not type the Target.
- Read images with `ctx.GetPreImage<T>()` / `ctx.GetPostImage<T>()` instead of the raw `PreEntityImages` / `PostEntityImages` dictionaries.
- Signal a user-facing failure with `throw new InvalidPluginExecutionException("...")`, or `throw new PluginBusinessException("...")` for a named business error (the Vela base surfaces its message cleanly and records a `business-exception` trace snapshot). Both are clean and neither is flagged by [VELA012](../reference/analyzers.md#vela012) inside a plugin that derives from a Vela base. A `FaultException<OrganizationServiceFault>` from a failed service call is rethrown unchanged, so the caller gets the fault's own error code (a duplicate key stays `0x80040237`). Any other exception type reaches the caller as a generic message with the correlation id (`An unexpected error occurred. Correlation id: ...`). The full exception is traced, so the plug-in trace log keeps it unless the environment's Plugin Trace Log setting is `Off`. That generic message is why VELA012 flags it. Override `FormatUnexpectedErrorMessage(context, exception)` to return a more detailed message.
- To give a business error a code, use `new PluginBusinessException(message, errorCode)`. Dataverse still reports `0x80040265` to the caller; an SDK caller reads your code from the fault's `ErrorDetails["SubErrorCode"]`, and the Web API does not return it.

**Don't**
- Stop a step chain before `.Handle(...)`: every step must bind a typed handler (a chain that stops earlier is flagged by analyzer VELA002). Prefer `RegisterPluginSteps()` + `RegisterStep()....Handle(...)` over a no-handler `Execute(...)` override.
- Cast context to `LocalPluginContext` in handlers.
- Mix multiple registration styles in the same plugin class.
- Catch and **swallow** an exception in a **synchronous** step - that is, silently discard the failure. A sync step runs inside the platform transaction, so a discarded error hides the failure and lets a broken operation commit. Let it propagate, or throw `InvalidPluginExecutionException` / `PluginBusinessException` (both via `throw new ...`) to roll the transaction back (see [VELA012](../reference/analyzers.md#vela012)). Handling an error is fine - a deliberate best-effort block (optional telemetry, a cache warm) may catch locally, and a Custom API may catch to set an error output - as long as you trace it and never leave it silent.

## Step configuration flow

The fluent builder enforces a specific order:

1. **Message** - `OnCreate()`, `OnUpdate()`, `OnCreateMultiple()`, `OnUpdateMultiple()`, `OnDelete()`,
   `OnRetrieve()`, `OnRetrieveMultiple()`, `OnAssociate()`, `OnDisassociate()`, or
   `OnMessage("custom_message")` for any other SDK message. A state change has no typed method: register on
   `OnUpdate().FilterOn(x => x.StateCode)`, or on `OnMessage(PluginMessages.SetStateDynamicEntity)`, which both
   a `SetStateRequest` and an Update of `statecode` fire. A step on `SetState` never fires on the platform
   ([VELA022](../reference/analyzers.md#vela022)).
2. **Stage** - `PreValidation()`, `PreOperation()`, or `PostOperation()` (typed methods; the stage you pick
   determines which images the rest of the chain will offer)
3. **Mode** - `Sync()` / `Async()` (`Async()` is PostOperation-only)
4. **Filtering** - `FilterOn(a => a.Attr1, a => a.Attr2)` - **required for `Create`, `Update`,
   `CreateMultiple` and `UpdateMultiple`** (at least one attribute; a chain without it is a build error,
   [VELA020](../reference/analyzers.md#vela020)); not offered on other messages. The step fires only when the
   request sets one of the attributes: on Update the changed columns, on Create the columns the record is created
   with. An unfiltered Create/Update step would fire on every change, so the builder makes you state the trigger.
5. **Optional** - `WithDescription()`, `WithImpersonation(...)` (see [Impersonation](#impersonation)), `WithSupportedDeployment()`, `WithExecutionOrder()` (the rank; defaults to `1`, see [Execution order](#execution-order-rank)), `Disabled()`, `WithUnsecureConfig(...)`, `WithSecureConfigFrom(...)`
6. **Images** - `WithPreImage(...)` and/or `WithPostImage(...)`
7. **Handler** - `Handle(...)` closes the chain; it must be the last call (a chain that stops earlier is flagged by analyzer VELA002).

The message -> stage -> mode order is enforced by the type-state; steps 4-6 (`FilterOn` and the `With*` /
`Disabled()` modifiers) return the same builder, so they can appear in any order between the mode and the
terminal `Handle(...)`.

Each single-value modifier (`FilterOn` and every `With...` setter) may be set **at most once** per step. A
second call would silently overwrite the first, so it is rejected: analyzer `VELA003` flags it in the editor and
at build, and the builder also throws at registration. (`Disabled()` is exempt: it is an idempotent flag toggle.)

> [!NOTE]
> Column selectors (`FilterOn`, the image selectors in `WithPreImage(...)` / `WithPostImage(...)`, and the typed query/change helpers) accept an
> early-bound column, e.g. `a => a.Name`. A bundled analyzer (`VELA001`, shipped in `Vela.Plugins`) flags, in the
> editor and at build, a selector that targets a member of the base `Entity` type (such as `a => a.LogicalName`, or
> `a => a.Id` on a model whose `Id` override has no `[AttributeLogicalName]`) rather than a real column, and a nested
> path such as `a => a.PrimaryContactId.Id` - no reference or configuration needed. (`vela models` names the `Id`
> override's column, so `a => a.Id` selects the primary key.)

### Execution order (rank)

`WithExecutionOrder(int order)` sets the step's **rank**: the `sdkmessageprocessingstep.rank` that orders the
steps registered on the same message, primary entity, stage and mode. Lower ranks run first, and the rank
defaults to `1` when you don't call it. Give steps distinct ranks to pin their relative order:

```csharp
// Lower rank runs first, so NormalizeName runs before ValidateName.
RegisterStep().OnUpdate().PreOperation().Sync()
    .FilterOn(a => a.Name)
    .WithExecutionOrder(10)
    .Handle(NormalizeName);

RegisterStep().OnUpdate().PreOperation().Sync()
    .FilterOn(a => a.Name)
    .WithExecutionOrder(20)
    .Handle(ValidateName);
```

> [!NOTE]
> Two steps that share a rank on the same message, entity, stage and mode execute in an order Dataverse does not
> define. `vela plan` and `vela apply` warn when a step you are deploying shares its rank with another step
> already registered in the environment (including steps owned by other assemblies), so you can give them
> distinct ranks. Under `--strict` the warning fails the run.

### Impersonation

`WithImpersonation(Guid systemUserId)` registers the step to run in the context of a specific Dataverse
**systemuser** (the `sdkmessageprocessingstep.impersonatinguserid`), so `ctx.Service` (and the injected
`IOrganizationService`) acts as that user rather than the calling user:

```csharp
// Runs this step as the integration service account, whatever user triggered the update.
RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.Name)
    .WithImpersonation(Guid.Parse("11111111-1111-1111-1111-111111111111"))
    .Handle(OnUpdated);
```

Omit `WithImpersonation` (the default) to run as the calling user. `vela plan` / `vela apply` read, diff and
write `impersonatinguserid`, so setting, changing or removing the impersonated user reconciles like any other step
field. Passing `Guid.Empty` is rejected at registration.

### Description

`WithDescription("...")` sets the step's description in Dataverse (what makers see in the plug-in registration
tool and the solution explorer). Without it, `apply` writes the step name. `vela apply` appends a
`[vela:stepkey=...]` line to the description of a keyed step, which keeps the step's identity when an admin renames
it; leave that line in place. The description is part of the plan diff, so a changed `WithDescription` shows as a
step change and `apply` updates it in place. Dataverse stores at most 256 characters including that line, and
`vela plan` rejects a longer description before any write.

### Typed images (compile-safe projections)

`ctx.PreImage` / `ctx.PostImage` return the full early-bound entity, so nothing stops you reading a column the
step never captured, which comes back as a silent `null`. Read the image instead through `ctx.PreImageAs<T>()` /
`ctx.PostImageAs<T>()`, where `T` is a **generated projection** that exposes only the columns declared in
`WithPreImage(...)` / `WithPostImage(...)`: reading an uncaptured column is then a **compile error**, and
completion is filtered to what you captured.

The step declaration keeps its usual shape (declare the images, then bind a named handler with `Handle(...)`);
only the read inside the handler changes. A build-time source generator emits the projection type from the
captured columns as a **nested type inside your plugin's own `partial` class**, and **names it after your handler
method**, so it is short and easy to guess: a step whose handler is `OnUpdated` gets `OnUpdated_PreImage` /
`OnUpdated_PostImage` (with `_{Alias}` for a custom alias). Because it is nested in the plugin class, the handler
reads it **unqualified**, no `using` to add. This is why a plugin that captures a typed image must be declared
`partial`: the analyzer [VELA014](../reference/analyzers.md#vela014) reminds you, with a one-click fix. (A lambda
handler has no name, so its projection falls back to `{Message}{Stage}_{PreImage|PostImage}`.)

```csharp
public sealed partial class AccountAudit(string unsecure, string secure) : EntityPluginBase<Account>(unsecure, secure)   // partial: the projection nests here
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage(a => a.Name, a => a.Revenue)
            .WithPostImage(a => a.Name)
            .Handle(OnUpdated);

    private void OnUpdated(IUpdatePostContext<Account> ctx)
    {
        var pre = ctx.PreImageAs<OnUpdated_PreImage>();   // {handler}_PreImage, nested in the plugin
        decimal? was = pre?.Revenue;   // captured -> available
        // var x = pre?.Telephone1;    // compile error: not captured for this image
    }
}
```

- Additive and opt-in: the existing `ctx.PreImage` / `ctx.PostImage` (full entity) and `ctx.GetPreImage<T>()`
  keep working. It mirrors the early-bound `ctx.GetPreImage<Account>()` accessor, so the read site is familiar;
  pass a custom alias with `ctx.PostImageAs<T>("MyAlias")`.
- Works on the guided path, the typed `On<TRequest>()` path and the permissive `OnMessage("...")` /
  `OnMessage<TMarker>()` path alike (it is a context accessor). On the permissive path the projection is named
  from the message: a string literal is capitalized into the name segment (`OnMessage("update")` with a lambda
  handler gives `UpdatePostOp_PostImage`, nested in the plugin), and a marker type uses its type name; a message
  passed as a variable is not statically known, so the generator skips it and you keep the untyped image.
- **Bulk operations:** a `CreateMultiple` step can capture a post-image and an `UpdateMultiple` step a pre- and
  post-image. Dataverse delivers one image per target, so read them with the list accessors
  `ctx.PreImagesAs<T>()` / `ctx.PostImagesAs<T>()`, aligned with `ctx.GetTargets()`. On a single
  `OnCreate()` / `OnUpdate()` step, use the singular `ctx.PreImageAs<T>()` / `ctx.PostImageAs<T>()`.
- Reading a column the step did not capture is a normal C# "no such member" compile error on the projection
  (the column simply is not on the type). The projection is nested in your plugin's `partial` class, so the handler
  reads it unqualified with no `using` to add; a plugin that captures a typed image must therefore be `partial`
  ([VELA014](../reference/analyzers.md#vela014), with a one-click fix).
- The projections are compiler-generated (they never appear as files in your source tree; `F12` opens them) by a
  source generator bundled in `Vela.Plugins` - see [Typed image projection generator](../reference/analyzers.md#typed-image-projection-generator)
  for how it names types and behaves when the early-bound model changes. A capture with no explicit columns
  (`WithPreImage()`) keeps the full entity and generates nothing. If the column list is not a literal set of
  selectors, the generator skips it and you keep the untyped image.

#### Typed step context (read images as `ctx.PreImage` / `ctx.PostImage`)

Every **named** handler gets a **generated typed step context** named `{Handler}Context` (nested in your plugin
`partial`) - on any modelled message (CRUD, bulk `*Multiple`, and the other messages like `Assign` / `GrantAccess`)
and the typed `On<TRequest>()` path, at any stage. Give the handler that parameter type: it is the uniform
signature for the step whether or not it captures an image. When the step captures one, the context exposes
`ctx.PreImage` / `ctx.PostImage` **already typed** to the captured columns - read them as members instead of
calling `ctx.PreImageAs<T>()`:

```csharp
public sealed partial class AccountAudit(string unsecure, string secure) : EntityPluginBase<Account>(unsecure, secure)
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage(a => a.Revenue)
            .WithPostImage(a => a.Name, a => a.Revenue)
            .Handle(OnUpdated);                 // no explicit type here

    private void OnUpdated(OnUpdatedContext ctx)   // the one short name you write; everything else is generated
    {
        var before = ctx.PreImage;    // typed to { Revenue }
        var after  = ctx.PostImage;   // typed to { Name, Revenue }
    }
}
```

- The generator emits `OnUpdatedContext` (a subclass of the message context, `UpdatePostContext<Account>` here)
  nested in your plugin `partial`, for **every named handler** on a modelled message, at any stage, **with or
  without** a captured image. On a **Create / Update / Delete** step the context adds `PreImage` / `PostImage` typed
  to exactly what the step captured (a bulk or non-CRUD context is a bare subclass - bulk images are per-record).
  An imageless step gets the same `{Handler}Context`. A **lambda** handler has no name, so it keeps the plain
  context and `ctx.PreImageAs<T>()`; the permissive `OnMessage("...")` string path and generated Custom API
  `On{X}` extensions (no modelled context type) keep the plain context too.
- Because the context is a type nested in your plugin, **the plugin class must be `partial`** (and so must any class
  that encloses it) - [VELA014](../reference/analyzers.md#vela014) reports it with a one-click fix, and
  `vela init` / `vela add` scaffold it `partial` already.
- `.Handle(OnUpdated)` stays clean: a method group binds the context through its natural type, so you never write the
  context type in `.Handle`. The **signature is stable**: start imageless with `OnUpdatedContext`, add a
  `WithPreImage(...)` later and `ctx.PreImage` becomes typed on the same context - the handler signature never
  changes; a read you no longer capture simply stops compiling.
- Guard rails: the handler's context must match its step ([VELA015](../reference/analyzers.md#vela015), with a fix
  that corrects the type), and a handler using a typed context must bind exactly one step
  ([VELA016](../reference/analyzers.md#vela016)). To see what was generated, `F12` on `OnUpdatedContext`.
- You are not forced to use it: a method group also binds when you type the parameter as the plain message context
  (`IUpdatePostContext<Account>`), and `ctx.PreImageAs<T>()` keeps working. The one case to avoid is reusing a single
  handler across several steps - the per-handler context cannot serve two steps ([VELA016](../reference/analyzers.md#vela016)).
- Works on the typed `On<TRequest>()` path too: there the generated context derives from the generic message context
  (`MessagePreContext<TRequest, TEntity>` / `MessagePostContext<TRequest, TEntity>`), so a handler for a non-CRUD
  message (`On<AssignRequest>()`, ...) reads `ctx.PreImage` / `ctx.PostImage` the same way. The permissive
  `OnMessage("...")` string path has no request type, so it keeps `ctx.PreImageAs<T>()`.

#### Need the whole entity (for a service)?

The projection exposes only the captured columns, so it does not fit a shared service that takes the full
early-bound entity (`void Normalize(Account account)`). When you genuinely need the whole image, widen it with an
**explicit** cast, or read it directly with `ctx.GetPreImage<Account>()` / `ctx.GetPostImage<Account>()`:

```csharp
private void OnUpdated(OnUpdatedContext ctx)
{
    var pre = ctx.PreImage;              // projection: the captured columns, compile-safe

    _normalizer.Normalize((Account)pre); // widen to the full image for a service that wants the entity
    // same as: _normalizer.Normalize(ctx.GetPreImage<Account>());
}
```

- The cast is **lossless**: the projection retains the source image, so `(Account)pre` carries the `Id`, the
  logical name and every attribute the image held - a `service.Update(...)` on the result works. It is exactly
  `ctx.GetPreImage<Account>()` in cast form (both go through `Entity.ToEntity`), and casting an absent image
  returns `null`; use whichever reads better. Note `ctx.PreImageAs<Account>()` is different: it projects annotated
  attributes only and does not carry the `Id`.
- It is **explicit on purpose**, never implicit. Leaving the captured-columns view means re-entering early-bound
  territory, where reading a column the step did not capture compiles and returns a silent `null` again. Keeping the
  widening visible (`(Account)`) keeps that a deliberate step, so the compile-safety the projection gives you (and
  [VELA017](../reference/analyzers.md#vela017)) is not lost by accident. Prefer reading the projection directly;
  cast only when a service needs the whole entity.

**Typed request and response, split by stage** - a step handler takes the message-typed context, and the type
depends on the stage:

- A **pre-stage** step (`PreValidation()` / `PreOperation()`) hands the handler an `I{Message}PreContext<TEntity>`:
  it exposes the strongly-typed `ctx.Request` (`CreateRequest`, `UpdateRequest`, `MergeRequest`, ...) but **no**
  `ctx.Response` - the platform only fills the response after the main operation, so reading it in a pre-stage
  does not compile.
- A **post-stage** step (`PostOperation()`) hands the handler an `I{Message}PostContext<TEntity>`: `ctx.Request`
  **and** the matching `ctx.Response`.

Both are inferred from the message with no cast; binding an `OnUpdate()` step to the wrong message's context, or
reading `ctx.Response` on a pre-stage handler, does not compile. Read message-specific fields off `ctx.Request`
(for example the merge subordinate from `ctx.Request.SubordinateId`).

### Writing the Target: which stage persists

Setting an attribute on the Target (`ctx.Request.Target["x"] = ...` or `ctx.GetTarget<T>().X = ...`) only reaches
the database when the main operation still lies ahead - so it is a **pre-stage, synchronous** move:

- **PreValidation / PreOperation (Sync)** - the write is part of the row the platform is about to save. This is
  where you set defaults, normalize, or derive a column. Prefer the typed `ctx.Request.Target`.
- **PostOperation (Sync)** - the row is already written. The Target write is **not** saved to the database.
  (It is still visible to a later post-step in the same pipeline, since `InputParameters["Target"]` is shared, but
  do not rely on that to persist anything - use a shared variable to pass data downstream, and
  `ctx.Service.Update(...)` to change the row.)
- **Asynchronous** - the step runs after the operation commits, in its own execution: a Target write has **no
  effect at all** (not persisted, not observed by anyone). [VELA018](../reference/analyzers.md#vela018) flags this.
  Call `ctx.Service.Update(...)` with the change instead.

A matching nuance: a **pre-validation** step has no pre-image (the row may not exist yet), and `HasChanged` needs a
registered pre-image - see [HasChanged](#reacting-only-to-real-changes-haschanged).

Every message reads its primary record off the already-typed `ctx.Request`. For `Create`, `Update` and
`SendEmailFromTemplate`, `ctx.Request` is a Vela-owned typed request (`Vela.Messages.CreateRequest<TEntity>`,
`UpdateRequest<TEntity>`, `SendEmailFromTemplateRequest<TEntity>`) whose `ctx.Request.Target` returns the
early-bound `TEntity` instead of the SDK's base `Entity`. Every other message uses the SDK request:
`ctx.Request.Target` (the `EntityReference` for `Delete`/`Retrieve`/`Associate`/`Disassociate`/`Merge`/
`GrantAccess`/`ModifyAccess`/`RevokeAccess`/`AddToQueue`/`RouteTo`) or `ctx.Request.Targets` (the
`EntityCollection` for `CreateMultiple`/`UpdateMultiple`). To read any target early-bound regardless of message,
the `ctx.GetTarget<T>()` extension stays available.

Vela also owns `UpsertRequest<TEntity>` / `UpsertResponse` for **calling** Upsert from plugin code:
`service.Execute(new UpsertRequest<Account> { Target = account }).ToResponse<UpsertResponse>()` returns
`RecordCreated` (whether the row was inserted or updated) and the affected `Target` reference. Upsert is a message
you call rather than register a step on, so it has no curated `On...()` method. To upsert by a **business key**,
`service.UpsertByKey(account, Account.ByAccountNumberKey("A-001"))` copies the key onto the target and returns
`(Created, Target)` in one call (see [alternate keys](models.md#what-the-generated-table-classes-contain)).

The modelled messages with a typed context are:

- **CRUD & bulk:** `Create`, `Update`, `Delete`, `CreateMultiple`, `UpdateMultiple`, `Retrieve`, `RetrieveMultiple`.
- **Relationships & sharing:** `Associate`, `Disassociate`, `Merge`, `GrantAccess`, `ModifyAccess`, `RevokeAccess`,
  `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`.
- **Sales lifecycle:** `QualifyLead`, `WinOpportunity`, `LoseOpportunity`, `WinQuote`, `ConvertSalesOrderToInvoice`,
  `GenerateQuoteFromOpportunity` (registered on `quote`, the only entity Dataverse filters it on).
- **Queues:** `AddToQueue`, `RemoveFromQueue`, `PickFromQueue`, `RouteTo`, `ReleaseToQueue`.
- **Email:** `SendEmail`, `SendEmailFromTemplate`, `DeliverIncomingEmail`, `DeliverPromoteEmail`.
- **Team / marketing-list membership:** `AddMembersTeam`, `RemoveMembersTeam`, `AddListMembersList`,
  `AddMemberList`, `RemoveMemberList`.
- **Case / contract:** `CloseIncident`, `CancelContract`, `CloneContract`.

**A trigger is named after the SDK request, the step registers on the sdkmessage.** For some business messages the
two differ, and Dataverse only knows the sdkmessage: `OnSendEmail()` registers on `Send`, and the handler sees
`ctx.MessageName == "Send"`. The full list (verified live): `SendEmail` → `Send`, `SendEmailFromTemplate` →
`SendFromTemplate`, `DeliverIncomingEmail` → `DeliverIncoming`, `DeliverPromoteEmail` → `DeliverPromote`,
`WinOpportunity` / `WinQuote` → `Win`, `LoseOpportunity` → `Lose`, `CloseIncident` → `Close`, `CancelContract` →
`Cancel`, `CloneContract` → `Clone`, `AddMembersTeam` → `AddMembers`, `RemoveMembersTeam` → `RemoveMembers`,
`AddListMembersList` → `AddListMembers`, `AddMemberList` → `AddMember`, `RemoveMemberList` → `RemoveMember`.
`OnMessage(...)`, the test harness and `PluginContext.For<T>().OnMessage(...)` apply the same translation, so
`OnMessage(PluginMessages.SendEmail)` also registers on `Send`.

Some request messages accept no custom step at all, so they have no trigger: `ReviseQuote`, `FulfillSalesOrder`,
`ConvertQuoteToSalesOrder`, `GenerateSalesOrderFromOpportunity`, `GenerateInvoiceFromOpportunity` and the three
`Get*ProductsFromOpportunity`. Dataverse rejects a step on them with 0x80044187 ("Custom SdkMessageProcessingStep is
not allowed on the specified message and entity").

Global, entity-less messages (for example `WhoAmI`) are not in this list: they cannot be registered as an
entity-bound plugin step, so there is no `On...()` for them. You can still call them from a handler
(`service.Execute(new WhoAmIRequest())`) and generate a typed request with `vela models --messages`.

A step registered through `OnMessage("...")` (any other message) can bind its handler three ways:

- `.Handle(OnX)` where `void OnX(ILocalPluginContext ctx)` - the plain, untyped context.
- `.Handle<TRequest>(OnX)` where `void OnX(IMessagePreContext<TRequest> ctx)` (pre-stage) or
  `IMessagePostContext<TRequest> ctx` (post-stage) - a **typed `ctx.Request`** over the live `InputParameters`.
  `TRequest` is any `OrganizationRequest` with a parameterless constructor: an SDK request class, or a request
  generated by [`vela models --messages` / `--custom-apis`](models.md). At post-stage `ctx.Response` is a base
  `OrganizationResponse` (read results via `ctx.Response.Results[...]`).
- To also get early-bound images, take the two-argument context `IMessagePreContext<TRequest, TEntity>` /
  `IMessagePostContext<TRequest, TEntity>` (same `.Handle<TRequest>(...)` binding), which adds **`ctx.PreImage`** /
  **`ctx.PostImage`** (the step's own `TEntity`, the plugin's entity - no extra type argument). Read the primary
  record from `ctx.Request.Target`, or early-bound with `ctx.GetTarget<TEntity>()`.

### `On<TRequest>()` - state the request type once

When you have a request type (an SDK request, or one generated by `vela models --messages` / `--custom-apis`),
prefer `On<TRequest>()`. It derives the message name from the request itself (its `RequestName`), so there is no
magic string, and the handler is pre-typed - `ctx.Request` is `IMessagePre/PostContext<TRequest>` **without
restating the type on `Handler`**:

```csharp
protected override void RegisterPluginSteps()
    => RegisterStep().On<CalculateAccountScoreRequest>()    // message = CalculateAccountScoreRequest.RequestName
        .PostOperation().Sync()
        .Handle(OnScore);                                   // no type argument here

private void OnScore(IMessagePostContext<CalculateAccountScoreRequest> ctx)
{
    int weight = ctx.Request.Weight;   // typed, no cast
}
```

`On<TRequest>()` is permissive like `OnMessage("...")` (images validated at runtime, no filtering). Use the
untyped `OnMessage("...")` / marker `OnMessage<TMarker>()` when you do not have a request type in hand.

This is the same typing the curated `On<Message>()` methods give, without a hand-authored per-message context.
`vela models --messages` extends this to generated messages: it emits an entity-constrained `On<Message>()`
extension per applicable entity (compile-time entity applicability, so `steps.OnQualifyLead()` only compiles on an
`EntityPluginBase<Lead>`), delegating to `On<XxxRequest>()`. See [Generate models](models.md).

An untyped handler bound with `.Handle(OnX)` (taking `ILocalPluginContext`) is not scoped to one message, so it
does not expose `ctx.Request`. Read the record there with `ctx.GetTarget<T>()` (a full entity for
`Create`/`Update`), `ctx.GetTargetRef()` (the `EntityReference` for `Delete`/`Associate`/`Disassociate`), or the
message-specific `ctx.GetRequest<TRequest>()`.

| Message method | Handler context | `ctx.Request` | Primary record |
| --- | --- | --- | --- |
| `OnCreate()` | `ICreatePostContext<T>` | `CreateRequest<T>` | `ctx.Request.Target` (`T`), `ctx.Response.id` |
| `OnUpdate()` | `IUpdatePostContext<T>` | `UpdateRequest<T>` | `ctx.Request.Target` (`T`) |
| `OnDelete()` | `IDeletePostContext<T>` | `DeleteRequest` | `ctx.Request.Target` |
| `OnCreateMultiple()` | `ICreateMultiplePostContext<T>` | `CreateMultipleRequest` | `ctx.Request.Targets`, `ctx.Response.Ids` |
| `OnUpdateMultiple()` | `IUpdateMultiplePostContext<T>` | `UpdateMultipleRequest` | `ctx.Request.Targets` |
| `OnRetrieve()` | `IRetrievePostContext<T>` | `RetrieveRequest` | `ctx.Request.Target`, `ctx.Response.Entity` |
| `OnRetrieveMultiple()` | `IRetrieveMultiplePostContext<T>` | `RetrieveMultipleRequest` | `ctx.Request.Query` |
| `OnAssociate()` | `IAssociatePostContext<T>` | `AssociateRequest` | `ctx.Request.Target`, `ctx.Request.Relationship` |
| `OnDisassociate()` | `IDisassociatePostContext<T>` | `DisassociateRequest` | `ctx.Request.Target` |
| `OnMerge()` | `IMergePostContext<T>` | `MergeRequest` | `ctx.Request.Target`, `ctx.Request.SubordinateId` |
| `OnGrantAccess()` | `IGrantAccessPostContext<T>` | `GrantAccessRequest` | `ctx.Request.Target`, `ctx.Request.PrincipalAccess` |
| `OnModifyAccess()` | `IModifyAccessPostContext<T>` | `ModifyAccessRequest` | `ctx.Request.Target` |
| `OnRevokeAccess()` | `IRevokeAccessPostContext<T>` | `RevokeAccessRequest` | `ctx.Request.Target`, `ctx.Request.Revokee` |
| `OnAssign()` | `IAssignPostContext<T>` | `AssignRequest` | `ctx.Request.Target`, `ctx.Request.Assignee` |

**Associate / Disassociate** register with **no primary entity**, whatever `TEntity` the plugin uses. Dataverse has
no per-entity message filter for these (the `Target` can be either end of the relationship), so the step is global
and you discriminate in the handler by the relationship name:

```csharp
RegisterStep().OnAssociate().PostOperation().Sync().Handle(ctx =>
{
    if (ctx.Request.Relationship.SchemaName != "sample_account_leads") return;   // the only filter available
    // ctx.Request.Target + ctx.Request.RelatedEntities are the two ends of the link
});
```

The `TEntity` on `EntityPluginBase<TEntity>` is irrelevant for these two messages (it does not scope them); pick any
and filter on `ctx.Request.Relationship`. Do not branch on `ctx.PrimaryEntityName` or `ctx.PrimaryEntityId`: on these
steps they are always `"none"` and `Guid.Empty`, at every stage and mode. Use `ctx.Request.Target` to tell which end
of the link the call came from.

The scaffold generates an `AccountMessageReference.cs` cheat-sheet with one dormant (`Disabled()`) handler per
row, so you can copy the exact shape for the message you need. `OnAssign()` is entity-bound (unlike
Associate / Disassociate): it keeps its primary entity and fires on reassignment, with `ctx.Request.Assignee`
holding the new owner. The deprecated `SetState` / `SetStateDynamicEntity` messages are intentionally not
modelled: update `statecode` / `statuscode` with `OnUpdate()` instead.

**Enabled / disabled** - steps are registered enabled by default. Mark a step `Disabled()` to register it dormant
(kill-switch or phased rollout); `apply` sets the step's state accordingly and `plan` shows the transition. Re-enabling
is just removing the `Disabled()` marker.

**Step configuration** - both strings reach the plugin constructor `(string unsecureConfig, string secureConfig)`,
and the base class exposes them as the protected `UnsecureConfig` / `SecureConfig` properties (never null). Parse
them yourself (for example with Newtonsoft.Json) when the step carries structured settings.

- `WithUnsecureConfig("...")` holds non-sensitive settings. It is compiled into the assembly, stored unencrypted
  by Dataverse and printed verbatim in `plan`/`inspect` output.
- `WithSecureConfigFrom("PaymentApiKey")` holds a **secret** (API key, connection string, token). The code only
  **names** it; the value is supplied at deployment, so it never lands in git or in the uploaded assembly:

```powershell
vela apply --packages-dir artifacts --solution ZavaSales --secure-config PaymentApiKey=@payment-key.txt
# or: $env:VELA_SECURE_CONFIG_PaymentApiKey = "..."   (handy in CI; --secure-config wins when both are set)
```

`NAME=VALUE` passes the value inline and `NAME=@path` reads it from a file. `apply` writes it to the step's linked
`sdkmessageprocessingstepsecureconfig` row; plan/inspect output only ever shows a digest, so a changed secret is
detected without printing it. **When no value is given, the environment owns the secure configuration**: `apply`
never writes, clears or deletes it, so a value an admin set in the environment survives every deployment. `plan`
reports a step whose declared secure configuration has no value anywhere (fatal under `--strict`). Pass an empty
value (`--secure-config PaymentApiKey=`) to clear it.

`MainOperation` is reserved for Custom API main handlers; it is not one of the typed stage methods, so a
regular plugin step cannot register there.

**Image validity is a compile error, not a runtime surprise.** The stage method you pick returns a builder
that exposes **only** the images that (message, stage) actually supports, so a wrong image does not compile -
in the IDE, without the CLI. For example `OnDelete().PostOperation()....WithPostImage()`,
`OnUpdate().PreValidation()....WithPostImage()` and `OnCreate().PreValidation()....WithPreImage()` all fail to build.
The rules the chain encodes:

1. `PreValidation` - only `PreImage`, only for `Update` and `Delete` (and `UpdateMultiple`). See the note below.
2. `PreOperation` - only `PreImage`, only for `Update` and `Delete`.
3. `PostOperation` - `PreImage` for `Update`/`Delete`, `PostImage` for `Create`/`Update`.
4. `OnMessage("...")` - a non-modelled message is **permissive**: both images are offered and validity is left
   to Dataverse at deploy time (Vela does not know an arbitrary message's image rules). A modelled message
   reached through `OnMessage(...)` is still validated by the runtime backstop in `Build()`.
5. `CreateMultiple` / `UpdateMultiple` - the same rules as `Create` / `Update`: a `PostImage` on `CreateMultiple`,
   a `PreImage` (at any stage) and a `PostImage` on `UpdateMultiple`. Dataverse delivers one image per target, only
   in `Pre/PostEntityImagesCollection`; read them with `ctx.GetPreImage(index, alias)` /
   `ctx.GetPostImage(index, alias)`.

`vela plan`/`apply` check the same rules at scan time, before anything is written, so an image Dataverse would
refuse fails with a clear error rather than a platform error code mid-apply. They also cover the messages Vela
does not model: a `PostImage` before `PostOperation` and any image on `RouteTo` are rejected. Each image is
registered against the request property the message expects (`Id` for Create, `Ids` for CreateMultiple, `Targets`
for UpdateMultiple, `EmailId` for Send/DeliverIncoming/DeliverPromote, `EntityMoniker` for SetState, `Target`
otherwise).

**A PreValidation pre-image is fine for validation, not for a strictly consistent read.** A top-level PreValidation
step runs outside the database transaction and its image is read without a lock, so another process can change the
row before the main operation. Validation logic can accept that (it is the pattern Microsoft Learn recommends over
an extra Retrieve); logic that needs the exact state the write applies to belongs at PreOperation. A nested
PreValidation step (depth > 1) runs inside the parent transaction, and its image includes that transaction's
uncommitted writes. On a Delete, PreValidation is the only stage that still sees the record's children: the cascade
deletes them before PreOperation.

## Context helpers

- `ctx.GetTarget<T>()` - Get the target entity
- `ctx.GetTargets()` / `ctx.TryGetTargets(...)` - Get bulk targets for `CreateMultiple` / `UpdateMultiple`
- `ctx.GetPreImage<T>()`, `ctx.GetPostImage<T>()` - Get images as early-bound
- `ctx.GetPreImage(index, alias)`, `ctx.GetPostImage(index, alias)` - Get per-record images in bulk operations
- `ctx.GetPreImageMerged()`, `ctx.GetPostImageMerged()` - Merge the image's captured columns with the Target's (`GetPreImageMerged<Account>()` returns it typed). Only captured/Target columns are present; a column neither captured nor in the Target is **absent** (reads `null`), not the stored value. The result is an independent snapshot - writing to it does not persist (use `ctx.Service.Update`)
- `ctx.GetPreImageMerged(index)`, `ctx.GetPostImageMerged(index)` - Merge per-record target with per-record image in bulk operations
- `target.Contains(x => x.Name)` - Typed attribute-presence check (no magic strings). `Contains(x => x.A, x => x.B)` requires **all**; `ContainsAny(x => x.A, x => x.B)` needs **at least one** - useful to test which columns were in the `Target`
- `ctx.HasChanged<Account>(x => x.Name)` / `ctx.HasAnyChanged<Account>(x => x.A, x => x.B)` - True when the attribute is in the `Target` **and** differs from the registered pre-image (requires an entity `Target` + a pre-image; throws otherwise) - the "only act when X actually changed" guard
- `entity.GetFormattedValue(x => x.StatusCode)` - The formatted (display) value for a choice/lookup/money/date, or `null`
- `entity.Clone()` - A detached, create-ready copy of a record for the "duplicate a record" pattern (see [Duplicating a record](#duplicating-a-record-clone) below)
- `ctx.Tag` / `ctx.GetSharedVariable("key")` / `ctx.TryGetSharedVariable<T>("key", out var v)` - Read shared variables: the client-supplied `tag`, a value set by an earlier plugin in the same operation, or one inherited from the parent context of a cascaded operation
- `ctx.IsApplicationUser`, `ctx.EnvironmentId`, `ctx.TenantId`, `ctx.InitiatingUserAgent`, `ctx.IsPortalsClientCall`, `ctx.PortalsContactId` - Extended execution context helpers with graceful fallback
- `service.UploadFile(target, "vela_doc", "name.ext", bytes)` / `service.DownloadFile(target, "vela_doc")` / `service.DeleteFile(...)` - One-call File/Image column helpers (`using Vela.Files;`) that drive the block upload/download protocol for you (blocks of at most 4 MB, the platform maximum; a larger `blockSize` is capped; a file the platform serves without chunking is downloaded in one block)
- `ctx.GetEnvironmentVariable("adc_ApiBaseUrl")` / `ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries")` - Read a Dataverse environment variable (see [Environment variables](#environment-variables) below)
- `ctx.AcquireManagedIdentityToken(scopes)` - Acquire token via `IManagedIdentityService` when available
- `ctx.LogInformation(...)` / `ctx.LogWarning(...)` / `ctx.LogError(ex, ...)` / `ctx.LogMetric(...)` - Leveled, structured telemetry to the trace log and Application Insights (see [Telemetry & Application Insights](telemetry.md))
- `ctx.TraceMethod("details")` - Trace caller method name with correlation/stage/depth
- `ctx.TraceExecutionSnapshot("phase")` - Trace a structured context snapshot to the trace log, including the Target/Image/Input/Output attribute values (pass `TraceSnapshotDetail.Metadata` for names only)

### Duplicating a record (`Clone`)

`entity.Clone()` (`using Vela.Querying;`) returns a detached copy ready to pass to `Create` as a new row. It
resets the `Id` and drops everything that ties the copy to the source record: the primary-key attribute, the
system-assigned audit columns (`createdon` / `createdby` / `modifiedon` / `modifiedby` / `versionnumber`),
`RowVersion`, `EntityState`, `KeyAttributes`, `FormattedValues` and `RelatedEntities`. Every remaining attribute
value is **deep-copied**, so mutating the clone never touches the original.

```csharp
using Vela.Querying;

var copy = original.Clone();           // Id is empty; attribute values are independent copies
copy.Name = $"Copy of {original.Name}";
ctx.Service.Create(copy);          // a brand-new record
```

Business columns such as `ownerid` and `statecode` are preserved; clear any your table rejects on create before
calling `Create`. Nested records in a collection column (for example an activity partylist) are themselves cloned
with their `Id` reset, so the whole graph is create-ready.

### Environment variables

Read Dataverse environment variables from a handler with `ctx.GetEnvironmentVariable("adc_X")` /
`ctx.GetEnvironmentVariableAs<T>("adc_X")` (plus `fallback` overloads and `TryGet...`). The effective value is the
environment variable value when set, otherwise the definition's default. See
[Environment variables](environment-variables.md) for typing, missing-value handling, and caching.

### Reacting only to real changes: HasChanged

`FilterOn(...)` decides *when* the step fires (the attribute is present in the `Target`), but a caller can send
an attribute already set to its current value. To run logic only when a value **actually differs** from its
committed state, guard with `HasChanged`, which compares the `Target` against a registered **pre-image**:

```csharp
RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.CreditLimit)
    .WithPreImage(a => a.CreditLimit)   // required: HasChanged compares the Target against this
    .Handle(ctx =>
    {
        if (ctx.HasChanged<Account>(a => a.CreditLimit))
        {
            // only runs when CreditLimit really changed, not merely re-sent
        }
    });
```

- It always compares **`Target` vs the pre-image** (the old value). It needs an entity `Target` (Create/Update;
  Delete/Assign/SetState carry an `EntityReference` and throw) **and** a pre-image holding the attribute, or it
  throws. An attribute absent from the `Target` counts as unchanged.
- It works in **PreOperation and PostOperation** alike, as long as a pre-image is registered. Comparing against a
  **post-image** is not a thing: the post-image already holds the new value, so `Target` vs post-image would
  report "no change" for the very attributes that changed.
- `HasAnyChanged(a => a.A, a => a.B)` is the any-of form. You don't need `HasChanged` at all when reacting to the
  `Target` presence is enough, and it is unavailable on Create (there is no prior state to diff against).

### FilterOn on a bulk write batch (`CreateMultiple` / `UpdateMultiple`)

A bulk **write** batch carries many rows, and only some may carry the filtered column in their `Target`. As for a
single message, `FilterOn` tests column **presence** in the `Target` (not whether the value changed - that is
`HasChanged`); where that test is applied depends on the step shape:

- A single **`OnCreate()`** / **`OnUpdate()`** step (which also runs under `CreateMultiple` / `UpdateMultiple`)
  fires **once per row whose `Target` carries a filtered column**; a row without it is skipped, and each invocation
  sees only its own record.
- A bulk **`OnCreateMultiple()`** / **`OnUpdateMultiple()`** step fires **once for the whole batch if at least one
  row carries a filtered column** (and not at all if none do). Its handler then receives the **entire
  `ctx.Request.Targets` collection, including the rows that lack the column** - the filter gates the batch, not each
  row. So re-check per row (`target.Contains(a => a.X)` / `HasChanged`) before acting on each one.

(`RetrieveMultiple` is query-based and carries no `Target` batch, so none of this applies to it. This matches the
in-memory [test harness](testing.md); the platform invokes a bulk write step once per batch and hands over the
submitted `Targets` verbatim.)

Managed identity: acquire a token for an external API without a stored secret with
`ctx.AcquireManagedIdentityToken(scopes)` - see [Managed identity](managed-identity.md).

Dataverse operation tracing is automatic through wrapped services (`Service`, `SystemService`, `AsUser(id)`): each call logs operation name (`Create`, `Update`, `RetrieveMultiple`, `Execute`, ...), timestamp, duration, and pipeline context.

Handler entry is traced automatically too: alongside the plugin-level `Executing ...` line (message, stage, mode, depth), the runtime writes `Entering handler '<Name>' for message '<Message>'.` naming the specific step (or Custom API) handler that fired - the missing dimension when a plugin registers several steps, so you no longer hand-trace the method name to locate a failure. Add `protected override bool AutoTraceHandlerEntry => false;` on the plugin/Custom API to silence it (a lambda handler has only a compiler-generated name and is not traced).

> **Writing early-bound entities.** `ctx.Service` accepts early-bound entities (for example
> `new Account { ... }`) directly - the wrapped services downcast them to the base `Entity` before each
> `Create`/`Update`/`Execute`, so you never hit the sandbox `SerializationException` ("The deserializer has no
> knowledge of any type that maps to the name '&lt;namespace&gt;:Account'"). `vela init` also marks the plugin
> project as a proxy-types assembly (`<AssemblyAttribute Include="Microsoft.Xrm.Sdk.Client.ProxyTypesAssemblyAttribute" />`),
> which additionally covers early-bound entities nested in a deep-insert graph. If you write such graphs from an
> older project, add that attribute (or `[assembly: Microsoft.Xrm.Sdk.Client.ProxyTypesAssembly]`) to your plugin project.

Trace messages also go to the platform's Application Insights logger (`Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`) at Information level, a no-op when the environment exports no telemetry. See [Telemetry](telemetry.md).

Whether a trace is *persisted* is the Dataverse **Plugin Trace Log** environment setting (`Off` / `Exception` / `All`) - not anything in code. There is no framework-level verbosity switch: server-side plugins run in Microsoft's sandbox where OS environment variables cannot be set, so a runtime verbosity flag would have no effect online. What you control in code is *what extra detail* you trace, at the call site.

**You get an automatic snapshot on every execution, without record data.** `PluginBase` wraps your handler and
writes a `start` snapshot before your code runs, then one for the failure if it throws
(`invalid-plugin-exception` / `organization-service-fault` / `business-exception` / `unhandled-exception`). By default these list only
**metadata**: message, stage, ids, and the *names* of the parameters, images and attributes, never their values.
Plugins routinely handle personal data and secrets, and the trace log is persisted when the environment's Plugin
Trace Log is `All`. While debugging, opt into values for one plugin:

```csharp
protected override TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.Full; // adds values, and a success snapshot
```

Snapshots go **only to the plug-in trace log**: they are never forwarded to Application Insights.

To capture **intermediate** state, call `TraceExecutionSnapshot(...)` yourself **inside your handler** with your
own phase label - a checkpoint around a risky step. An explicit call traces values (`TraceSnapshotDetail.Full`)
unless you pass `TraceSnapshotDetail.Metadata`. Use the lighter `TraceMethod(...)` when you don't need a payload:

```csharp
private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
{
    var target = ctx.Request.Target;
    ctx.TraceMethod("computing credit limit");       // lightweight: method/message/stage/depth, no payload
    var limit = ComputeCreditLimit(target);

    ctx.TraceExecutionSnapshot("after-credit-calc"); // full snapshot at this exact point, your own label
}
```

> [!WARNING]
> A full snapshot writes Target/Image/Input/Output attribute values into the Plugin Trace Log. Keep it out of
> plugins that handle sensitive data when the environment's Plugin Trace Log is set to `All`.

A metadata snapshot (the automatic default) looks like:

```text
[2026-06-14T09:12:44.1573820Z] ExecutionSnapshot
  Phase: start
  MessageName: Update
  Stage: 40
  Mode: 0
  Depth: 1
  CorrelationId: 8f1c2d6e-4a7b-4f0e-9c11-2b6e1d2a9f33
  PrimaryEntityName: account
  PrimaryEntityId: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
  ...
  InputParameters: Target=account:3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41 [creditlimit, name]
  OutputParameters: <none>
  PreEntityImages: PreImage=account:3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41 [name]
  PostEntityImages: <none>
  Target: account:3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41 [creditlimit, name]
```

A full snapshot replaces the last five lines with every parameter and attribute value
(`name: "Zava"`, `creditlimit: 1500 (Money)`, ...).

Each Dataverse call made through the wrapped services is traced as one line when it starts (so a call that hangs or
times out is still visible) and one when it ends - metadata only (operation, table, ids, request name):

```text
Dataverse.Update start scope=User depth=1 entity=account; id=3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
Dataverse.Update ok 42 ms
```

A failed call traces `Dataverse.Update fail 42 ms <ExceptionType>`: only the exception type, since a fault message
can echo submitted values (the exception itself still reaches your handler, message included).

Keep your own handler traces business-focused (decisions and key values); rely on the framework for the
pipeline, snapshot, and Dataverse-operation tracing.

## Respect the sandbox

A plugin runs in the Dataverse **sandbox**, which constrains what the code may do:

- **No filesystem access.** `System.IO.File` / `Directory` and `new FileStream` / `FileInfo` / `DirectoryInfo` throw
  at runtime - [VELA019](../reference/analyzers.md#vela019) flags them. Keep data in memory (`MemoryStream`), read
  embedded resources with `Assembly.GetManifestResourceStream(...)`, or move file work out of the sandbox. In-memory
  types and the pure-string `System.IO.Path` helpers are fine.
- **Outbound HTTP is restricted** to an allow-list the administrator configures (and only over TLS). Route external
  calls through `HttpClient`, authenticating with a [managed identity](managed-identity.md) token rather than a
  stored secret:

  ```csharp
  private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
  {
      string token = ctx.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });
      using var http = new HttpClient();
      http.Timeout = TimeSpan.FromSeconds(15);             // fail fast: the default is 100 s
      http.DefaultRequestHeaders.ConnectionClose = true;   // KeepAlive off in the sandbox
      http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
      // ... call the allow-listed endpoint synchronously (.GetAwaiter().GetResult()).
  }
  ```

  Set an explicit, short `Timeout` and turn `KeepAlive` off, as Microsoft recommends for plug-ins
  ([Set Timeout](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins),
  [Set KeepAlive to false](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-keepalive-false-interacting-external-hosts-plugin)):
  the default 100-second timeout leaves almost nothing of the execution budget below, and a stale persistent
  connection makes the call hang until the network resets it.

- **~2-minute execution budget.** The whole pipeline execution (all synchronous steps) must finish inside roughly two
  minutes or the platform aborts it. Keep outbound calls fast and few; offload slow work to an asynchronous step or
  an external process. (This is the plugin runtime budget - distinct from the CLI's `--timeout`, which bounds a
  `vela` command's Dataverse requests.)

`net462` note: `HttpClient` on `net462` binds against the `System.Net.Http` GAC facade - the project `vela init`
scaffolds already references it (`<Reference Include="System.Net.Http" />`). No TLS setup is needed: the sandbox
runs the plugin on .NET Framework 4.8 with `ServicePointManager.SecurityProtocol` at `SystemDefault`, so the OS
negotiates TLS 1.2 or 1.3 even for a `net462` assembly (verified live, 2026-10). Don't set `SecurityProtocol`: it is
process-wide, so it would change every plugin in the sandbox worker.

## See also

- [Typed queries](queries.md) - strongly-typed `QueryExpression` and `ToFetchXml()`.
- [Dependency injection](dependency-injection.md) and [Caching](caching.md).
- [Testing with Vela.Testing](testing.md) - run this plugin with no connection.
