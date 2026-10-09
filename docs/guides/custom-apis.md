# Write a Custom API

For Custom APIs, use `CustomApiBase<TRequest, TResponse>` and the fluent registration hook. The request and
response derive from `CustomApiRequest`/`CustomApiResponse` (pass the API unique name to the base constructor):
each typed property is a one-liner over the base `Get<T>()` / `Set(value)` helpers, which key the underlying
`Parameters`/`Results` bags by the **plain property name** - the key Dataverse uses for a Custom API's
`InputParameters`/`OutputParameters` - and the base sets `RequestName`/`ResponseName` to the API unique name. A
single generated type therefore serves all three usages - authoring the Custom API, calling it
(`service.Execute(new ScoreRequest { ... }).ToResponse<ScoreResponse>()`), and reacting to it
(`On<ScoreRequest>()...Handle(...)`). Generate these types with [`vela models --custom-apis`](models.md) or write
them by hand as shown below:

```csharp
public class CalculateAccountScore : CustomApiBase<ScoreRequest, ScoreResponse>
{
    public CalculateAccountScore(string u, string s) : base(u, s) { }

    protected override void RegisterCustomApi()
    {
        RegisterCustomApi("zava_CalculateAccountScore")
            .DisplayName("Calculate Account Score")
            .Description("Calculates an account score")
            .AsAction()
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
            .WorkflowSdkStep(true)
            .Request(r => r
                .Add(x => x.AccountId)
                .Add(x => x.AsOfDate, optional: true, "Date to score against; defaults to today."))
            .Response(r => r.Add(x => x.Score));
    }

    protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
    {
        return new ScoreResponse { Score = ComputeScore(request.AccountId) };
    }
}

public class ScoreRequest : CustomApiRequest
{
    public ScoreRequest() : base("zava_CalculateAccountScore") { }

    public Guid AccountId
    {
        get => Get<Guid>();
        set => Set(value);
    }

    public DateTime AsOfDate
    {
        get => Get<DateTime>();
        set => Set(value);
    }
}

public class ScoreResponse : CustomApiResponse
{
    public ScoreResponse() : base("zava_CalculateAccountScore") { }

    public int Score
    {
        get => Get<int>();
        set => Set(value);
    }
}
```

> **Why three types and a two-argument `Execute`.** A Custom API is authored as an implementation class plus a
> typed request and a typed response. That shape is deliberate, not ceremony. The same request/response type is
> what you pass to `service.Execute(...)`, what `ToResponse<T>()` hands back, and what you bind with
> `On<TRequest>()`, so there is a single compile-safe, IntelliSense-friendly contract across authoring, calling
> and reacting - and `vela models --custom-apis` generates the typed request and response (and the `On<TRequest>()`
> reaction entry) for you, so the only part you write by hand is the implementation.
> `Execute(ILocalPluginContext ctx, TRequest request)` keeps the two concerns apart: `ctx` is the infrastructure
> (services, tracing, the org service) and `request` is the typed payload, so the body reads `request.AccountId`
> instead of digging values out of the context by string key. Collapsing this into one class or a single-argument
> `Execute(ctx)` would give back the untyped `ctx.InputParameters["..."]` access and lose the typed call/react
> round-trip - the opposite of what the typed contract buys you.

`CustomApiRequest` / `CustomApiResponse` (and the base `TypedRequest` / `TypedResponse` for standard messages)
live in `Vela.Messages`. The `Get<T>()` / `Set(value)` helpers infer the parameter key from the property name and
that name is the bag key as-is, so a property is a plain one-liner - no `Parameters.Contains(...)`
boilerplate and no key string to spell out. A read-only response property can drop the setter:
`public int Score => Get<int>();`. An absent parameter reads as `default`; a value of another type throws an
`InvalidCastException` naming the parameter, and a base `Entity` (what Dataverse sends) is converted to a declared
early-bound type such as `Account`.

> **Why a dedicated base.** A Custom API's request parameters and response properties are keyed by their plain
> unique name (for example `AccountId`, not `zava_CalculateAccountScore.AccountId`) - that is the key the plugin's
> `InputParameters`/`OutputParameters` use. `CustomApiRequest`/`CustomApiResponse` keep that plain-name keying from
> `TypedRequest`/`TypedResponse` and add the API unique name as the `RequestName`/`ResponseName`, so
> `service.Execute(new ScoreRequest { ... })` targets the right message without a hand-set `RequestName`. The naming
> is pinned by [`CustomApiParameterNamingTests`](https://github.com/allandecastro/vela/blob/main/tests/Vela.Tests/CustomApiParameterNamingTests.cs).

**Optional request parameters.** A request parameter you declare is **required** (`isoptional = false`, as in
Dataverse): a caller that omits it gets an error from the platform instead of your handler reading a default
value. That holds for `Add(x => x.AccountId)` and for the lightweight `RequestParameter<T>("Name")`, whether or
not the property carries `[Required]`. Mark one optional with `Add(x => x.AsOfDate, optional: true, "...")` (the
third argument sets the description) or `RequestParameter<T>("Name", optional: true)`. When you call neither
`Request(...)` nor `Response(...)`, the parameters are inferred from the model's properties, and an inferred
request parameter is optional unless the property carries `[Required]`. Optionality applies to **request
parameters only**: response properties have no optional flag (Dataverse's `customapiresponseproperty` has no
`isoptional`), so `optional: true` on a response property is ignored.

**The table of an entity parameter.** An `EntityReference`, `Entity` or `EntityCollection` parameter can name its
table (the Dataverse `logicalentityname`). An early-bound entity property (`Account`) uses its own table. Otherwise
declare it with the `logicalEntityName` argument, or with `[LogicalEntityName("contact")]` (in `Vela.Messages`) on the model property
(which also applies to inferred parameters); the argument wins over the attribute:

```csharp
.Request(r => r
    .Add(x => x.Owner, optional: false, logicalEntityName: "systemuser")
    .Add(x => x.Contact))                     // [LogicalEntityName("contact")] on the property
// lightweight
.RequestParameter<EntityReference>("Owner", logicalEntityName: "systemuser")
.ResponseParameter<EntityReference>("Created", logicalEntityName: "contact")
```

A table on any other type fails, and so does one on an `EntityCollection` response property, which Dataverse does
not allow. `apply` registers the table and, because `logicalentityname` is fixed at creation, recreates the
parameter when it changes.

**Rules checked when the definition is built.** `Build()` (run when `vela plan` scans the package and when the
plugin first runs) rejects, with a message naming the API and the parameter, the shapes Dataverse documents as
invalid:

- a function (`AsFunction()`) without a response property (Dataverse does not expose it, so calls get a 404);
- a workflow-enabled API (`WorkflowSdkStep(true)`) that is a function, that uses an `Entity`, `EntityCollection`
  or `string[]` parameter, or that uses an `EntityReference` without being bound to a table (`BoundTo<T>()`);
- a parameter name with anything other than letters, digits and underscores;
- the same parameter name twice in the request (or twice in the response), ignoring case. A request parameter and
  a response property may share a name.

`CustomApiBase<TRequest, TResponse>` constrains `TRequest : OrganizationRequest` and
`TResponse : OrganizationResponse` (the same constraint on `Register.RegisterCustomApi<TRequest, TResponse>()`
and `ICustomApiBuilder<TRequest, TResponse>`). `CustomApiRequest` / `CustomApiResponse` satisfy it, since they
derive from `TypedRequest` / `TypedResponse`, which are themselves an `OrganizationRequest` / `OrganizationResponse`.
This is why one generated type serves authoring, calling, and reacting: the request you build to
`service.Execute(...)` and the request you bind with `On<TRequest>()` are the exact type the Custom API is authored
with.

The framework hydrates the request over the live `InputParameters` (so `request.AccountId` reads the actual
input) and copies the response's `Results` back to `OutputParameters` after `Execute` returns. The generic base
exposes `RegisterCustomApi(...)`, and the same shape is available from
`Register.RegisterCustomApi<TRequest, TResponse>()` when you want to build manifests or scripts directly. Only
the request/response's own declared properties become Custom API parameters; the inherited
`TypedRequest`/`TypedResponse` members (`RequestName`, `Parameters`, `Results`, ...) never do.

For small Custom APIs, you can also choose the lightweight mode and declare parameters directly without dedicated request/response CLR models:

```csharp
public class CalculateAccountScoreLight : CustomApiBase
{
    public CalculateAccountScoreLight(string u, string s) : base(u, s) { }

    protected override void RegisterCustomApi()
    {
        RegisterCustomApi("zava_CalculateAccountScore", ExecuteScore)
            .DisplayName("Calculate Account Score")
            .Description("Calculates an account score")
            .AsAction()
            .RequestParameter<Guid>("AccountId")
            .ResponseParameter<int>("Score")
            .ResponseParameter<string>("Rating");
    }

    private void ExecuteScore(ILocalPluginContext ctx)
    {
        Guid accountId = ctx.GetInputParameter<Guid>("AccountId");
        ctx.SetOutputParameter("Score", ComputeScore(accountId));
        ctx.SetOutputParameter("Rating", "High");
    }
}
```

Use the generic base when you want typed request/response models and automatic parameter mapping; use the lightweight base when you prefer fewer files and direct access to `InputParameters` / `OutputParameters`. Both start from `RegisterCustomApi(...)`.

## Invoke and test

Call a Custom API through `IOrganizationService.Execute`. Generate typed request/response classes with
[`vela models --custom-apis`](models.md) so the call is strongly typed:

```csharp
var response = service.Execute(new zava_RecalculateRequest { Target = accountRef }).ToResponse<zava_RecalculateResponse>();
```

Run the handler with no connection using the [test harness](testing.md) - the same `Execute` call works
against the in-memory `IOrganizationService`.

In the handler, `ctx.PrimaryEntityName` and `ctx.PrimaryEntityId` follow the binding type, in the harness as on
Dataverse:

| Binding | `PrimaryEntityName` | `PrimaryEntityId` |
|---|---|---|
| Global | `"none"` | `Guid.Empty` |
| `BoundTo<T>()` | the bound table | the `Target` record's id |
| `BoundToCollection<T>()` | the bound table | `Guid.Empty` |

## React with a plugin step (registrability-gated)

To run your own step when a Custom API executes, `vela models --custom-apis` emits an `On<Message>()` builder
extension next to the generated request/response. Its shape is driven by the API's Dataverse metadata, so an
invalid registration does not compile:

```csharp
public sealed class AfterEstimate(string unsecure, string secure) : EntityPluginBase<Account>(unsecure, secure)   // API bound to account
{
    protected override void RegisterPluginSteps()
        => RegisterStep().OnAccountEstimateValue()   // only exists on IStepTrigger<Account>
            .PostOperation().Async()                  // AsyncOnly API: .Sync() does not compile
            .Handle(OnEstimated);

    private void OnEstimated(IMessagePostContext<zava_AccountEstimateValueRequest> ctx) { /* ... */ }
}
```

- **Entity applicability.** A bound (or collection-bound) API constrains the extension to `IStepTrigger<TEntity>`
  for its entity, so `OnAccountEstimateValue()` only compiles inside an `EntityPluginBase<Account>`; on any other
  entity it does not exist (a compile error). A global API emits a generic `On<Message><TEntity>()` instead.
- **Allowed step type.** An **AsyncOnly** API returns an async-only stage that offers only `PostOperation()` and
  only `.Async()` (an asynchronous step is PostOperation-only in Dataverse), so a synchronous or pre-stage
  registration does not compile. A **None** or **private** API emits no extension at all (only a comment), so
  there is no blessed way to register a step on it.
- **Escape hatch.** The generic `On<zava_AccountEstimateValueRequest>()` entry stays available for explicit,
  unchecked use (it always allows `.Sync()`); for a compile-time-safe manual async-only registration use
  `OnAsyncOnly<zava_AccountEstimateValueRequest>()`, which returns the same async-only stage as the generated
  extension. Dataverse also rejects an invalid registration at deploy time regardless.

The method name is the unique name minus its publisher prefix, PascalCased (`zava_AccountEstimateValue` becomes
`OnAccountEstimateValue`).

## Do / Don't (Custom APIs)

**Do**
- Override `RegisterCustomApi()` and start with `RegisterCustomApi(...)`.
- Keep metadata + handler binding in one fluent block.
- Prefer typed mode when request/response contracts are reused.

**Don't**
- Start new code with legacy `RegisterCustomApi(ICustomApiBuilder...)` overloads.
- Scatter metadata across multiple methods.
- Access raw `InputParameters` / `OutputParameters` when a typed request/response model is better.

## Changing an existing Custom API

Most Custom API metadata reconciles in place on the next `apply`: display name, description, request/response
parameters (including their descriptions), `ExecutePrivilege`, and `IsPrivate`. Without `.DisplayName(...)` or
`.Description(...)`, `apply` writes the unique name; a parameter without a description gets its name. Dataverse
stores at most 100 characters for the display name, 300 for the description and a request parameter's description,
and 100 for a response property's description; `vela plan` rejects a longer value before any write. `plan` and `apply` diff the request inputs and response outputs
**per parameter**, so a change reads as exactly which parameters were added, removed, or retyped rather than the
whole API showing as one opaque change.

A request parameter's or response property's **type**, **optionality** (`isOptional`) and **entity binding** are
fixed at creation (the `Type` column also encodes collection-ness). When one of them changes, `apply` **deletes
and recreates that one parameter** - only the affected parameter, leaving the Custom API and its other parameters
in place. An unchanged parameter is not written at all.

**Parameter types.** A parameter's CLR type must have an exact Custom API type: `bool`, `int`, `decimal`, `double`
(Float), `DateTime`, `Guid`, `string`, `string[]` (StringArray), `Money`, `OptionSetValue` (Picklist),
`EntityReference`, `Entity` (or an early-bound entity such as `Account`) and `EntityCollection`. Any other type
(`long`, `float`, an enum, `byte[]`, `List<string>`, ...) fails when the Custom API definition is built (and in
`vela plan`), with the type to use
instead: registering it as a lookalike would make the property read back as its default value.

Several **API-level** fields are fixed by Dataverse when the Custom API is first created and cannot be changed
afterward:

- function versus action (`AsFunction()` / `AsAction()`),
- the binding type (`BoundTo<T>()` versus global),
- the bound entity,
- the allowed custom-processing-step type,
- whether it is enabled for workflow (`WorkflowSdkStepEnabled`).

Dataverse silently ignores updates to these, so `apply` fails fast with a clear message rather than reporting a
change that never takes effect (which would otherwise re-appear on every plan). To change one of them, delete the
Custom API (remove it from the package and `apply`, or delete it in Dataverse) and re-apply to recreate it with
the new shape.

**Workflow-enabled Custom APIs** (`WorkflowSdkStepEnabled`) have extra rules that `apply` validates locally
before writing: the Custom API must be an action (not a function), and its parameters cannot use the `Entity`,
`EntityCollection`, or `StringArray` types. A violation fails with a clear message instead of an opaque platform
error. When applying into a solution, a Custom API's unique name must also carry the solution
[publisher prefix](#the-publisher-prefix) (as web resources and plugin packages do); `apply --solution` checks
this up front.

## The publisher prefix

A Custom API's unique name (like `dv_AccountEstimateValue`) is `prefix` + `_` + `Name`. The `prefix` part is the
**publisher customization prefix** of the Dataverse solution you deploy into, and it is worth understanding
because it trips people up.

- **What it is.** Every Dataverse solution belongs to a *publisher*, and every publisher has a short
  customization prefix (2-8 characters, for example `dv` or `acme`). Dataverse prepends `prefix_` to the unique
  name of anything you customize (tables, columns, Custom APIs, web resources) so that components from different
  publishers never collide. A Custom API without the prefix is rejected.
- **How Vela uses it.** `apply --solution <name>` reads the prefix from that solution's publisher and validates
  it up front: the plugin package name, every Custom API unique name, and web-resource names must all carry it.
  This is why `apply` fails fast with a clear message if a name does not start with the right prefix, instead of
  surfacing an opaque platform error later.
- **It is *not* the project prefix.** `vela init --prefix Zava` sets the **.NET project/namespace** prefix
  (`Zava.Plugins`, `Zava.Shared`, ...). That is a code-organization choice and is unrelated to the Dataverse
  publisher prefix. They are often different, and the *environment name* (for example `contoso-dev`) is unrelated
  to both. `vela add customapi` defaults a Custom API's unique-name prefix to a lower-cased form of the project
  prefix purely for convenience; if that does not match your target solution's publisher, pass
  `--prefix <publisherprefix>` (for example `vela add customapi Recalculate --global --action --prefix dv`) so the
  generated name is `dv_Recalculate`, or rename the unique name by hand.

## See also

- [Write a plugin](plugins.md) - steps, context helpers, and images.
- [`vela models`](models.md) - generate typed request/response classes.
- [Testing with Vela.Testing](testing.md) - run the handler with no connection.
