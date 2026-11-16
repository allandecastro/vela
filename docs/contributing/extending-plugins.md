---
_description: "The files a new modelled message touches"
---

# Add a typed plugin message

This page explains how to add a modelled message to the framework: a message with its own typed trigger
(`On<Message>()`) and its own stage-typed contexts. It is for framework contributors.

Vela models a fixed set of Dataverse messages with a typed step context: the CRUD and bulk messages, sharing, the
sales lifecycle, queues, email, membership, and the case and contract families. The full list is in
[Write a plugin](../guides/plugins.md). Any other SDK message is reachable through its request type,
`On<XxxRequest>()` (an SDK request, or one that `vela models --messages` generates), which gives the handler the
request-typed `IMessagePreContext<XxxRequest>` or `IMessagePostContext<XxxRequest>`. There is no string form.

Contexts are split by stage. Each modelled message has:

- an `I<Message>PreContext<TEntity>` for pre-stage handlers: `ctx.Request` and the typed target, no `ctx.Response`;
- an `I<Message>PostContext<TEntity>` for post-stage handlers: `ctx.Request` and `ctx.Response`.

Adding a modelled message touches several files. Miss one and the message is only half wired; most often, `vela add`
keeps generating `On<XxxRequest>()` instead of the typed call.

## Steps

Make these changes in this order.

1. **Check the sdkmessage on a live environment.** The trigger is named after the SDK request, but the step
   registers on the sdkmessage, and for some business messages the two differ (`SendEmail` registers on `Send`,
   `WinQuote` on `Win`). Query `sdkmessage` and `sdkmessagefilter` for the request name:
   - If no sdkmessage has that name, add the mapping to `src/Vela.Plugins/Steps/SdkMessageNames.cs`. It lists every
     request of `sdkmessagerequest` whose name is not an sdkmessage, with the table it runs on.
   - If the message accepts steps on a known set of tables only, note those tables for `Entities:` in step 3.
   - If its only filter is `none` (a global message), leave `Entities:` unset and add the message to
     `PluginMessages.GlobalMessages`: the builder then registers the step on no table.
   - If the filter for your table has `iscustomprocessingstepallowed = false`, do not model the message:
     Dataverse does not accept custom steps on it (see
     [sdkmessagefilter](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/sdkmessagefilter)
     on Microsoft Learn).
2. **`src/Vela.Plugins/Context/MessageContexts.cs`.** Add two interfaces and two classes:
   - `I<Message>PreContext<TEntity>` over `IStepRequestContext<TRequest>`;
   - `I<Message>PostContext<TEntity>` over `IStepRequestContext<TRequest>` and `IStepResponseContext<TResponse>`;
   - `<Message>PreContext<TEntity> : PreStepContext<TRequest>`;
   - `<Message>PostContext<TEntity> : PostStepContext<TRequest, TResponse>`.

   The handler reads the primary record from the typed `ctx.Request.Target` or `ctx.Request.Targets`: an
   `EntityReference`, a bulk `Targets` collection, or an early-bound `TEntity` for a Vela-owned typed request such as
   `Create` or `Update`. To expose an early-bound target for a message that carries a full entity `Target`, model a
   Vela-owned request generic over `TEntity` (see `Vela.Messages.CreateRequest<TEntity>`), so `ctx.Request.Target`
   is the early-bound `TEntity` instead of the SDK's `Entity`. Otherwise the handler can fall back to the
   `ctx.GetTarget<T>()` extension.
3. **The step surface (generated).** The `On<Message>()` trigger on `IStepTrigger<TEntity>`, the
   `I<Message>Stages<TEntity>` stage selector, the stage, mode and configuration interfaces, their
   `StepBuilder<TEntity>` implementations (including the terminal `Handle(...)` and the registrar delegation) and the
   `TypedTriggers` table are generated into `src/Vela.Plugins/Steps/StepApi.cs`, `StepBuilder.Impl.cs` and
   `TypedTriggers.cs` (all committed). Add one line to the generator's model,
   `tools/codegen/Vela.StepApiGen/MessageModel.cs`. It states:
   - whether the message supports `FilterOn(...)`;
   - which images it allows (`Images.Pre`, `Images.Post`, `Images.PreAndPost`). Pre-images are offered at every
     stage, PreValidation included; post-images at PostOperation only;
   - when Dataverse accepts a step on a known set of tables only, those tables (`Entities:`, checked by `Build()`).

   For example, `new("QualifyLead", Entities: ["lead"]),` or `new("Update", FilterOn: true, Images: Images.PreAndPost),`.
   Then regenerate:

   ```bash
   dotnet run --project tools/codegen/Vela.StepApiGen
   ```

   Do not edit the generated files by hand. The next run overwrites them, and CI runs the generator with
   `-- --check`, which fails when the committed files differ from its output.
4. **`src/Vela.Plugins/Plugin/PluginBaseOfTEntity.cs`.** Add a case to `BuildTypedContext`, keyed by the step's
   request name, that builds `<Message>PostContext` for a PostOperation step and `<Message>PreContext` otherwise.
   The runtime then hands the handler the stage-correct typed context.
5. **`src/Vela.Plugins/Constants/PluginMessages.cs`.** Add the message-name constant (the request name) if it does
   not exist yet.
6. **`vela add` needs no change.** `AddCommand.KnownMessages` is the generated `TypedTriggers.Messages`, so
   `vela add plugin --message <Message>` renders the typed `On<Message>()` call as soon as the generator has run.
   Add the message to the `Add_Plugin_KnownMessage_RendersTypedOnCall` or
   `ResolveKnownMessage_maps_an_sdkmessage_to_its_typed_trigger` test cases.

## Verify

- **Framework:** run `dotnet build Vela.sln` and the plugin tests. The `net462` suites run under Mono outside
  Windows, and the CLI suite runs with `dotnet test` (see [Build and test](index.md#build-and-test)).
- **CLI wiring:** assert the generated call in a `vela add` test (see `Add_Plugin_KnownMessage_RendersTypedOnCall`),
  and compile a generated plugin end to end with the build smoke test. Clear the stale packed framework from the
  NuGet cache first (see [Run the build smoke test](index.md#run-the-build-smoke-test)).

## Messages that are deliberately not modelled

`SetState` and `SetStateDynamicEntity` are deprecated (see
[SetStateRequest](https://learn.microsoft.com/dotnet/api/microsoft.crm.sdk.messages.setstaterequest) on Microsoft
Learn). They get no typed trigger: they are not in `KnownMessages` and have no `I<Message>PreContext` or
`I<Message>PostContext`, so `vela add` renders `On<SetStateRequest>()` for them. Change `statecode` and `statuscode`
through an `Update` instead, and do not add typed support for these messages. `Assign` is modelled: use
`OnAssign()`.
