# Add a typed plugin message

Vela models a fixed set of Dataverse messages with a strongly-typed step context - the CRUD/bulk, sharing, sales
lifecycle, queue, email, membership and case/contract families (the full list is in
[Write a plugin](../guides/plugins.md)). Any other SDK message is reachable through the permissive `OnMessage("...")`
escape hatch, which hands the handler a plain `ILocalPluginContext`.

Adding a new **modelled** message (so it gets its own `On<Message>()` builder method and an
`I<Message>Context<TEntity>` with typed `ctx.Request` / `ctx.Response`) is a small but **multi-file** change. Miss
one step and the feature is only half-wired - most commonly the CLI keeps generating `OnMessage("...")` instead of
the typed call. Touch all of these, in this order:

1. **`src/Vela.Plugins/Context/MessageContexts.cs`** - add the `I<Message>Context<TEntity>` interface and its
   sealed class. Attach the right target-capability interface so the primary record is exposed already typed:
   - `IEntityTargetContext<TEntity>` when the message carries a full entity `Target` (exposes `ctx.Target`),
   - `IReferenceTargetContext` when it carries an `EntityReference` `Target` (exposes `ctx.TargetReference`),
   - `ICollectionTargetContext` for a bulk `Targets` collection (exposes `ctx.TargetCollection`).
   The class forwards the accessor to the existing `GetTarget<T>()` / `GetTargetRef()` / `GetTargets()` extensions.
2. **A step interface** - `src/Vela.Plugins/StepBuilder/NoImageMessageSteps.cs` (message with no images) or
   `ImagedMessageSteps.cs` (message with images): `I<Message>Step<TEntity>` plus its `I<Message>StepStage<TEntity>`
   stage selector.
3. **`src/Vela.Plugins/StepBuilder/StepBuilder.cs`** - add the new interfaces to the class declaration list, add the
   `On<Message>()` entry method, and the explicit stage / mode / (filtering) / `Handler` implementations.
4. **`src/Vela.Plugins/StepBuilder/IStepBuilder.cs`** - the public `On<Message>()` entry.
5. **`src/Vela.Plugins/Plugin/PluginStepsRegistrar.cs`** - the delegating `On<Message>()` for the multi-step router.
6. **`src/Vela.Plugins/Plugin/PluginBaseOfTEntity.cs`** - a `case` in `BuildTypedContext(message, ctx)` that
   constructs the new context, so the runtime hands the handler the typed argument.
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
method: they are not in `KnownMessages` and have no `I<Message>Context`, so they render `OnMessage("...")`. Update
`statecode` / `statuscode` (or `ownerid`) through an `Update` instead. Do not add typed support for them.
