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
    public class AccountPostUpdate : EntityPluginBase<Account>
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
public class AccountStepRouter : EntityPluginBase<Account>
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

**Don't**
- Stop a step chain before `.Handle(...)`: every step must bind a typed handler (a chain that stops earlier is flagged by analyzer VELA002). Prefer `RegisterPluginSteps()` + `RegisterStep()....Handle(...)` over a no-handler `Execute(...)` override.
- Cast context to `LocalPluginContext` in handlers.
- Mix multiple registration styles in the same plugin class.

## Step configuration flow

The fluent builder enforces a specific order:

1. **Message** - `OnCreate()`, `OnUpdate()`, `OnCreateMultiple()`, `OnUpdateMultiple()`, `OnDelete()`,
   `OnRetrieve()`, `OnRetrieveMultiple()`, `OnAssociate()`, `OnDisassociate()`, or
   `OnMessage("custom_message")` for any other SDK message. The deprecated `SetState` /
   `SetStateDynamicEntity` messages have no typed method: update `statecode`/`statuscode` with `OnUpdate()`
   instead, or register through `OnMessage("SetState")` if you must target the legacy message.
2. **Stage** - `PreValidation()`, `PreOperation()`, or `PostOperation()` (typed methods; the stage you pick
   determines which images the rest of the chain will offer)
3. **Mode** - `Sync()` / `Async()` (`Async()` is PostOperation-only)
4. **Filtering** - `FilterOn(a => a.Attr1, a => a.Attr2)` - **required for `Create`, `Update`,
   `CreateMultiple` and `UpdateMultiple`** (at least one attribute; the first is mandatory at compile time);
   not offered on other messages. An unfiltered Create/Update step would fire on every change, so the builder
   makes you state the trigger.
5. **Optional** - `WithDescription()`, `WithImpersonation()`, `WithSupportedDeployment()`, `WithExecutionOrder()` (the rank; defaults to `1`, see [Execution order](#execution-order-rank)), `Disabled()`, `WithUnsecureConfig(...)`, `WithSecureConfig(...)`
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
> editor and at build, a selector that targets a member of the base `Entity` type (such as `a => a.LogicalName` or
> `a => a.Id`) rather than a real column - no reference or configuration needed.

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

### Typed images (compile-safe projections)

`ctx.PreImage` / `ctx.PostImage` return the full early-bound entity, so nothing stops you reading a column the
step never captured, which comes back as a silent `null`. Read the image instead through `ctx.PreImageAs<T>()` /
`ctx.PostImageAs<T>()`, where `T` is a **generated projection** that exposes only the columns declared in
`WithPreImage(...)` / `WithPostImage(...)`: reading an uncaptured column is then a **compile error**, and
completion is filtered to what you captured.

The step declaration keeps its usual shape (declare the images, then bind a named handler with `Handle(...)`);
only the read inside the handler changes. A build-time source generator emits the projection type from the
captured columns and **names it after your handler method**, so it is easy to guess: a step whose handler is
`OnUpdated` on plugin `AccountAudit` gets `AccountAudit_OnUpdated_PreImage` / `AccountAudit_OnUpdated_PostImage`
(with `_{Alias}` for a custom alias; the plugin class prefix keeps the name unique across classes). They live in
the `<YourNamespace>.Generated.Images` namespace, which the generator also exposes with a `global using`, so
completion offers the type as you type `ctx.PostImageAs<` with no `using` to add. (A lambda handler has no name,
so its projection falls back to `{PluginClass}_{Message}{Stage}_{PreImage|PostImage}`.)

```csharp
public sealed class AccountAudit : EntityPluginBase<Account>
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage(a => a.Name, a => a.Revenue)
            .WithPostImage(a => a.Name)
            .Handle(OnUpdated);

    private void OnUpdated(IUpdatePostContext<Account> ctx)
    {
        var pre = ctx.PreImageAs<AccountAudit_OnUpdated_PreImage>();   // {plugin}_{handler}_PreImage
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
  from the message: a string literal is capitalized into the name segment (`OnMessage("update")` on plugin
  `PermissiveAudit` with a lambda handler gives `PermissiveAudit_UpdatePostOp_PostImage`), and a marker type uses
  its type name; a message passed as a variable is not statically known, so the generator skips it and you keep
  the untyped image.
- **Bulk operations:** Dataverse does not allow entity images on `CreateMultiple` / `UpdateMultiple` steps
  (a `*Multiple` step that captures a `WithPreImage`/`WithPostImage` is rejected at `vela plan`/`apply`). Capture
  the image on the single **`OnCreate()` / `OnUpdate()`** step instead: that step also runs for bulk operations,
  firing per record, so each invocation reads its record's projection with the singular `ctx.PreImageAs<T>()` /
  `ctx.PostImageAs<T>()`. (The list accessors `ctx.PreImagesAs<T>()` / `ctx.PostImagesAs<T>()`, aligned with
  `ctx.GetTargets()`, exist for a native `*Multiple` batch context and are exercised via the in-memory test
  harness, where per-record image collections can be supplied.)
- Reading a column the step did not capture is a normal C# "no such member" compile error on the projection
  (the column simply is not on the type). The `global using` needs C# 10; on older `LangVersion` it is skipped
  and you add `using <YourNamespace>.Generated.Images;` yourself (completion still offers it).
- The projections are compiler-generated (they never appear as files in your source tree; `F12` opens them) by a
  source generator bundled in `Vela.Plugins` - see [Typed image projection generator](../reference/analyzers.md#typed-image-projection-generator)
  for how it names types and behaves when the early-bound model changes. A capture with no explicit columns
  (`WithPreImage()`) keeps the full entity and generates nothing. If the column list is not a literal set of
  selectors, the generator skips it and you keep the untyped image.

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
you call rather than register a step on, so it has no curated `On...()` method.

The modelled messages with a typed context are:

- **CRUD & bulk:** `Create`, `Update`, `Delete`, `CreateMultiple`, `UpdateMultiple`, `Retrieve`, `RetrieveMultiple`.
- **Relationships & sharing:** `Associate`, `Disassociate`, `Merge`, `GrantAccess`, `ModifyAccess`, `RevokeAccess`,
  `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`.
- **Sales lifecycle:** `QualifyLead`, `WinOpportunity`, `LoseOpportunity`, `WinQuote`, `ReviseQuote`,
  `FulfillSalesOrder`, `ConvertQuoteToSalesOrder`, `ConvertSalesOrderToInvoice`, `GenerateQuoteFromOpportunity`,
  `GenerateSalesOrderFromOpportunity`, `GenerateInvoiceFromOpportunity`, `GetQuoteProductsFromOpportunity`,
  `GetSalesOrderProductsFromOpportunity`, `GetInvoiceProductsFromOpportunity`.
- **Queues:** `AddToQueue`, `RemoveFromQueue`, `PickFromQueue`, `RouteTo`, `ReleaseToQueue`.
- **Email:** `SendEmail`, `SendEmailFromTemplate`, `DeliverIncomingEmail`, `DeliverPromoteEmail`.
- **Team / marketing-list membership:** `AddMembersTeam`, `RemoveMembersTeam`, `AddListMembersList`,
  `AddMemberList`, `RemoveMemberList`.
- **Case / contract:** `CloseIncident`, `CancelContract`, `CloneContract`.

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

The scaffold generates an `AccountMessageReference.cs` cheat-sheet with one dormant (`Disabled()`) handler per
row, so you can copy the exact shape for the message you need. The deprecated `SetState` / `SetStateDynamicEntity`
and `Assign` messages are intentionally not modelled: update `statecode`/`statuscode` or `ownerid` with
`OnUpdate()` instead.

**Enabled / disabled** - steps are registered enabled by default. Mark a step `Disabled()` to register it dormant
(kill-switch or phased rollout); `apply` sets the step's state accordingly and `plan` shows the transition. Re-enabling
is just removing the `Disabled()` marker.

**Step configuration** - `WithUnsecureConfig("...")` and `WithSecureConfig("...")` declare the configuration strings passed to the
plugin constructor `(string unsecureConfig, string secureConfig)`. Both are reconciled by `apply` (the secure value is
stored on the linked `sdkmessageprocessingstepsecureconfig` row). Unsecure config appears verbatim in the `vela plan`
JSON (it is non-sensitive by design); the **secure** value is **redacted to a hash** in plan/inspect output (and any
`-o` plan file), so a changed secret is still detected without the value ever leaving Dataverse.

`MainOperation` is reserved for Custom API main handlers; it is not one of the typed stage methods, so a
regular plugin step cannot register there.

**Image validity is a compile error, not a runtime surprise.** The stage method you pick returns a builder
that exposes **only** the images that (message, stage) actually supports, so a wrong image does not compile -
in the IDE, without the CLI. For example `OnDelete().PostOperation()....WithPostImage()` and
`OnUpdate().PreValidation()....WithPreImage()` and `OnCreate().PostOperation()....WithPreImage()` all fail to build.
The rules the chain encodes:

1. `PreValidation` - no images (nothing captured yet).
2. `PreOperation` - only `PreImage`, only for `Update` and `Delete`.
3. `PostOperation` - `PreImage` for `Update`/`Delete`, `PostImage` for `Create`/`Update`.
4. `OnMessage("...")` - a non-modelled message is **permissive**: both images are offered and validity is left
   to Dataverse at deploy time (Vela does not know an arbitrary message's image rules). A modelled message
   reached through `OnMessage(...)` is still validated by the runtime backstop in `Build()`.
5. `CreateMultiple` / `UpdateMultiple` - not deployable with images. Dataverse accepts neither `Target` nor
   `Targets` as an image property on the bulk messages, so a `*Multiple` step that carries a `PreImage`/`PostImage`
   is rejected by `plan`/`apply` at scan time (a clear error, before anything is written). Register the image on
   the single `Create`/`Update` step instead: it also runs for bulk operations, where per-record images are read
   with `ctx.GetPreImage(index, alias)` / `ctx.GetPostImage(index, alias)`. The in-memory test harness does allow
   bulk-step images, so you can still unit-test per-record image handling against `ctx.BulkPreEntityImagesCollection`.

## Context helpers

- `ctx.GetTarget<T>()` - Get the target entity
- `ctx.GetTargets()` / `ctx.TryGetTargets(...)` - Get bulk targets for `CreateMultiple` / `UpdateMultiple`
- `ctx.GetPreImage<T>()`, `ctx.GetPostImage<T>()` - Get images as early-bound
- `ctx.GetPreImage(index, alias)`, `ctx.GetPostImage(index, alias)` - Get per-record images in bulk operations
- `ctx.GetPreImageMerged()`, `ctx.GetPostImageMerged()` - Merge target with image
- `ctx.GetPreImageMerged(index)`, `ctx.GetPostImageMerged(index)` - Merge per-record target with per-record image in bulk operations
- `target.Contains(x => x.Name)` - Typed attribute-presence check (no magic strings). `Contains(x => x.A, x => x.B)` requires **all**; `ContainsAny(x => x.A, x => x.B)` needs **at least one** - useful to test which columns were in the `Target`
- `ctx.HasChanged<Account>(x => x.Name)` / `ctx.HasAnyChanged<Account>(x => x.A, x => x.B)` - True when the attribute is in the `Target` **and** differs from the registered pre-image (requires an entity `Target` + a pre-image; throws otherwise) - the "only act when X actually changed" guard
- `entity.GetFormattedValue(x => x.StatusCode)` - The formatted (display) value for a choice/lookup/money/date, or `null`
- `ctx.Tag` / `ctx.GetSharedVariable("key")` / `ctx.TryGetSharedVariable<T>("key", out var v)` - Read shared variables: the client-supplied `tag`, a value set by an earlier plugin in the same operation, or one inherited from the parent context of a cascaded operation
- `ctx.IsApplicationUser`, `ctx.EnvironmentId`, `ctx.TenantId`, `ctx.InitiatingUserAgent`, `ctx.IsPortalsClientCall`, `ctx.PortalsContactId` - Extended execution context helpers with graceful fallback
- `service.UploadFile(target, "vela_doc", "name.ext", bytes)` / `service.DownloadFile(target, "vela_doc")` / `service.DeleteFile(...)` - One-call File/Image column helpers (`using Vela.Files;`) that drive the block upload/download protocol for you
- `ctx.GetEnvironmentVariable("adc_ApiBaseUrl")` / `ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries")` - Read a Dataverse environment variable (see [Environment variables](#environment-variables) below)
- `ctx.AcquireManagedIdentityToken(scopes)` - Acquire token via `IManagedIdentityService` when available
- `ctx.LogInformation(...)` / `ctx.LogWarning(...)` / `ctx.LogError(ex, ...)` / `ctx.LogMetric(...)` - Leveled, structured telemetry to the trace log and Application Insights (see [Telemetry & Application Insights](telemetry.md))
- `ctx.TraceMethod("details")` - Trace caller method name with correlation/stage/depth
- `ctx.TraceExecutionSnapshot("phase")` - Trace a structured context snapshot, including full Target/Image/Input/Output attribute values with PascalCase sections

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

Managed identity: acquire a token for an external API without a stored secret with
`ctx.AcquireManagedIdentityToken(scopes)` - see [Managed identity](managed-identity.md).

Dataverse operation tracing is automatic through wrapped services (`UserService`, `SystemService`, `CreateOrganizationService`): each call logs operation name (`Create`, `Update`, `RetrieveMultiple`, `Execute`, ...), timestamp, duration, and pipeline context.

> **Writing early-bound entities.** `ctx.UserService` accepts early-bound entities (for example
> `new Account { ... }`) directly - the wrapped services downcast them to the base `Entity` before each
> `Create`/`Update`/`Execute`, so you never hit the sandbox `SerializationException` ("The deserializer has no
> knowledge of any type that maps to the name '&lt;namespace&gt;:Account'"). `vela init` also marks the plugin
> project as a proxy-types assembly (`<AssemblyAttribute Include="Microsoft.Xrm.Sdk.Client.ProxyTypesAssemblyAttribute" />`),
> which additionally covers early-bound entities nested in a deep-insert graph. If you write such graphs from an
> older project, add that attribute (or `[assembly: Microsoft.Xrm.Sdk.Client.ProxyTypesAssembly]`) to your plugin project.

When plugin telemetry is available in runtime (`Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`), trace messages are also forwarded to plugin telemetry for Application Insights correlation.

Whether a trace is *persisted* is the Dataverse **Plugin Trace Log** environment setting (`Off` / `Exception` / `All`) - not anything in code. There is no framework-level verbosity switch: server-side plugins run in Microsoft's sandbox where OS environment variables cannot be set, so a runtime verbosity flag would have no effect online. What you control in code is *what extra detail* you trace, at the call site.

**You get two snapshots for free on every execution.** `PluginBase` wraps your handler and emits the
boundaries automatically - a `start` snapshot before your code runs, then `success` when it returns (or
`invalid-plugin-exception` / `business-exception` / `unhandled-exception` if it throws). You write nothing for
those; the phase names are fixed.

To capture **intermediate** state, call `TraceExecutionSnapshot(...)` yourself **inside your handler** with your
own phase label - a checkpoint around a risky step. (The framework already covers entry and exit, so there's no
point re-emitting `start` yourself.) Use the lighter `TraceMethod(...)` when you don't need the full payload:

```csharp
private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
{
    var target = ctx.Request.Target;
    ctx.TraceMethod("computing credit limit");       // lightweight: method/message/stage/depth, no payload
    var limit = ComputeCreditLimit(target);

    ctx.TraceExecutionSnapshot("after-credit-calc"); // full snapshot at this exact point, your own label
    ctx.UserService.Update(target);
}
```

> [!WARNING]
> A snapshot dumps Target/Image/Input/Output attribute values into the Plugin Trace Log. Avoid calling `TraceExecutionSnapshot` from plugins handling sensitive data when the environment's Plugin Trace Log is set to `All`.

`TraceExecutionSnapshot(...)` always emits the **full** structured payload (every Input/Output parameter, the
pre/post images and the Target, with attribute values); there is no compact/verbose mode. A snapshot looks like:

```text
[2026-06-14T09:12:44.1573820Z] ExecutionSnapshot
  Phase: start
  MessageName: Update
  Stage: 20
  Mode: 0
  Depth: 1
  CorrelationId: 8f1c2d6e-4a7b-4f0e-9c11-2b6e1d2a9f33
  PrimaryEntityName: account
  PrimaryEntityId: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
  UserId: 6c2f1a8b-5d33-4e90-8a17-0f4b2c9d7e55
  InitiatingUserId: 6c2f1a8b-5d33-4e90-8a17-0f4b2c9d7e55
  IsApplicationUser: False
  IsPortalsClientCall: False
  PortalsContactId: <none>
  InitiatingUserAgent: <none>
  EnvironmentId: <none>
  TenantId: <none>
  IsBulkOperation: False
  InputParameters:
    Target:
      LogicalName: account
      Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
      Attributes:
        creditlimit: 1500 (Money)
        name: "Zava"
  OutputParameters:
    <none>
  PreEntityImages:
    PreImage:
      LogicalName: account
      Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
      Attributes:
        name: "Old Zava"
  PostEntityImages:
    PostImage:
      LogicalName: account
      Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
      Attributes:
        name: "Zava"
  Target:
    LogicalName: account
    Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
    Attributes:
      creditlimit: 1500 (Money)
      name: "Zava"
```

Example output - wrapped Dataverse operation tracing (automatic on `UserService` / `SystemService`), emitted as paired `start` / `ok` entries:

```text
[2026-06-14T09:12:44.1790021Z] Dataverse.Update start
  scope       : UserService
  message     : Update
  stage/mode  : 20/0
  depth       : 1
  correlation : 8f1c2d6e-4a7b-4f0e-9c11-2b6e1d2a9f33
  details     : entity=account; id=3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
[2026-06-14T09:12:44.1820017Z] Dataverse.Update ok
  scope       : UserService
  durationMs  : 42
  details     : entity=account; id=3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
```

Keep your own handler traces business-focused (decisions and key values); rely on the framework for the
pipeline, snapshot, and Dataverse-operation tracing.

## See also

- [Typed queries](queries.md) - strongly-typed `QueryExpression` and `ToFetchXml()`.
- [Dependency injection](dependency-injection.md) and [Caching](caching.md).
- [Testing with Vela.Testing](testing.md) - run this plugin with no connection.
