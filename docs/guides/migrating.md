# Migrating an existing plugin project to Vela

This guide converts an existing Dataverse plugin or workflow-activity codebase to Vela: what to set up first, how
each part of the source maps to Vela, and how to verify and deploy the result. It applies to the classic
`PluginBase` + tuple template, raw `IPlugin` classes, and solutions split over many assemblies.

If you are starting from scratch instead, follow the [quickstart](../getting-started/quickstart.md).

## Why migrate

- **Consolidation.** Solutions that fragmented into many per-feature assemblies, each carrying its own
  regenerated copy of the early-bound model, collapse to **one shared models project and one plugin package**.
- **Typed everywhere.** Magic attribute strings and hand-maintained option-set numbers become generated
  models and enums. Typing alone tends to surface latent bugs (for example a handler that writes to a
  post-operation target and never persists the change).
- **Less infrastructure.** The hand-rolled base plugin, local context, and message/stage/mode dispatch are
  replaced by `EntityPluginBase<T>` and a fluent step builder.
- **Simpler outbound auth.** Token acquisition, secret storage and custom decryption can be replaced by a
  [managed identity](managed-identity.md).
- **Deploy from code.** [`vela plan` / `vela apply`](plan-apply.md) register steps, images, and Custom APIs
  from your code, replacing manual registration.

## Before you start

- Target `net48`, as the scaffold does. `vela plan` and `vela apply` warn when a package targets a later framework
  (such as `net481`), which Dataverse does not accept for plugins. See
  [Microsoft's plugin requirements](https://learn.microsoft.com/power-apps/developer/data-platform/write-plug-in)
  for the supported .NET Framework versions.
- Reference the published `Vela.Plugins` package from NuGet. Do not pin an in-repo development version. Its
  dependency surface is deliberately lean so the plugin package loads in the Dataverse sandbox unmerged (no
  ILMerge / ILRepack step): the SDK core assemblies, `Microsoft.Extensions.DependencyInjection.Abstractions`
  (the abstractions only, not a container), `Newtonsoft.Json` and `System.ComponentModel.Annotations`. It also
  brings its companion `Vela.Messages` (the typed request and response base types).
- Scaffold a target solution with [`vela init`](scaffold.md), so you have the project layout, project files and
  package references to copy your logic into. If your repository uses Central Package Management, the scaffold's
  own `Directory.Packages.props` turns it off for the new solution, so the inline package versions restore.
- Generate [early-bound models](models.md) with `vela models`. If you cannot connect to the environment yet,
  generate them offline from an exported solution with
  [`--from-solution`](models.md#offline-generation-from-an-exported-solution---from-solution), then regenerate
  from the environment later.

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

Scaffold the shell with [`vela add customapi`](add.md) (`--prefix <publisherprefix>` is the publisher prefix of
its unique name). With `--entity <Table>`, it generates a typed `CustomApiBase<TRequest, TResponse>` bound to that
table, with its request and response classes. With `--global`, it generates a lightweight `CustomApiBase` whose
`Handle` method reads `ctx.GetInputParameter<T>(...)` and writes `ctx.SetOutputParameter(...)`. Then port the
body. The lightweight global form, which the generic case below also uses:

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

    private static Money Recalculate(ILocalPluginContext ctx, EntityReference target) { /* the ported logic */ }
}
```

For the typed version with `CustomApiBase<TRequest, TResponse>` and `.Request(...)` / `.Response(...)`, see the
[Custom API guide](custom-apis.md); scaffold it with `--entity <Table>`.

An activity with clear inputs and outputs maps one to one. **The callers change:** a classic workflow that used the
activity must be edited to call the Custom API instead. Marking the API `.WorkflowSdkStep(true)` only makes it
*available* as a workflow step; it does not replace the existing activity steps.

**A generic "operate on any table or column" activity** becomes a **global** (unbound) Custom API with an
`EntityReference` parameter for the record and a `string` parameter for the column name, because there is no
implicit primary entity. If you mark it `WorkflowSdkStep(true)`, use `EntityReference` (an `Entity` /
`EntityCollection` / `StringArray` parameter is rejected on a workflow-enabled API), and pass several attribute
names as one delimited `string` rather than a `string[]`.

### Consolidate the layout

Follow the scaffold layout: a folder per table with `Plugins/`, `Services/`, and `CustomApis/`
subfolders, and `GlobalCustomApis/` for unbound Custom APIs. Namespaces follow the folders
(`Zava.Plugins.Account.Plugins`), so a namespace segment is named after a table: keep the `using` directives inside
a block namespace, or the segment hides the generated entity type of the same name (see
[Namespaces and usings](scaffold.md#namespaces-and-usings)).

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
- **Throw to roll back.** A synchronous step in the transaction rolls back when it throws
  `InvalidPluginExecutionException`; do not catch and swallow the error in a synchronous step (see
  [Errors](plugins.md#errors)).
- **Respect the sandbox.** No file system, outbound HTTP limited to what the administrator allows, and a time limit
  on the synchronous pipeline. See [Respect the sandbox](plugins.md#respect-the-sandbox).
- **Extract logic into services.** Move shared data access out of the handler into DI services; this also
  makes the code straightforward to [test](testing.md).

## Known limits and escape hatches

- **N:N traversal** in a typed query links through the intersect table, which needs its model
  (`vela models --intersect`, see [Intersect tables](models.md#intersect-nn-join-tables---intersect)). Until you
  generate it, keep the FetchXML through the [escape hatch](escape-hatches.md).
- **Several steps on one trigger** (same message, stage, mode and table) can live in one plugin class when their
  filtering attributes, images or relationship scope tell them apart; otherwise give each a `WithStepKey(...)`
  (VELA008 flags the ones that collide). See [Write a plugin](plugins.md).
- **Images depend on the message and stage.** Vela rejects an image the platform cannot provide (for example a
  post-image before PostOperation, or a pre-image on Create) when the step is built. See
  [Images](plugins.md#images).
- **Tables without a model.** Generate any table you read, including system tables, with
  `vela models --entities <table>`; a read stays late-bound until its table has a model.

## Verify

Build the solution against the published package (the plugin project writes its package to `artifacts/`), then
preview and deploy from code:

```bash
dotnet build -c Release
vela plan  --packages-dir ./artifacts --solution <YourSolution> --interactive
vela apply --packages-dir ./artifacts --solution <YourSolution> --interactive
```

A clean build against `Vela.Plugins` from NuGet that produces the plugin package means the conversion is ready to
register. `vela plan` shows what `apply` would change. See [Plan and apply](plan-apply.md) for the deployment loop
and [Testing](testing.md) to cover the converted plugins before you deploy.
