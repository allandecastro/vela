# Write a Custom API

For Custom APIs, use `CustomApiBase<TRequest, TResponse>` and the fluent registration hook:

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

public class ScoreRequest { public Guid AccountId { get; set; } }
public class ScoreResponse { public int Score { get; set; } }
```

The generic base exposes `RegisterCustomApi(...)`, and the same shape is available from `Register.RegisterCustomApi<TRequest, TResponse>()` when you want to build manifests or scripts directly.

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

Use the generic base when you want typed request/response models and automatic parameter mapping; use the lightweight base when you prefer fewer files and direct access to `InputParameters` / `OutputParameters`.

Both modes support the same direct pattern with `RegisterCustomApi(...)` as the registration entry point.

## Do / Don't (Custom APIs)

**Do**
- Override `RegisterCustomApi()` and start with `RegisterCustomApi(...)`.
- Keep metadata + handler binding in one fluent block.
- Prefer typed mode when request/response contracts are reused.

**Don't**
- Start new code with legacy `RegisterCustomApi(ICustomApiBuilder...)` overloads.
- Scatter metadata across multiple methods.
- Access raw `InputParameters` / `OutputParameters` when a typed request/response model is better.

Legacy names (`Register.Plugin`, `Register.CustomApi`, `ConfigureCustomApi`) are kept for backward compatibility, but new code should use the direct API (`RegisterSteps()` for plugins, `RegisterCustomApi(...)` for Custom APIs).
