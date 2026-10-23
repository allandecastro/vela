# Write a Custom API

A Custom API is an implementation class plus a typed request and a typed response. The same request/response
types serve all three usages: authoring the API, calling it
(`service.Execute(new ScoreRequest { ... }).ToResponse<ScoreResponse>()`) and reacting to it with a plugin step
(`On<ScoreRequest>()`). Generate them with [`vela models --custom-apis`](models.md) or write them by hand:

```csharp
using System;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;

public sealed class CalculateAccountScore(string unsecureConfig, string secureConfig)
    : CustomApiBase<ScoreRequest, ScoreResponse>(unsecureConfig, secureConfig)
{
    protected override void RegisterCustomApi()
    {
        RegisterCustomApi("zava_CalculateAccountScore")
            .DisplayName("Calculate Account Score")
            .Description("Calculates an account score.")
            .AsAction()
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
            .Request(r => r
                .Add(x => x.AccountId)
                .Add(x => x.AsOfDate, optional: true, "Date to score against; defaults to today."))
            .Response(r => r.Add(x => x.Score));
    }

    protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
        => new ScoreResponse { Score = request.AccountId == Guid.Empty ? 0 : 42 };
}

public sealed class ScoreRequest : CustomApiRequest
{
    public ScoreRequest() : base("zava_CalculateAccountScore") { }

    public Guid AccountId { get => Get<Guid>(); set => Set(value); }

    public DateTime AsOfDate { get => Get<DateTime>(); set => Set(value); }
}

public sealed class ScoreResponse : CustomApiResponse
{
    public ScoreResponse() : base("zava_CalculateAccountScore") { }

    public int Score { get => Get<int>(); set => Set(value); }
}
```

`Execute(ctx, request)` keeps infrastructure (`ctx`: services, tracing) apart from the typed payload (`request`).
The framework hydrates the request from `InputParameters` and copies the response back to `OutputParameters` after
`Execute` returns.

## Request and response types

`CustomApiRequest` / `CustomApiResponse` (in `Vela.Messages`) derive from `TypedRequest` / `TypedResponse`, so they
are an `OrganizationRequest` / `OrganizationResponse`. Their constructor takes the API unique name and sets
`RequestName` / `ResponseName`.

- **Keys are the plain property name.** `Get<T>()` / `Set(value)` key the `Parameters` / `Results` bags by the
  property name (`AccountId`, not `zava_CalculateAccountScore.AccountId`), which is the key Dataverse uses for a
  Custom API's inputs and outputs.
- **Reading values.** An absent parameter reads as `default`. A value of another type throws an
  `InvalidCastException` naming the parameter. A base `Entity` (what Dataverse sends) is converted to a declared
  early-bound type such as `Account`.
- **Only declared properties become parameters.** Inherited members (`RequestName`, `Parameters`, `Results`, ...)
  never do. A read-only response property is fine: `public int Score => Get<int>();`.

## Parameters

**Required by default.** A request parameter you declare with `Add(x => x.Foo)` or `RequestParameter<T>("Foo")` is
required (`isoptional = false`), whether or not the property carries `[Required]`. Pass `optional: true` to make it
optional. When you call neither `Request(...)` nor `Response(...)`, the parameters are inferred from the model's
properties, and an inferred request parameter is optional unless the property carries `[Required]`. Response
properties have no optionality in Dataverse.

**Description.** The third argument of `Add` (or `description:`), else a `[Description]` attribute on the property.

**Table of an entity parameter.** An `EntityReference`, `Entity` or `EntityCollection` parameter can name its table
(`logicalentityname`). An early-bound entity property (`Account`) uses its own table. Otherwise pass
`logicalEntityName`, or put `[LogicalEntityName("contact")]` (from `Vela.Messages`) on the model property; the
argument wins over the attribute:

```csharp
.Request(r => r
    .Add(x => x.Owner, optional: false, logicalEntityName: "systemuser")
    .Add(x => x.Contact))                     // [LogicalEntityName("contact")] on the property
// lightweight builder
.RequestParameter<EntityReference>("Owner", logicalEntityName: "systemuser")
.ResponseParameter<EntityReference>("Created", logicalEntityName: "contact")
```

A table on any other type fails, and so does one on an `EntityCollection` response property.

**Types.** A parameter's CLR type must map exactly to a Custom API type: `bool`, `int`, `decimal`, `double`
(Float), `DateTime`, `Guid`, `string`, `string[]` (StringArray), `Money`, `OptionSetValue` (Picklist),
`EntityReference`, `Entity` (or an early-bound entity) and `EntityCollection`. Any other type (`long`, `float`, an
enum, `byte[]`, `List<string>`, ...) fails when the definition is built, with the type to use instead.

## Rules checked by `Build()`

`Build()` runs when `vela plan` scans the package and when the plugin first runs. It rejects, with a message naming
the API and the parameter, the shapes Dataverse documents as invalid:

- a function (`AsFunction()`) without a response property;
- a workflow-enabled API (`WorkflowSdkStep(true)`) that is a function, that has an `Entity`, `EntityCollection` or
  `string[]` parameter, or that has an `EntityReference` parameter without being bound to a table;
- a parameter name with characters other than letters, digits and underscores;
- the same parameter name twice in the request (or twice in the response), ignoring case. A request parameter and a
  response property may share a name.

Recommendations that Dataverse does not enforce (for example a `Target` parameter on a bound API) are not checked.

## Lightweight mode

For a small API, skip the request/response classes and declare parameters directly:

```csharp
using System;
using Vela.Context;
using Vela.CustomApi;

public sealed class CalculateAccountScoreLight(string unsecureConfig, string secureConfig)
    : CustomApiBase(unsecureConfig, secureConfig)
{
    protected override void RegisterCustomApi()
    {
        RegisterCustomApi("zava_CalculateAccountScore", ExecuteScore)
            .DisplayName("Calculate Account Score")
            .Description("Calculates an account score.")
            .AsAction()
            .RequestParameter<Guid>("AccountId")
            .ResponseParameter<int>("Score");
    }

    private void ExecuteScore(ILocalPluginContext ctx)
    {
        Guid accountId = ctx.GetInputParameter<Guid>("AccountId");
        ctx.SetOutputParameter("Score", accountId == Guid.Empty ? 0 : 42);
    }
}
```

## Invoke and test

```csharp
var response = service.Execute(new ScoreRequest { AccountId = accountId }).ToResponse<ScoreResponse>();
```

The [test harness](testing.md) runs the API with no connection, through the same pipeline as Dataverse (steps on
its message, input validation). In the handler, `ctx.PrimaryEntityName` and `ctx.PrimaryEntityId` follow the
binding, in the harness as on Dataverse:

| Binding | `PrimaryEntityName` | `PrimaryEntityId` |
|---|---|---|
| Global | `"none"` | `Guid.Empty` |
| `BoundTo<T>()` | the bound table | the `Target` record's id |
| `BoundToCollection<T>()` | the bound table | `Guid.Empty` |

## React with a plugin step

`vela models --custom-apis` emits an `On<Name>()` step extension next to the generated request/response. Its shape
follows the API's metadata, so an invalid registration does not compile:

```csharp
namespace Zava.Plugins.Account.Plugins
{
    using Vela.Context;
    using Vela.Plugin;
    using Zava.Shared.Models.Generated;

    public sealed partial class AfterEstimate(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)   // the API is bound to account
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnAccountEstimateValue()   // only exists on IStepTrigger<Account>
                .PostOperation().Async()                  // AsyncOnly API: .Sync() does not compile
                .Handle(OnEstimated);

        private void OnEstimated(OnEstimatedContext ctx)
        {
            var request = ctx.Request;   // zava_AccountEstimateValueRequest, typed
        }
    }
}
```

The handler gets its generated `{Handler}Context`, as with `On<TRequest>()`: a `MessagePostContext<TRequest, TEntity>`
(`MessagePreContext<TRequest, TEntity>` at a pre-stage) nested in the plugin class, which is why the class is
`partial`. It has a typed `ctx.Request` and, when the step captures an image, `ctx.PreImage` / `ctx.PostImage`
typed to the captured columns.

- **Entity.** A bound or collection-bound API extends `IStepTrigger<TEntity>` for its table only. A global API gets
  a generic `On<Name><TEntity>()`.
- **Allowed step type.** An AsyncOnly API offers only `PostOperation().Async()`. A `None` or private API gets no
  extension.
- **Escape hatch.** `On<TRequest>()` stays available unchecked; `OnAsyncOnly<TRequest>()` gives the async-only stage
  by hand.

The method name is the unique name minus its publisher prefix, PascalCased (`zava_AccountEstimateValue` becomes
`OnAccountEstimateValue`).

## Changing an existing Custom API

`plan` and `apply` diff the API and each parameter separately.

- **Updated in place:** display name, description, parameter descriptions, `ExecutePrivilege` and `Private()`.
  Without `.DisplayName(...)` or `.Description(...)`, `apply` writes the unique name; a parameter without a
  description gets its name. `vela plan` rejects, before any write, a value longer than Dataverse stores: display
  name 100 characters, description 300, request parameter description 300, response property description 100.
- **Parameter recreated:** a change to a parameter's type, optionality or table (all fixed at creation). Only that
  parameter is deleted and recreated.
- **Never changed:** function versus action, the binding type, the bound table, the allowed custom processing step
  type and `WorkflowSdkStep`. Dataverse ignores updates to these, so `apply` fails with a message. Remove the API
  from the package, `apply`, then add it back with the new shape.
- **Ownership:** `apply` stops when a Custom API with the same unique name is implemented by another assembly.

## The publisher prefix

A Custom API's unique name (`dv_AccountEstimateValue`) is the **publisher customization prefix** of the solution
you deploy into, then `_`, then the name. Dataverse rejects a Custom API without it.

- `apply --solution <name>` reads the prefix from that solution's publisher and checks it up front on the plugin
  package name, every Custom API unique name and every web resource name.
- It is **not** the .NET project prefix. `vela init --prefix Zava` names the projects and namespaces
  (`Zava.Plugins`). `vela add customapi` defaults the unique-name prefix to a lower-cased project prefix; pass
  `--prefix <publisherprefix>` when that does not match your solution's publisher.

## See also

- [Write a plugin](plugins.md): steps, context helpers and images.
- [`vela models`](models.md): generate typed request/response classes.
- [Testing with Vela.Testing](testing.md): run the API with no connection.
