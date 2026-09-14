# Architecture

This page describes how the Vela framework repository is laid out and the rules that
keep its concerns separated. It is aimed at people building or contributing to the
framework itself; if you just want to *use* Vela, start with the
[CLI overview](../guides/cli-overview.md).

## Solution layout

```text
Vela.sln
src/
  Vela.Plugins/   # Framework runtime, contracts, primitives, and shared definitions
  Vela.Cli/       # CLI (command `vela`) for manifests and Dataverse sync
  Vela.Testing/   # Test harness package
tests/
  Vela.Tests/          # Runtime/framework unit tests
  Vela.Testing.Tests/  # In-memory harness/pipeline tests
  Vela.Cli.Tests/      # CLI unit tests
.github/workflows/ci.yml
```

## Architecture rules

Keep these rules in mind when adding new code:

1. **Core** (`Vela.Plugins`) contains framework-level runtime code, contracts, primitives, and generic immutable definitions.
2. **Tools** (`Vela.Cli`) contains the shared CLI used locally and in CI.
3. Generated early-bound model code belongs to the **consumer/plugin repo**, not this framework repo.
4. Use the framework APIs to build plugins and metadata generators; do not keep sample app code in the release path.
5. Keep registration metadata and runtime execution concerns separate.

## How a plugin is built

A Vela plugin is a normal Dataverse `IPlugin`, but you never implement `IPlugin` directly or touch the raw
`IServiceProvider`. Instead you inherit `EntityPluginBase<TEntity>` and describe *what* the plugin does; the
framework owns the plumbing. There are two clearly separated concerns, and they meet only through one immutable
value object, the **`StepDefinition`**:

- **Registration metadata** - *when* the plugin should fire (message, stage, mode, filtering, images, config).
  Declared in `RegisterPluginSteps()`, compiled to `StepDefinition`s, and reconciled into Dataverse by
  `vela plan` / `vela apply`. This never runs your business logic.
- **Runtime execution** - *what* happens when it fires. The sandbox instantiates your class and calls it; the
  base routes the call to the right typed handler.

```text
                         ┌─────────────────────── build / design time ───────────────────────┐
RegisterPluginSteps()  →  RegisterSteps().OnUpdate().PostOperation().Mode(..).Filtering(..).Handler(OnX)
        │                        (type-state fluent builder: message → stage → mode → filtering → images)
        ▼
   StepBuilder<TEntity>  ──Build()──►  StepDefinition   ──►  vela plan / apply  ──►  Dataverse sdkmessageprocessingstep
        │                              (registration metadata: message, stage, mode, filtering, images, keys, config)
        │
        └────────────────────────────── runtime ──────────────────────────────┐
                                                                               ▼
Dataverse pipeline  →  IPlugin.Execute(IServiceProvider)  →  PluginBase  →  ILocalPluginContext
                                                                 │   (depth guard, service container, wrapped
                                                                 │    tracing services, execution snapshots)
                                                                 ▼
                                        EntityPluginBase.ExecuteDataversePlugin(ctx)
                                                                 │  match the pipeline execution to one StepDefinition
                                                                 ▼
                        BuildTypedContext(message, stage, ctx)  →  I<Message>Context<TEntity>  →  .Handler(OnX) method
```

### The layers, bottom to top

1. **`PluginBase` (the pipeline entry).** Implements `IPlugin.Execute(IServiceProvider)`. It builds the
   `ILocalPluginContext` - the depth guard, the per-execution dependency-injection container, the org services
   wrapped with automatic Dataverse-operation tracing, and the start/success/exception execution snapshots - then
   calls the single abstract method `ExecuteDataversePlugin(ILocalPluginContext)`. Everything below sees only the
   typed context, never the raw `IServiceProvider`.
2. **`EntityPluginBase<TEntity>` (the router).** Ties the plugin to one early-bound entity. On first use it runs
   `RegisterPluginSteps()` once, collects the `StepDefinition`s, and caches them (thread-safe, because the sandbox
   reuses one instance concurrently). At runtime it matches the current pipeline execution (message + stage + mode
   + entity) to exactly one step, then either invokes that step's typed handler or, for a single-step plugin with
   no handler, calls your `Execute(ctx)` override. Ambiguous or missing handlers fail fast with a clear message.
3. **`StepBuilder<TEntity>` (the type-state builder).** `RegisterSteps()` returns a fluent chain whose *type* at
   each position only exposes the calls that are legal next: message → stage → mode → (filtering, where required)
   → images. The (message, stage) pair you pick only offers the images that combination actually supports, so an
   invalid image is a compile error, not a runtime surprise. `Build()` produces the immutable `StepDefinition`
   and re-checks the invariants as a backstop for reflection/out-of-order assembly.
4. **Typed message contexts (the socle).** For a modelled message the router wraps the runtime context in an
   `I<Message>Context<TEntity>` (for example `IUpdatePostContext<Account>`). That context is a thin, allocation-cheap
   view: `ctx.Response` is the **standard SDK response class** (`CreateResponse`, `UpdateResponse`, ...) and
   `ctx.Request` is a **Vela-owned typed request** for `Create` / `Update` / `SendEmailFromTemplate`
   (`Vela.Messages.CreateRequest<TEntity>` / `UpdateRequest<TEntity>` / `SendEmailFromTemplateRequest<TEntity>`) or the
   standard SDK request for every other message, each projected over the pipeline's `InputParameters` /
   `OutputParameters` `ParameterCollection`s, and every message reads its primary record off the typed
   `ctx.Request.Target` / `ctx.Request.Targets` (an early-bound `TEntity` on `Create` / `Update`; the SDK's `Entity` /
   `EntityReference` / `EntityCollection` otherwise). The
   handler is bound by **method group**, so the message and the handler's context type are checked by the compiler:
   an `OnUpdate()` step bound to an `ICreateContext` handler does not build. A non-modelled `OnMessage("...")` step
   receives the plain `ILocalPluginContext`.

### Why this shape

Keeping registration metadata (`StepDefinition`) separate from execution is what lets the **same source** drive
both the deployment (`plan`/`apply` diff the definitions against the environment) and the runtime dispatch, with no
duplicated "where does this fire" configuration. Pushing image/stage validity into the *types* moves a whole class
of registration mistakes from a failed deploy to a failed compile. And projecting the typed context over the
pipeline's own `ParameterCollection`s (SDK responses, and thin typed requests over the live `InputParameters`,
rather than parallel DTOs) means the typed context is a convenience layer over the real pipeline data, never a copy
that can drift from it.
