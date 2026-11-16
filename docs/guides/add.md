# Add a component (`vela add`)

`vela add` generates a **single** plugin, service, Custom API or test file into an existing project, or adds a step to
an existing plugin, so you do not write the boilerplate by hand. Use it in a solution scaffolded by
[`vela init`](scaffold.md) or in any project that references `Vela.Plugins`. Namespaces are inferred from the target
project's `RootNamespace`, and files follow the same by-table layout as `vela init`, so they drop straight into a
scaffolded solution.

```powershell
# run inside the project folder, or pass --project <csproj or its folder>
vela add plugin AccountValidatorPlugin --entity Account --message Update --filtering-attributes Name
vela add plugin --entity Contact --message Delete                 # named ContactPlugin
vela add service AccountFollowUpService --entity Account          # table-scoped interface and implementation
vela add service AuditService                                     # shared by several tables, under Services/
vela add customapi RecalculateTotals --prefix zava                # global (unbound), lightweight Custom API
vela add customapi ExportContact --entity Contact --prefix zava   # typed Custom API bound to a table

# run inside the test project (RootNamespace ending in .Tests)
vela add test AccountValidatorPlugin --entity Account             # test for a plugin
vela add test RecalculateTotals --kind customapi                  # test for a global Custom API

# add a step to an existing multi-step plugin (edits the file in place)
vela add step AccountRouterPlugin --entity Account --message Update --filtering-attributes Name

# or run the guided wizard (prompts for the kind, name, entity and options)
vela add --interactive
```

## Common options

- **`--entity <ModelType>`** (or `-e`) is the early-bound class name (`Account`) or the table logical name
  (`account`). It sets the `EntityPluginBase<T>` type and the `<Table>/` folder. A logical name, or a class name in
  the wrong case, is resolved to the generated model's class through its `EntityLogicalNameValue`, with a note, so
  `--entity account` writes `Account/Plugins/AccountPlugin.cs` typed on `Account`. When no generated model matches, a
  value with no upper-case letter is refused (exit `2`): run [`vela models`](models.md) first, or pass the class
  name. A class name is used as given. `--entity` is required for a plugin and a step, and optional for a service
  (without it, a service shared by several tables) and a Custom API (without it, a global one).
- **`--project <csproj or its folder>`** (or `-p`) names the target project. Default: the single `.csproj` in the
  current folder.
- **`--force`** overwrites an existing file (not accepted by `add step`). **`--json`** prints a machine-readable
  report.
- **Each kind takes only its own options.** An option the kind does not use (for example `add service --message`
  or `add step --force`) is rejected with a message rather than silently ignored. `vela add --help` lists each
  kind's options.

## Plugin (`add plugin`)

- **Name.** `<Name>` is optional and defaults to `<Entity>Plugin` (`--entity Account` writes
  `Account/Plugins/AccountPlugin.cs`). Every other kind requires its name.
- **An existing plugin file is never edited.** When `<Table>/Plugins/<Name>.cs` exists and `--force` is not passed,
  `add plugin` writes nothing, exits `2` and prints the `vela add step <Name> --entity <Entity> --message ...`
  command (with the `--handler`, `--stage`, `--mode`, `--filtering-attributes` and `--project` you passed) that adds
  the requested step to that class. `--force` overwrites the file with a new one-step plugin instead.
- **Message.** `--message <Message>` defaults to `Create`. The messages Vela models get a typed `On...()` call: the
  CRUD and bulk set (`Create`, `Update`, `CreateMultiple`, `UpdateMultiple`, `Delete`, `Retrieve`,
  `RetrieveMultiple`), relationships and sharing (`Associate`, `Disassociate`, `Merge`, `GrantAccess`,
  `ModifyAccess`, `RevokeAccess`, `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`), the sales
  lifecycle (`QualifyLead`, `WinOpportunity`, `LoseOpportunity`, `WinQuote`, `ConvertSalesOrderToInvoice`,
  `GenerateQuoteFromOpportunity`), queues (`AddToQueue`, `RouteTo` and others), email (`SendEmail`,
  `SendEmailFromTemplate` and others), team and list membership, and case and contract messages (see the full list
  in [Write a plugin](plugins.md)). `Assign` gets the typed `OnAssign()` too. The sdkmessage name works as well:
  `--message Send` renders `OnSendEmail()`, and `--message Win` renders `OnWinQuote()` or `OnWinOpportunity()`
  depending on `--entity`.
- **Other messages** are registered through their request type, `On<XxxRequest>()`: the SDK class, fully
  qualified, when one ships, otherwise the request that [`vela models --messages <name>`](models.md) (or
  `--custom-apis <name>`) generates, with a note to generate it. For example, `--message SetState` renders
  `On<Microsoft.Crm.Sdk.Messages.SetStateRequest>()`, which registers on `SetStateDynamicEntity`, the message a
  `SetStateRequest` fires. That request is deprecated and an Update of `statecode` covers both, so for a state change
  prefer `--message Update` with `--filtering-attributes StateCode`.
- **Handler.** Whatever the message, the plugin is `partial` and its handler takes its generated `<Handler>Context`
  (see [Typed step context](plugins.md#typed-step-context-read-images-as-ctxpreimage--ctxpostimage)), so its
  signature stays the same when the step later captures an image. The handler is `Handle<Message>`
  (`HandleUpdate(HandleUpdateContext ctx)`), the same default as `add step`. `--handler <Method>` names it; a value
  that is not a valid C# method name fails the command, which writes nothing.
- **Filtering attributes.** A `Create` or `Update` step (also `CreateMultiple` and `UpdateMultiple`) requires a
  `FilterOn`, so `--filtering-attributes <Col1,Col2>` (or `--filter`) is **required** for it. Pass the early-bound
  property names: `--filtering-attributes Name,Telephone1` renders `FilterOn(e => e.Name, e => e.Telephone1)`.
  `add` never guesses a column from the model: without the option it fails with a message naming it, and writes
  nothing. On any other message the option is rejected, because those steps have no `FilterOn`. The wizard asks for
  the columns.
  Property names are **case-sensitive** (`Name`, not `name`). When the generated model is available (found under the
  solution root, the nearest folder above the project that holds a `.sln` or `.slnx`), `add` maps a column logical
  name to its property through the model's `[AttributeLogicalName]` (`telephone1` becomes `Telephone1`), corrects a
  value that differs only in case, and prints a note, so the emitted `FilterOn` compiles. A name that matches no
  property is kept as given, with a warning. Without a model, an all-lower-case value is refused, because nothing
  can map it and `e => e.name` would not compile: run `vela models` first or pass the property name.
- **Stage and mode.** `--stage <PreValidation|PreOperation|PostOperation>` and `--mode <Sync|Async>` set the step's
  stage and execution mode. Both are optional and default to `PostOperation` and `Sync`. The handler's generated
  context follows the stage: at a pre-stage it derives from the pre-stage context (`<Message>PreContext<T>`, no
  `ctx.Response`), at PostOperation from `<Message>PostContext<T>`. Dataverse allows asynchronous steps only at
  PostOperation, so `--mode Async` with a pre-stage is rejected with a message.

## Step (`add step`)

`vela add step <PluginName>` edits an existing **handler-based (multi-step)** plugin in place. It appends a
`RegisterStep().On<Message>()...Handle(Handle<Message>)` registration, where `On<Message>()` is the typed method
(`OnUpdate()`) or `On<XxxRequest>()` for another message, as for `add plugin`, and a matching handler method that
takes its generated `<Handler>Context`.

- It accepts the same `--message`, `--handler`, `--stage`, `--mode` and `--filtering-attributes` options as
  `add plugin`, with the same rules: `--filtering-attributes` is required on a `Create` or `Update` (or `*Multiple`)
  step and rejected on any other message, and its values are validated and corrected against the model.
- The context is nested in the plugin class, so `add step` adds the `partial` modifier to the class (and to any type
  enclosing it) when it is missing. The existing handlers keep their parameter types.
- It refuses a plugin that has no handler-bound step yet (add a step ending in `.Handle(...)` first) and any file it
  cannot parse cleanly, so it never corrupts source.
- A plugin may have several steps on one trigger (message, stage and mode) when their `FilterOn` columns, images,
  `WithStepKey` or `ForRelationship` tell them apart. `add step` refuses only an exact duplicate: an existing step on
  the same trigger with the same filtering columns and nothing else to tell them apart (the defaults `Create`,
  `PostOperation`, `Sync` are what `add plugin` emits). A step that shares a trigger but filters on other columns is
  added, with a note suggesting `.WithStepKey("...")` to name both.
- The handler name must be new in the file: a second step on a message whose `Handle<Message>` already exists needs
  `--handler <Method>`.
- The handler body's TODO points at the typed access for the message: `ctx.Request.Target`, or
  `ctx.Request.Targets` for a `*Multiple` step.

## Service (`add service`)

`vela add service <Name> --entity <ModelType>` writes a table-scoped `I<Name>` interface and a sealed implementation
that injects `IOrganizationService`, under `<Table>/Services/`, typed on the table's early-bound class. Without
`--entity` the service is shared by several tables: it goes under `Services/` and takes the late-bound `Entity`.

The command prints the `services.AddScoped<...>(...)` line to add to your plugin's `ConfigureServices(...)` (see
[Dependency injection](dependency-injection.md)). With `--json`, that line is returned in a `registration` field
(omitted for every other kind), so an agent driving the CLI can wire the service up without parsing the text.

## Custom API (`add customapi`)

`vela add customapi <Name> --prefix <publisherprefix>` writes a Custom API. Without `--entity` it is a global
(unbound), lightweight Custom API with a handler method; with `--entity` it is a typed Custom API bound to the table,
with its request and response classes. See [Write a Custom API](custom-apis.md).

- **Prefix.** The unique name is `<prefix>_<Name>`, where `<prefix>` must match the publisher prefix of your target
  solution, or `apply --solution` rejects it. `--prefix` is **required** (`--prefix dv` renders `dv_<Name>`): `add`
  works offline, so it does not resolve a solution, and it never guesses the prefix from the project prefix or reads
  it from the project. The value is the `publisherPrefix` recorded in [`PROJECT.md`](scaffold.md#projectmd), and
  `vela solution show <solution> --json` prints a solution's prefix. Without `--prefix` the command exits `2` and
  writes nothing. When the project still has the `<VelaPublisherPrefix>` property an older `vela init` wrote, the
  message quotes its value (`pass --prefix zava`) but does not use it. The wizard asks for the prefix. A value that
  is not a valid publisher prefix (2-8 characters, letter first, alphanumeric) is refused rather than written into a
  name Dataverse would reject. See [The publisher prefix](custom-apis.md#the-publisher-prefix).
- **Operation type.** `--function` or `--action` chooses it (default: action). `--function` adds a placeholder
  `Result` response, because a function must return a value, and sets it in the handler.
- **Binding.** `--bound` and `--global` make the binding explicit instead of inferring it from `--entity`. `--bound`
  requires `--entity`; `--global` is unbound.

## Test (`add test`)

`vela add test <ClassUnderTest>` writes a `Vela.Testing` test. Run it inside the test project, whose `RootNamespace`
must end in `.Tests`. It reads the class under test from the source of the projects the test project references (or
else the solution) and fails when it finds none, so the test names the real class, namespace, steps and unique name.
See [Testing with Vela.Testing](testing.md).

- **Plugin test** (the default, `<Plugin>Tests`). It drives the plugin through `PluginPipeline`. For each step it
  covers (the steps on the message `--message` names, else every step the plugin registers; `--entity` defaults to
  the plugin's `EntityPluginBase<T>`), it writes two tests named after the step's message and stage:
  `Fires_On_<Message>_<Stage>` asserts from the `ExecutionLog` that the step fired, whatever the handler does, and
  the skipped `Does_What_It_Should_On_<Message>_<Stage>` is the skeleton for asserting what the plugin does. A second
  step on the same message and stage gets `_2`, and so on, in the order the plugin registers them.
  The message is sent the way a caller sends it: `Create` and `Update` (and `*Multiple`, with `Targets`) with an
  entity that sets the step's `FilterOn` columns; `Delete`, `Retrieve`, `RetrieveMultiple` and `Assign` on a seeded
  record, with typed reads (`pipeline.Service.Retrieve<T>(id, x => ...)` and `Query.For<T>()`) that select only the
  step's filtering columns, or else the primary key; `Associate` and `Disassociate` over a placeholder many-to-many
  relationship, with a TODO to replace it with yours. Any other message sends the step's request with a TODO for its
  inputs, in a test skipped until they are set.
- **Completing an existing test class.** When `<Plugin>Tests.cs` already exists, `vela add test` **completes** the
  class instead of failing. It adds the two tests of every covered step that has no `Fires_On_<Message>_<Stage>`
  method yet (a `Fires_On_<Message>` method, the name earlier versions generated, counts for the first step on that
  message) before the class's closing brace, plus any `using` the new tests need. Nothing else in the file changes, so
  your edits are kept. Add a step with `vela add step`, run `vela add test` again (with or without its `--message`),
  and the new step has its tests, even when an earlier step is on the same message. When every step is tested
  already, it says so and exits `0`. A file whose class it cannot find, or whose braces do not balance, is left alone
  and the command exits `2`. `--force` still overwrites the file with a new class.
- **Custom API test** (`--kind customapi`, `<Class>Tests`). It finds the class named `<Name>` or `<Name>CustomApi`,
  or the one registering `<prefix>_<Name>`, and takes its unique name and request type from the source. It checks
  the registered unique name and that every declared output parameter is set, with a `WithInputParameter(...)`
  example for the inputs. `--prefix` is needed only when the unique name cannot be read from the source: the test
  then asserts `<prefix>_<Name>`, and without `--prefix` the command exits `2`. When the test class exists already
  it has these tests, so there is nothing to add: the command says so and exits `0` (`2` when the file has no such
  class).

## Client-side scripts (preview)

- `vela add form-script <entity>` scaffolds a client-side form script (`src/<entity>/Forms/main.ts` and a
  co-located `main.test.ts`) in the [web resources project](client-side.md) and registers its entry in
  `web-resources.entries.ts`. Run it inside the `{prefix}.WebResources` folder (or pass `--project <dir>`). It
  refuses to overwrite an existing file (use `--force`), and the `<entity>` is the table logical name.
- `vela add ribbon-script <entity>` scaffolds a [ribbon command](client-side.md) module
  (`src/<entity>/Ribbon/commands.ts` and a co-located test) in the web resources project and registers its entry.
  It prints the `<Prefix>.Ribbon.<entity>` namespace the maker points the button at in the Command Designer. The
  entity needs a generated Main form model (run `vela models --forms --entities <entity>` first). It follows the
  same conventions as `form-script` (`--project`, `--force`).

## See also

- [Write a plugin](plugins.md) and [Write a Custom API](custom-apis.md): fill in the generated file.
- [Testing with Vela.Testing](testing.md): what the generated `PluginPipeline` test runs.
