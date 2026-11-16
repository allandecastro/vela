---
_description: "Typed request and response, parameters (required by default), binding, the rules Build() enforces, and changing a deployed Custom API"
---

# Write a Custom API

This page shows how to author a Custom API with Vela, call it, react to it with a plugin step, and change it once
it is deployed. For what a Custom API is on the platform, see
[Create and use Custom APIs](https://learn.microsoft.com/power-apps/developer/data-platform/custom-api) on
Microsoft Learn.

A Vela Custom API is an implementation class plus a typed request and a typed response. The same request and
response types serve three uses: authoring the API, calling it
(`service.Execute(new ScoreRequest { ... }).ToResponse<ScoreResponse>()`), and reacting to it with a plugin step
(`On<ScoreRequest>()`). Scaffold one with [`vela add customapi`](add.md), generate the classes of an existing API
with [`vela models --custom-apis`](models.md#custom-api-message-classes---custom-apis), or write them by hand:

[!code-csharp[](../samples/Vela.DocSamples/CustomApiSample.cs#typed)]

`Execute(ctx, request)` keeps the infrastructure (`ctx`: services, tracing) apart from the typed input
(`request`). The framework fills the request from `InputParameters` and copies the response to
`OutputParameters` after `Execute` returns.

## Request and response types

`CustomApiRequest` and `CustomApiResponse` (in `Vela.Messages`) derive from `TypedRequest` and `TypedResponse`, so
they are an `OrganizationRequest` and an `OrganizationResponse`. Their constructor takes the API unique name and
sets `RequestName` / `ResponseName`.

- **Keys are the property name.** `Get<T>()` and `Set(value)` key the `Parameters` and `Results` collections by the
  property name (`AccountId`, not `zava_CalculateAccountScore.AccountId`), which is the key Dataverse uses for a
  Custom API's inputs and outputs.
- **Back each property with `Get` / `Set`.** A plain auto-property (`{ get; set; }`) on the request would never
  receive the input, so the API throws when an input matches one.
- **Reading values.** An absent parameter reads as `default`. A value of another type throws an
  `InvalidCastException` that names the parameter. A base `Entity` (what Dataverse sends) is converted to the
  declared early-bound type, such as `Account`.
- **Only declared properties become parameters.** Inherited members (`RequestName`, `Parameters`, `Results`, ...)
  never do. A read-only response property is fine: `public int Score => Get<int>();`.

## Parameters

**Required by default.** A request parameter declared with `Add(x => x.Foo)` or `RequestParameter<T>("Foo")` is
required (`isoptional = false`), whether or not the property carries `[Required]`. Pass `optional: true` to make it
optional. Without a `Request(...)` call, the request parameters are inferred from the request properties, and an
inferred parameter is optional unless the property carries `[Required]`. Without a `Response(...)` call, the
response properties are inferred the same way. Response properties have no optionality in Dataverse.

**Description.** The third argument of `Add` (or `description:`), otherwise a `[Description]` attribute on the
property.

**Table of an entity parameter.** An `EntityReference`, `Entity` or `EntityCollection` parameter can name its table
(`logicalentityname`). An early-bound entity property (`Account`) uses its own table. Otherwise pass
`logicalEntityName`, or put `[LogicalEntityName("contact")]` (from `Vela.Messages`) on the property; the argument
wins over the attribute:

[!code-csharp[](../samples/Vela.DocSamples/CustomApiSample.cs#entity-parameter)]

With the lightweight builder:

[!code-csharp[](../samples/Vela.DocSamples/CustomApiSample.cs#entity-parameter-light)]

A table on any other type is rejected, and so is a table on an `EntityCollection` response property.

**Types.** A parameter's CLR type must map exactly to a Custom API type: `bool`, `int`, `decimal`, `double`
(Float), `DateTime`, `Guid`, `string`, `string[]` (StringArray), `Money`, `OptionSetValue` (Picklist),
`EntityReference`, `Entity` (or an early-bound entity) and `EntityCollection`. Any other type (`long`, `float`, an
enum, `byte[]`, `List<string>`, ...) is rejected when the definition is built, with the type to use instead.

## Rules checked when the definition is built

The definition is built when `vela plan` scans the package and when the plugin first runs. Vela rejects, with a
message that names the API and the parameter, the shapes Dataverse documents as invalid:

- a function (`AsFunction()`) without a response property;
- a workflow-enabled API (`WorkflowSdkStep(true)`) that is a function, that has an `Entity`, `EntityCollection` or
  `string[]` parameter, or that has an `EntityReference` parameter without being bound to a table;
- a parameter name with characters other than letters, digits and underscores;
- the same parameter name twice in the request (or twice in the response), ignoring case. A request parameter and
  a response property may share a name.

Recommendations that Dataverse does not enforce (for example a `Target` parameter on a bound API) are not checked.

## Lightweight mode

For a small API, skip the request and response classes and declare the parameters directly:

[!code-csharp[](../samples/Vela.DocSamples/CustomApiSample.cs#lightweight)]

## Call and test

Call the API with its typed request:

[!code-csharp[](../samples/Vela.DocSamples/CustomApiSample.cs#invoke)]

The [test harness](testing.md) runs the API with no connection, through the same pipeline as Dataverse (steps on
its message, input validation). In the handler, `ctx.PrimaryEntityName` and `ctx.PrimaryEntityId` follow the
binding, in the harness as on Dataverse:

| Binding | `PrimaryEntityName` | `PrimaryEntityId` |
|---|---|---|
| Global | `"none"` | `Guid.Empty` |
| `BoundTo<T>()` | the bound table | the `Target` record's id |
| `BoundToCollection<T>()` | the bound table | `Guid.Empty` |

## React with a plugin step

`vela models --custom-apis` writes an `On<Name>()` step extension next to the generated request and response. Its
shape follows the API's metadata, so an invalid registration does not compile. For an API bound to `account` that
allows async steps only:

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

The handler receives its generated `{Handler}Context`, as with `On<TRequest>()`: a
`MessagePostContext<TRequest, TEntity>` (`MessagePreContext<TRequest, TEntity>` at a pre-stage) nested in the
plugin class, which is why the class is `partial`. It has a typed `ctx.Request` and, when the step captures an
image, `ctx.PreImage` / `ctx.PostImage` typed to the captured columns.

- **Table.** A bound or collection-bound API's extension exists on `IStepTrigger<TEntity>` for its table only,
  when that table is generated in the same `vela models` run (`--entities account --custom-apis ...`). Otherwise,
  and for a global API, the extension is a generic `On<Name><TEntity>()`.
- **Allowed step type.** An AsyncOnly API offers only `PostOperation().Async()`. An API that allows no custom
  processing step, or a private API, gets no extension.
- **Without the extension.** `On<TRequest>()` stays available, unchecked; `OnAsyncOnly<TRequest>()` gives the
  async-only stages by hand.

The method name is the unique name without its publisher prefix, in PascalCase (`zava_AccountEstimateValue` becomes
`OnAccountEstimateValue`). When two APIs would produce the same name (`zava_Calc` and `acme_Calc`), both keep their
prefix (`OnZavaCalc`, `OnAcmeCalc`).

## Change a deployed Custom API

`plan` and `apply` compare the API and each parameter separately.

- **Updated in place:** display name, description, parameter descriptions, `ExecutePrivilege` and `Private()`.
  Without `.DisplayName(...)` or `.Description(...)`, `apply` writes the unique name; a parameter without a
  description gets its name. `vela plan` rejects, before any write, a value longer than Dataverse stores: display
  name 100 characters, description 300, request parameter description 300, response property description 100.
- **Parameter recreated:** a change to a parameter's type, optionality or table, which are fixed at creation. Only
  that parameter is deleted and recreated.
- **Never changed:** function or action, the binding type, the bound table, the allowed custom processing step
  type and `WorkflowSdkStep`. Dataverse ignores updates to these, so `apply` fails with a message. To change one,
  remove the API from the package, run `apply`, then add it back with the new shape.
- **Ownership:** `apply` stops when a Custom API with the same unique name is implemented by another assembly.

## The publisher prefix

A Custom API's unique name (`zava_AccountEstimateValue`) is the **publisher customization prefix** of the solution
you deploy into, then `_`, then the name. Dataverse rejects a Custom API without it.

- `apply --solution <name>` reads the prefix from that solution's publisher and checks it up front on the plugin
  package name, every Custom API unique name and every web resource name.
- It is **not** the .NET project prefix. `vela init --prefix Zava` names the projects and namespaces
  (`Zava.Plugins`). `vela add customapi` takes the publisher prefix as `--prefix <publisherprefix>` (required) and
  never derives it from the project prefix. `vela solution show <solution> --json` prints a solution's prefix.

## See also

- [Write a plugin](plugins.md): steps, context helpers and images.
- [`vela models`](models.md#custom-api-message-classes---custom-apis): generate typed request and response classes.
- [`vela add`](add.md): scaffold a Custom API and its tests.
- [Testing with Vela.Testing](testing.md): run the API with no connection.
