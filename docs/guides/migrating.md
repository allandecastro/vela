# Migrating an existing plugin project to Vela

You do not need a greenfield project to adopt Vela. Existing Dataverse plugin and workflow-activity
codebases convert cleanly, and the conversion is usually a net simplification: less hand-rolled
infrastructure, typed models and queries instead of string keys, and a code-as-source-of-truth deploy
instead of the Plugin Registration Tool.

This guide is the practical recipe distilled from converting several real, hand-written codebases (from the
classic `PluginBase` + tuple template, to raw `IPlugin` classes, to fragmented multi-assembly solutions,
to large integration-heavy ones).

## Why migrate

- **Consolidation.** Solutions that fragmented into many per-feature assemblies, each carrying its own
  regenerated copy of the early-bound model, collapse to **one shared models project + one plugin package**.
- **Typed everywhere.** Magic attribute strings and hand-maintained option-set numbers become generated
  models and enums. Typing alone tends to surface latent bugs (for example a handler that writes to a
  post-operation target and never persists the change).
- **Less infrastructure.** The hand-rolled base plugin, local context, and message/stage/mode dispatch are
  replaced by `EntityPluginBase<T>` and a fluent step builder.
- **Modern outbound auth.** A stack of token acquisition + secret storage + custom decryption can be
  replaced by [managed identity](managed-identity.md).
- **Deploy from code.** [`vela plan` / `vela apply`](plan-apply.md) register steps, images, and Custom APIs
  from your code, replacing manual registration.

## Before you start

- Target `net48`. Microsoft supports plug-ins on .NET Framework 4.6.2 through 4.8 and recommends 4.8; 4.8.1 is
  not supported, and `vela` warns when a package targets it. Existing plug-ins on an older version keep running,
  but plan the move to 4.8.
- Reference the published `Vela.Plugins` package from NuGet. Do not pin an in-repo development version. Its
  dependency surface is deliberately lean so the plugin package loads in the Dataverse sandbox unmerged (no
  ILMerge / ILRepack step): the SDK core assemblies, `Microsoft.Extensions.DependencyInjection.Abstractions`
  (the abstractions only, not the concrete container), `Newtonsoft.Json`, and `System.ComponentModel.Annotations`. The package also carries its companion
  `Vela.Messages` (the typed request/response and model base types); no concrete DI container or heavy runtime
  closure comes along.
- Generate [early-bound models](models.md) with `vela models`. If you cannot connect to the org yet, you
  can stub the few models you need by hand to get building, then regenerate later.
- If your repository uses Central Package Management, a freshly scaffolded solution may fail to restore
  (NU1008). See [troubleshooting](troubleshooting.md); a local `Directory.Packages.props` that sets
  `ManagePackageVersionsCentrally` to `false` for the solution resolves it.

Scaffold a target solution first with [`vela init`](scaffold.md) so you have the project layout, csproj,
and package references to copy your logic into.

## Map your source to Vela

| In the source | In Vela |
|---------------|---------|
| Hand-rolled `PluginBase` + `Collection<Tuple<...>>`, or a raw `IPlugin` with manual `MessageName`/`Stage`/`Mode` gating | `EntityPluginBase<TEntity>` + `RegisterStep().On*().Stage().Mode().FilterOn(...).Handle(...)` (see [Write a plugin](plugins.md)) |
| Pre/Post images read by attribute string | Capture with `.WithPreImage(...)` / `.WithPostImage(...)`; read from the generated typed step context |
| Static data-access helpers | [Dependency-injected services](dependency-injection.md) resolved with `ctx.GetRequiredService<T>()` |
| `QueryExpression` / FetchXML with string names | [Typed queries](queries.md) `Query.For<T>()` |
| `CodeActivity` workflow activity | A [Custom API](custom-apis.md) (Vela does not host workflow activities) |
| Token acquisition + stored secret + custom decryption | [Managed identity](managed-identity.md) `ctx.AcquireManagedIdentityToken(scopes)` |
| Hand-rolled environment-variable reader | [`ctx.GetEnvironmentVariable`](environment-variables.md) |

### Steps and the typed context

Declare each step in `RegisterPluginSteps()`. When a step captures an image, Vela generates a typed step
context named after the handler, whose `PreImage` / `PostImage` expose exactly the captured columns:

```csharp
public sealed partial class AccountPlugin(string unsecure, string secure) : EntityPluginBase<Account>(unsecure, secure)
{
    protected override void RegisterPluginSteps()
        => RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage(a => a.Name)
            .Handle(OnUpdatePostSync);

    private void OnUpdatePostSync(OnUpdatePostSyncContext ctx)
    {
        Account target = ctx.Request.Target;
        string previousName = ctx.PreImage.Name; // typed projection of the captured columns
    }
}
```

The generated context (`OnUpdatePostSyncContext`) requires the class to be `partial`. Every named handler gets
one, with or without a captured image; a handler typed with the interface context (for example
`ICreatePreContext<Account>`) binds too.

The typed image is a projection of the captured columns, not the entity itself. When you need to hand the
whole image to a service typed on the early-bound entity, read it with `ctx.GetPreImage<T>()` /
`ctx.GetPostImage<T>()` (or cast the projection, `(Account)ctx.PreImage`):

```csharp
private void OnUpdatePreSync(OnUpdatePreSyncContext ctx)
    => ctx.GetRequiredService<IAccountRules>()
          .Apply(ctx.Request.Target, ctx.GetPreImage<Account>());
```

### Workflow activities become Custom APIs

Vela does not host `CodeActivity` workflow activities. Convert each one to a [Custom API](custom-apis.md). The
mapping is mechanical:

| In the workflow activity | In the Custom API |
| --- | --- |
| `CodeActivity` class | `CustomApiBase<TRequest, TResponse>` (or the lightweight `CustomApiBase`) |
| `[Input] InArgument<T>` | a request parameter (mark it `optional: true` if the activity's input was optional) |
| `[Output] OutArgument<T>` | a response property |
| `Execute(CodeActivityContext ctx)` body | `Execute(ILocalPluginContext ctx, TRequest request)` (typed) or `Handle(ILocalPluginContext ctx)` (lightweight) |
| the workflow's implicit primary entity | an explicit `EntityReference` request parameter |

Scaffold the shell with [`vela add customapi`](add.md) (`--prefix <publisherprefix>` names it): `--entity <Table>` generates a typed
`CustomApiBase<TRequest, TResponse>` with its request/response classes; `--global` generates a lightweight
`CustomApiBase` whose `Handle` reads `ctx.GetInputParameter<T>(...)` and writes `ctx.SetOutputParameter(...)`.
Then port the body. The lightweight global form (what the generic case below uses):

```csharp
// before: a workflow activity that recalculates a total on a record
public sealed class RecalculateTotal : CodeActivity
{
    [RequiredArgument] public InArgument<EntityReference> Target { get; set; }
    [Output] public OutArgument<Money> NewTotal { get; set; }
    protected override void Execute(CodeActivityContext context) { /* ... */ }
}

// after: vela add customapi RecalculateTotal --global --action --prefix dv
public sealed class RecalculateTotal(string u, string s) : CustomApiBase(u, s)
{
    protected override void RegisterCustomApi() =>
        RegisterCustomApi("dv_RecalculateTotal", Handle).AsAction()
            .RequestParameter<EntityReference>("Target")
            .ResponseParameter<Money>("NewTotal");

    private void Handle(ILocalPluginContext ctx)
        => ctx.SetOutputParameter("NewTotal", Recalculate(ctx, ctx.GetInputParameter<EntityReference>("Target")));
}
```

(For a model-typed version with `CustomApiBase<TRequest, TResponse>` and `.Request(...)` / `.Response(...)`, see
the [Custom API guide](custom-apis.md) - scaffold it with `--entity <Table>`.)

A clean input/output activity maps one to one. **The invocation surface changes:** callers that triggered the
activity from a classic workflow must be re-pointed at the Custom API - editing those workflows to call the new
API. Marking it `.WorkflowSdkStep(true)` only makes it *available* as a workflow step; it does not redirect the
existing activity steps.

**The generic "operate on any entity / any attribute" activity** becomes a **global** (unbound) Custom API with an
`EntityReference` parameter for the record plus a `string` parameter for the attribute name - there is no implicit
primary entity to lean on. If you mark it `WorkflowSdkStep(true)`, use `EntityReference` (an `Entity` /
`EntityCollection` / `StringArray` parameter is rejected on a workflow-enabled API), and pass several attribute
names as one delimited `string` rather than a `string[]`.

### Consolidate the layout

Follow the scaffold layout: a folder per table with `Plugins/`, `Services/`, and `CustomApis/`
subfolders, and `GlobalCustomApis/` for unbound Custom APIs. Keep the namespace flat (for example
`Zava.Plugins`) rather than following the folder path, because a namespace segment named after a table
collides with the generated entity type of the same name.

## Best practices learned from real conversions

- **Use primary constructors** for plugins, Custom APIs, and services.
- **Prefer the typed step context** where an image is captured.
- **Set defaults in PreOperation on the target**, not in PostOperation with an extra `Update`. A
  PreOperation change to the target persists with no extra write, no extra Update event, and no recursion.
- **Guard against recursion.** A PostOperation Update handler that writes back to its own record can
  re-fire itself. Gate on what actually changed (`FilterOn`, `HasChanged`) or flag the write with a shared
  variable, rather than checking `ctx.Depth` (see [Preventing recursion](plugins.md#preventing-recursion)).
- **Keep plugins stateless.** Instances are cached and shared across threads, so avoid mutable instance
  fields and mutable static state; the only fields should be the readonly configuration from the
  constructor.
- **Throw to roll back.** Synchronous steps run inside the platform transaction. Throw
  `InvalidPluginExecutionException` to abort and roll back; do not catch and swallow in a synchronous step.
- **Respect the sandbox.** No filesystem access, restricted outbound HTTP, and a two-minute execution
  budget. Route outbound calls through `HttpClient` with a [managed identity](managed-identity.md) token.
- **Extract logic into services.** Move shared data access out of the handler into DI services; this also
  makes the code straightforward to [test](testing.md).

## Known limits and escape hatches

- **N:N intersect traversal** is not expressible in the typed query today. Keep it as FetchXML through the
  [escape hatch](escape-hatches.md). (Aggregates are: see [Typed queries](queries.md).)
- **Several steps on one trigger** (same message, stage, mode and table) can live in one plugin class when their
  filtering attributes, images or relationship scope tell them apart; otherwise give each a `WithStepKey(...)`
  (VELA008 flags the ones that collide). See [Write a plugin](plugins.md).
- **Images at PreValidation.** A pre-image is available at PreValidation (Update, Delete, UpdateMultiple), but a
  post-image only at PostOperation, and Create never has a pre-image.
- **Unmodelled or advanced tables** (system tables, some intersect tables) may not have generated models
  yet; those reads stay late-bound until the model exists.

## Verify

Build the solution against the published package, then deploy from code:

```bash
dotnet build -c Release
vela plan  --packages-dir ./artifacts --solution <YourSolution> --interactive
vela apply --packages-dir ./artifacts --solution <YourSolution> --interactive
```

A clean build against `Vela.Plugins` from NuGet, with the plugin package produced, means the conversion is
ready to register. See [Plan & Apply](plan-apply.md) for the deployment loop and
[testing](testing.md) for exercising the converted plugins before you ship.
