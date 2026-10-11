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
   The handler reads the primary record straight off the already-typed `ctx.Request.Target` / `ctx.Request.Targets`
   (an `EntityReference`, a bulk `Targets` collection, or - for a Vela-owned typed request like `Create`/`Update` -
   an early-bound `TEntity`). To expose the target early-bound for a message that carries a full entity `Target`,
   model a Vela-owned request generic over `TEntity` (see `Vela.Messages.CreateRequest<TEntity>`) so its
   `ctx.Request.Target` is the early-bound `TEntity` instead of the SDK's base `Entity`; otherwise the handler can
   always fall back to the `ctx.GetTarget<T>()` extension.
2. **Step surface (generated).** The `On<Message>()` trigger method (on `IStepTrigger<TEntity>`), the per-message
   `I<Message>Stages<TEntity>` stage selector, the stage/mode/config interfaces and their `StepBuilder<TEntity>`
   implementations (including the terminal `Handle(Action<I<Message>PreContext/PostContext<TEntity>>)` and the
   registrar delegation) are **generated** into `src/Vela.Plugins/Steps/StepApi.cs` and
   `src/Vela.Plugins/Steps/StepBuilder.Impl.cs` (both committed). Add **one line** to the generator's model,
   `tools/codegen/Vela.StepApiGen/MessageModel.cs`, stating whether the message supports `FilterOn(...)`, which
   images it allows (`Images.Pre`, `Images.Post`, `Images.PreAndPost`; pre-images are offered at every stage,
   PreValidation included, post-images at PostOperation only) and, when Dataverse runs it on a single table, that
   table (`Entity:`, checked by `Build()`), for example `new("QualifyLead", Entity: "lead"),` or
   `new("Update", FilterOn: true, Images: Images.PreAndPost),`. Then regenerate:

   ```bash
   dotnet run --project tools/codegen/Vela.StepApiGen
   ```

   Do not hand-edit the generated files - they are overwritten on the next run, and CI runs the generator with
   `-- --check`, which fails the build when the committed files differ from its output.
3. **`src/Vela.Plugins/Plugin/PluginBaseOfTEntity.cs`** - a `case` in `BuildTypedContext(definition, ctx)`, keyed by
   the modelled message name, that builds `<Message>PostContext` for a PostOperation step and `<Message>PreContext`
   otherwise, so the runtime hands the handler the stage-correct typed argument.
4. **`src/Vela.Plugins/Constants/PluginMessages.cs`** - the constant, named after the request, whose value is the
   `sdkmessage` the step registers on. They are not always the same (`SendEmail = "Send"`): check the
   `sdkmessagerequest` table of an environment, and list the request in `Constants/SdkMessageNames.cs` when they
   differ or when the message runs on a single table. Check also that a filter of the message allows custom steps
   (`sdkmessagefilter.iscustomprocessingstepallowed`): Dataverse refuses a step on the others (0x80044187), so they
   get no `On<Message>()`.
5. **`src/Vela.Cli/Cli/AddCommand.cs` - `KnownMessages`** - the easy-to-forget one. This array is the CLI's list of
   messages that render a typed `On<Message>()` call from `vela add plugin --message <Message>`. **It must stay in
   sync with the framework's typed methods**: leave the message out and the scaffold silently falls back to the
   permissive `OnMessage("<Message>")` even though the typed builder now exists.
6. **Check the sdkmessage on a live environment first.** The trigger is named after the SDK request, but the step
   registers on the sdkmessage, and for some business messages the two differ (`SendEmail` is `Send`, `WinQuote` is
   `Win`). Query `sdkmessage` / `sdkmessagefilter` for the request name: if it does not exist, add the mapping to
   `src/Vela.Plugins/Steps/SdkMessageNames.cs`. If the filter for your entity has
   `iscustomprocessingstepallowed = false`, do not model the message at all: Dataverse rejects every custom step on
   it (0x80044187).

## Verify

- Framework: `dotnet build Vela.sln` and run the plugin tests (the net462 suites run under Mono locally; the
  CLI suite runs under `dotnet test`).
- CLI wiring: assert the generated call in a `vela add` test (see `Add_Plugin_KnownMessage_RendersTypedOnCall`),
  and compile-check a generated plugin end to end with the build smoke test. Remember to clear the stale packed
  framework from the NuGet cache first - see the cache note under [Build and test](index.md#build-and-test).

## Deprecated messages are intentionally not modelled

`SetState` and `SetStateDynamicEntity` are deprecated by the platform. They deliberately get **no** typed
method: they are not in `KnownMessages` and have no `I<Message>Pre`/`PostContext`, so they render `OnMessage("...")`. Update
`statecode` / `statuscode` through an `Update` instead. (`Assign` is supported: use `OnAssign()`.) Do not add typed support for them.
