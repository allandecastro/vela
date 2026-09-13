# Write a Custom API

For Custom APIs, use `CustomApiBase<TRequest, TResponse>` and the fluent registration hook. The request and
response derive from `TypedRequest`/`TypedResponse`: each typed property is a one-liner over the base
`Get<T>()` / `Set(value)` helpers (the parameter key defaults to the property name), reading and writing the
underlying `Parameters`/`Results` bags. A single generated type therefore serves all three usages - authoring
the Custom API, calling it (`service.Execute(new ScoreRequest { ... }).ToResponse<ScoreResponse>()`), and
reacting to it (`On<ScoreRequest>()...Handler(...)`). Generate these types with
[`vela models --custom-apis`](models.md) or write them by hand as shown below:

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
            .Request(r => r.Add(x => x.AccountId))
            .Response(r => r.Add(x => x.Score));
    }

    protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
    {
        return new ScoreResponse { Score = ComputeScore(request.AccountId) };
    }
}

public class ScoreRequest : TypedRequest
{
    public ScoreRequest() { RequestName = "zava_CalculateAccountScore"; }

    public Guid AccountId
    {
        get => Get<Guid>();
        set => Set(value);
    }
}

public class ScoreResponse : TypedResponse
{
    public int Score
    {
        get => Get<int>();
        set => Set(value);
    }
}
```

`TypedRequest` / `TypedResponse` live in `Vela.Messages`. The `Get<T>()` / `Set(value)` helpers infer the
parameter key from the property name, so a property is a plain one-liner - no `Parameters.Contains(...)`
boilerplate. A read-only response property can drop the setter: `public int Score => Get<int>();`.

`CustomApiBase<TRequest, TResponse>` constrains `TRequest : OrganizationRequest` and
`TResponse : OrganizationResponse` (the same constraint on `Register.RegisterCustomApi<TRequest, TResponse>()`
and `ICustomApiBuilder<TRequest, TResponse>`). Deriving from `TypedRequest` / `TypedResponse` satisfies it, since
those bases are themselves an `OrganizationRequest` / `OrganizationResponse`. This is why one generated type serves
authoring, calling, and reacting: the request you build to `service.Execute(...)` and the request you bind with
`On<TRequest>()` are the exact type the Custom API is authored with.

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

## React with a plugin step (registrability-gated)

To run your own step when a Custom API executes, `vela models --custom-apis` emits an `On<Message>()` builder
extension next to the generated request/response. Its shape is driven by the API's Dataverse metadata, so an
invalid registration does not compile:

```csharp
public sealed class AfterEstimate : EntityPluginBase<Account>   // API bound to account
{
    protected override void RegisterPluginSteps()
        => RegisterSteps().OnAccountEstimateValue()   // only exists on IStepBuilder<Account>
            .PostOperation().Async()                  // AsyncOnly API: .Sync() does not compile
            .Handler(OnEstimated);

    private void OnEstimated(IMessagePostContext<zava_AccountEstimateValueRequest> ctx) { /* ... */ }
}
```

- **Entity applicability.** A bound (or collection-bound) API constrains the extension to `IStepBuilder<TEntity>`
  for its entity, so `OnAccountEstimateValue()` only compiles inside an `EntityPluginBase<Account>`; on any other
  entity it does not exist (a compile error). A global API emits a generic `On<Message><TEntity>()` instead.
- **Allowed step type.** An **AsyncOnly** API returns an async-only stage that exposes only `.Async()`, so a
  synchronous registration does not compile. A **None** or **private** API emits no extension at all (only a
  comment), so there is no blessed way to register a step on it.
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
parameters, `ExecutePrivilege`, `IsPrivate`, and the allowed custom-processing-step types. A few fields are
fixed by Dataverse when the Custom API is first created and cannot be changed afterward:

- function versus action (`AsFunction()` / `AsAction()`),
- the binding type (`BoundTo<T>()` versus global),
- the bound entity.

Dataverse silently ignores updates to these, so `apply` fails fast with a clear message rather than reporting a
change that never takes effect (which would otherwise re-appear on every plan). To change one of them, delete
the Custom API (remove it from the package and `apply`, or delete it in Dataverse) and re-apply to recreate it
with the new shape.

Legacy names (`Register.Plugin`, `Register.CustomApi`, `ConfigureCustomApi`) are kept for backward compatibility, but new code should use the direct API (`RegisterSteps()` for plugins, `RegisterCustomApi(...)` for Custom APIs).

## See also

- [Write a plugin](plugins.md) - steps, context helpers, and images.
- [`vela models`](models.md) - generate typed request/response classes.
- [Testing with Vela.Testing](testing.md) - run the handler with no connection.
