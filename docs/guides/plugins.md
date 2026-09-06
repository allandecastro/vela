# Write a plugin

Create a class inheriting `EntityPluginBase<TEntity>` and declare steps in `RegisterPluginSteps()` with `RegisterSteps()`:

```csharp
using Vela.Context;
using Vela.Constants;
using Vela.Plugin;
using Vela.StepBuilder;
using Zava.Shared.Models;

namespace Zava.Plugins.Account
{
    public class AccountPostUpdate : EntityPluginBase<Account>
    {
        public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)      // Only trigger when Name changes
                .Description("Handles account updates")
                .PreImage()
                    .Attributes(a => a.Name)           // Include Name in pre-image
                .PostImage()
                    .Attributes(a => a.Name);          // Include Name in post-image
        }

        protected override void Execute(ILocalPluginContext ctx)
        {
            var target = ctx.GetTarget<Account>();
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
        RegisterSteps().OnCreate()
            .PostOperation()
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)  // required on Create/Update
            .Handler(OnCreatePost)
            .Description("Handles account creation after commit.")
            .PostImage()
                .Attributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1);

        RegisterSteps().OnUpdate()
            .PostOperation()
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)  // required on Create/Update
            .Handler(OnUpdatePost)
            .Description("Handles customer-facing account changes.")
            .PreImage()
                .Attributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)
            .PostImage()
                .Attributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1);
    }

    // Each handler takes the message-typed context: ICreateContext exposes a typed CreateRequest, IUpdateContext
    // a typed UpdateRequest (plus, in a Post step, the matching Response). It is still an ILocalPluginContext, so
    // images, services and tracing remain available.
    private void OnCreatePost(ICreateContext<Account> ctx)
    {
        var created = ctx.Target;                               // typed Account, no cast, no type argument
        var postImage = ctx.GetPostImage<Account>();
        ctx.Trace($"Account created: {postImage.Name} / {postImage.EmailAddress1}");
    }

    private void OnUpdatePost(IUpdateContext<Account> ctx)
    {
        var preImage = ctx.GetPreImage<Account>();
        var postImage = ctx.GetPostImage<Account>();
        ctx.Trace($"Account updated: {preImage.Name} -> {postImage.Name}");
    }
}
```

Each step in a multi-step plugin must have a **distinct trigger** (message + stage + mode + entity) and its own
`Handler(...)` (bound by method group, not a string). Two steps with the same trigger can't be told apart at
runtime, so `Build()` rejects them - put the shared logic in one step (combine the filtering attributes) or use a
different stage/mode. Because the handler is bound as a method group, the message and the handler's context type
are checked at compile time: binding an `OnUpdate()` step to a `void Handle(ICreateContext<Account> ctx)` method
does not compile.

## Do / Don't (plugins)

**Do**
- Use `RegisterPluginSteps()` + `RegisterSteps().On...()` for single-step and multi-step plugins.
- Use `ctx.GetRequiredService<T>()` for dependency resolution in handlers.
- Use typed helpers (`GetTarget<T>`, `GetPreImage<T>`, `GetPostImage<T>`) instead of raw dictionaries.

**Don't**
- Start new plugin code on `RegisterPluginStep(IStepBuilder<TEntity>)` legacy shape.
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
3. **Mode** - `Mode(ExecutionMode.Synchronous)` or `Mode(ExecutionMode.Asynchronous)`
4. **Filtering** - `FilteringAttributes(a => a.Attr1, a => a.Attr2)` - **required for `Create`, `Update`,
   `CreateMultiple` and `UpdateMultiple`** (at least one attribute) and must come immediately after `Mode(...)`;
   not offered on other messages. An unfiltered Create/Update step would fire on every change, so the builder
   makes you state the trigger.
5. **Optional** - `Description()`, `ImpersonateUsing()`, `SupportedDeployment()`, `ExecutionOrder()`, `Disabled()`, `UnsecureConfig(...)`, `SecureConfig(...)`
6. **Images** - `PreImage().Attributes(...)` and/or `PostImage().Attributes(...)`

Short aliases are available when you prefer action-style naming: `AddPreImage(...)`, `AddPostImage(...)`.

**Typed request and response** - a step handler takes the message-typed context, so `ctx.Request` is the
strongly-typed SDK request for that message (`CreateRequest`, `UpdateRequest`, `MergeRequest`, `GrantAccessRequest`,
...) and, in a `PostOperation` step, `ctx.Response` is the matching response - each inferred from the message, with no
cast. Read message-specific fields off `ctx.Request` (for example the merge subordinate from
`ctx.Request.SubordinateId`).

For the primary record the context also carries it **already typed**, so a handler never re-states the entity type
or casts: `ctx.Target` (the early-bound `TEntity` for `Create`/`Update`), `ctx.TargetReference` (the
`EntityReference` for `Delete`/`Retrieve`/`Associate`/`Disassociate`/`Merge`/`GrantAccess`/`ModifyAccess`/
`RevokeAccess`, and also `SendEmailFromTemplate` via `ctx.Target`, `AddToQueue`/`RouteTo` via `ctx.TargetReference`)
and `ctx.TargetCollection` (the `EntityCollection` for `CreateMultiple`/`UpdateMultiple`). Messages whose request has
no primary record read their message-specific fields off `ctx.Request` instead.

The modelled messages with a typed context are:

- **CRUD & bulk:** `Create`, `Update`, `Delete`, `CreateMultiple`, `UpdateMultiple`, `Retrieve`, `RetrieveMultiple`.
- **Relationships & sharing:** `Associate`, `Disassociate`, `Merge`, `GrantAccess`, `ModifyAccess`, `RevokeAccess`,
  `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`.
- **Sales lifecycle:** `QualifyLead`, `WinOpportunity`, `LoseOpportunity`, `ConvertQuoteToSalesOrder`,
  `ConvertSalesOrderToInvoice`, `GenerateQuoteFromOpportunity`, `GenerateSalesOrderFromOpportunity`,
  `GenerateInvoiceFromOpportunity`.
- **Queues:** `AddToQueue`, `RemoveFromQueue`, `PickFromQueue`, `RouteTo`, `ReleaseToQueue`.
- **Email:** `SendEmail`, `SendEmailFromTemplate`, `DeliverIncomingEmail`, `DeliverPromoteEmail`.
- **Team / marketing-list membership:** `AddMembersTeam`, `RemoveMembersTeam`, `AddListMembersList`,
  `AddMemberList`, `RemoveMemberList`.
- **Case / contract:** `CloseIncident`, `CancelContract`, `CloneContract`.

A step registered through `OnMessage("...")` (any other message) receives the plain `ILocalPluginContext`.

A single-step `Execute(ctx)` override is not scoped to one message, so it does not expose `ctx.Request`. Read the
record there with `ctx.GetTarget<T>()` (a full entity for `Create`/`Update`), `ctx.GetTargetRef()` (the
`EntityReference` for `Delete`/`Associate`/`Disassociate`), or the message-specific `ctx.GetRequest<TRequest>()`.

| Message method | Handler context | `ctx.Request` | Primary record |
| --- | --- | --- | --- |
| `OnCreate()` | `ICreateContext<T>` | `CreateRequest` | `ctx.Target` (`T`), `ctx.Response.id` |
| `OnUpdate()` | `IUpdateContext<T>` | `UpdateRequest` | `ctx.Target` (`T`) |
| `OnDelete()` | `IDeleteContext<T>` | `DeleteRequest` | `ctx.TargetReference` |
| `OnCreateMultiple()` | `ICreateMultipleContext<T>` | `CreateMultipleRequest` | `ctx.TargetCollection`, `ctx.Response.Ids` |
| `OnUpdateMultiple()` | `IUpdateMultipleContext<T>` | `UpdateMultipleRequest` | `ctx.TargetCollection` |
| `OnRetrieve()` | `IRetrieveContext<T>` | `RetrieveRequest` | `ctx.TargetReference`, `ctx.Response.Entity` |
| `OnRetrieveMultiple()` | `IRetrieveMultipleContext<T>` | `RetrieveMultipleRequest` | `ctx.Request.Query` |
| `OnAssociate()` | `IAssociateContext<T>` | `AssociateRequest` | `ctx.TargetReference`, `ctx.Request.Relationship` |
| `OnDisassociate()` | `IDisassociateContext<T>` | `DisassociateRequest` | `ctx.TargetReference` |
| `OnMerge()` | `IMergeContext<T>` | `MergeRequest` | `ctx.TargetReference`, `ctx.Request.SubordinateId` |
| `OnGrantAccess()` | `IGrantAccessContext<T>` | `GrantAccessRequest` | `ctx.TargetReference`, `ctx.Request.PrincipalAccess` |
| `OnModifyAccess()` | `IModifyAccessContext<T>` | `ModifyAccessRequest` | `ctx.TargetReference` |
| `OnRevokeAccess()` | `IRevokeAccessContext<T>` | `RevokeAccessRequest` | `ctx.TargetReference`, `ctx.Request.Revokee` |

The scaffold generates an `AccountMessageReference.cs` cheat-sheet with one dormant (`Disabled()`) handler per
row, so you can copy the exact shape for the message you need. The deprecated `SetState` / `SetStateDynamicEntity`
and `Assign` messages are intentionally not modelled: update `statecode`/`statuscode` or `ownerid` with
`OnUpdate()` instead.

**Enabled / disabled** - steps are registered enabled by default. Mark a step `Disabled()` to register it dormant
(kill-switch or phased rollout); `apply` sets the step's state accordingly and `plan` shows the transition. Re-enabling
is just removing the `Disabled()` marker.

**Step configuration** - `UnsecureConfig("...")` and `SecureConfig("...")` declare the configuration strings passed to the
plugin constructor `(string unsecureConfig, string secureConfig)`. Both are reconciled by `apply` (the secure value is
stored on the linked `sdkmessageprocessingstepsecureconfig` row). Unsecure config appears verbatim in the `vela plan`
JSON (it is non-sensitive by design); the **secure** value is **redacted to a hash** in plan/inspect output (and any
`-o` plan file), so a changed secret is still detected without the value ever leaving Dataverse.

`MainOperation` is reserved for Custom API main handlers; it is not one of the typed stage methods, so a
regular plugin step cannot register there.

**Image validity is a compile error, not a runtime surprise.** The stage method you pick returns a builder
that exposes **only** the images that (message, stage) actually supports, so a wrong image does not compile -
in the IDE, without the CLI. For example `OnDelete().PostOperation()....PostImage()` and
`OnUpdate().PreValidation()....PreImage()` and `OnCreate().PostOperation()....PreImage()` all fail to build.
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
- `ctx.AcquireManagedIdentityToken(scopes)` - Acquire token via `IManagedIdentityService` when available
- `ctx.TraceMethod("details")` - Trace caller method name with correlation/stage/depth
- `ctx.TraceExecutionSnapshot("phase")` - Trace a structured context snapshot, including full Target/Image/Input/Output attribute values with PascalCase sections

Managed identity example:

```csharp
string token = ctx.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });
```

Dataverse operation tracing is automatic through wrapped services (`UserService`, `SystemService`, `CreateOrganizationService`): each call logs operation name (`Create`, `Update`, `RetrieveMultiple`, `Execute`, ...), timestamp, duration, and pipeline context.

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
protected override void Execute(ILocalPluginContext ctx)
{
    var target = ctx.GetTarget<Account>();
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
