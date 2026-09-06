# Add a typed plugin message

Vela models a fixed set of Dataverse messages with a strongly-typed step context - the CRUD/bulk, sharing, sales
lifecycle, queue, email, membership and case/contract families (the full list is in
[Write a plugin](../guides/plugins.md)). Any other SDK message is reachable through the permissive `OnMessage("...")`
escape hatch, which hands the handler a plain `ILocalPluginContext`.

Contexts are **split by stage**: each message has an `I<Message>PreContext<TEntity>` (pre-stage handler:
`ctx.Request` + the typed target, no `ctx.Response`) and an `I<Message>PostContext<TEntity>` (post-stage handler:
`ctx.Request` + `ctx.Response`). Adding a new **modelled** message (so it gets its own `On<Message>()` builder
method plus those two contexts) is a small but **multi-file** change. Miss one step and the feature is only
half-wired - most commonly the CLI keeps generating `OnMessage("...")` instead of the typed call. Touch all of
these, in this order:

1. **`src/Vela.Plugins/Context/MessageContexts.cs`** - add **two** interfaces (`I<Message>PreContext<TEntity>` over
   `IStepRequestContext<TRequest>`, and `I<Message>PostContext<TEntity>` over
   `IStepRequestContext<TRequest>` + `IStepResponseContext<TResponse>`) and **two** sealed classes
   (`<Message>PreContext : PreStepContext<TRequest>`, `<Message>PostContext : PostStepContext<TRequest, TResponse>`).
   Attach `IEntityTargetContext<TEntity>` **only** when the message carries a full entity `Target`
   (`Create`/`Update`/`SendEmailFromTemplate`): it exposes `ctx.Target`, forwarding to the `GetTarget<T>()` extension
   so the handler gets the early-bound `TEntity` instead of the SDK's base `Entity`. For every other message add no
   target-capability interface: an `EntityReference` `Target` or a bulk `Targets` collection has no early-bound form,
   so the handler reads it straight off the already-typed `ctx.Request.Target` / `ctx.Request.Targets`.
2. **Step interfaces** - `src/Vela.Plugins/StepBuilder/MessageSteps.cs`: a pre-stage step (its `.Handler` takes
   `Action<I<Message>PreContext<TEntity>>`) and a post-stage step (`Action<I<Message>PostContext<TEntity>>`), each
   extending the shared image terminal the (message, stage) supports (`INoImageStep` / `IPreImageStep` /
   `IPostImageStep` / `IPreAndPostImageStep`), plus the `I<Message>StepStage<TEntity>` stage selector routing
   PreValidation/PreOperation to the pre step and PostOperation to the post step. A message that carries images
   (like Create/Update/Delete) needs a distinct step per stage so each binds the stage-correct context.
3. **`src/Vela.Plugins/StepBuilder/StepBuilder.cs`** - add the new interfaces to the class declaration list, add the
   `On<Message>()` entry method, and the explicit stage / mode / (filtering) / `Handler` implementations (one Handler
   per pre/post step).
4. **`src/Vela.Plugins/StepBuilder/IStepBuilder.cs`** - the public `On<Message>()` entry.
5. **`src/Vela.Plugins/Plugin/PluginStepsRegistrar.cs`** - the delegating `On<Message>()` for the multi-step router.
6. **`src/Vela.Plugins/Plugin/PluginBaseOfTEntity.cs`** - a `case` in `BuildTypedContext(message, stage, ctx)` that
   builds `<Message>PostContext` for a PostOperation step and `<Message>PreContext` otherwise, so the runtime hands
   the handler the stage-correct typed argument.
7. **`src/Vela.Plugins/Constants/PluginMessages.cs`** - the message-name constant, if it does not already exist.
8. **`src/Vela.Cli/Cli/AddCommand.cs` - `KnownMessages`** - the easy-to-forget one. This array is the CLI's list of
   messages that render a typed `On<Message>()` call from `vela add plugin --message <Message>`. **It must stay in
   sync with the framework's typed methods**: leave the message out and the scaffold silently falls back to the
   permissive `OnMessage("<Message>")` even though the typed builder now exists.

## Verify

- Framework: `dotnet build Vela.sln` and run the plugin tests (the net462 suites run under Mono locally; the
  CLI suite runs under `dotnet test`).
- CLI wiring: assert the generated call in a `vela add` test (see `Add_Plugin_KnownMessage_RendersTypedOnCall`),
  and compile-check a generated plugin end to end with the build smoke test. Remember to clear the stale packed
  framework from the NuGet cache first - see the cache note under [Build and test](index.md#build-and-test).

## Deprecated messages are intentionally not modelled

`SetState`, `SetStateDynamicEntity` and `Assign` are deprecated by the platform. They deliberately get **no** typed
method: they are not in `KnownMessages` and have no `I<Message>Pre`/`PostContext`, so they render `OnMessage("...")`. Update
`statecode` / `statuscode` (or `ownerid`) through an `Update` instead. Do not add typed support for them.
