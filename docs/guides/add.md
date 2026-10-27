# Add a plugin, service, Custom API, test or step (vela add)

Once a solution exists, generate a **single** plugin, service, Custom API or test file into a project (or add a
step to an existing plugin) without writing the boilerplate by hand. Namespaces are inferred from the target project's `RootNamespace`, and files
follow the same by-table layout as `vela init` (so they drop straight into a scaffolded solution):

```powershell
# run inside the project folder, or pass --project <csproj>
vela add plugin AccountValidatorPlugin --entity Account --message Update --filtering-attributes Name
vela add plugin --entity Contact --message Delete           # named ContactPlugin
vela add service AccountFollowUpService --entity Account  # table-scoped interface + implementation
vela add service AuditService                             # shared by several tables, under Services/
vela add customapi RecalculateTotals --prefix zava                # global (unbound), lightweight Custom API
vela add customapi ExportContact --entity Contact --prefix zava   # typed Custom API bound to a table

# run inside the test project (RootNamespace ending in .Tests)
vela add test AccountValidatorPlugin --entity Account            # test for a plugin
vela add test RecalculateTotals --kind customapi                # test for a global Custom API

# add a step to an existing multi-step plugin (edits the file in place)
vela add step AccountRouterPlugin --entity Account --message Update --filtering-attributes Name

# or run the guided wizard (prompts for kind/name/entity/options)
vela add --interactive
```

- `--entity <ModelType>` is the early-bound class name (e.g. `Account`); it sets the `EntityPluginBase<T>`
  type and the `<Table>/` folder. Required for a plugin and a service; omit on a Custom API to generate a global one.
- **Plugin name** (`add plugin`): `<Name>` is optional and defaults to `<Entity>Plugin` (`--entity Account` writes
  `Account/Plugins/AccountPlugin.cs`). Every other kind requires its name.
- **An existing plugin file is never edited by `add plugin`.** When `<Table>/Plugins/<Name>.cs` exists and `--force`
  is not passed, it writes nothing, exits `2` and prints the `vela add step <Name> --entity <Entity> --message ...`
  command (with the `--handler`, `--stage`, `--mode`, `--filtering-attributes` and `--project` you passed) that adds
  the requested step to that class. `--force` overwrites the file with a new one-step plugin instead.
- **Custom API ergonomics:** `--function`/`--action` choose the operation type (default `Action`), and
  `--bound`/`--global` make the binding explicit instead of inferring it from `--entity` (`--bound` requires
  `--entity`; `--global` is unbound). The generated unique name is `<prefix>_<Name>`, where `<prefix>` must match
  your target solution's **publisher prefix** or `apply --solution` rejects it. `--prefix <publisherprefix>` is
  **required** (for example `--prefix dv` renders `dv_<Name>`): `add` stays offline, so it does not resolve a
  solution, and it never guesses the prefix from the project prefix or reads it from the project. The value is the
  `publisherPrefix` of the solution's [`PROJECT.md`](scaffold.md#projectmd); `vela solution show <solution> --json`
  prints a solution's prefix. Without `--prefix` the command exits `2` and writes nothing; when the project still has
  the `<VelaPublisherPrefix>` an earlier `vela init` wrote, the message quotes its value (`pass --prefix zava`) but
  does not use it. The wizard (`vela add --interactive`) asks for it. A value that is not a valid publisher prefix
  (2-8 characters, letter first, alphanumeric) is refused rather than written into a name Dataverse would reject.
  See [the publisher prefix](custom-apis.md#the-publisher-prefix).
  `--function` adds a placeholder `Result` response (a function must return a value) and sets it in the handler.
- `--message <Message>` (plugin, default `Create`): the messages Vela models get a strongly-typed `On...()`
  call - the CRUD/bulk set (`Create`, `Update`, `CreateMultiple`, `UpdateMultiple`, `Delete`, `Retrieve`,
  `RetrieveMultiple`), relationships/sharing (`Associate`, `Disassociate`, `Merge`, `GrantAccess`, `ModifyAccess`,
  `RevokeAccess`, `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`), sales lifecycle (`QualifyLead`,
  `WinOpportunity`, `LoseOpportunity`, `WinQuote`, `ConvertSalesOrderToInvoice`, `GenerateQuoteFromOpportunity`),
  queues (`AddToQueue`, `RouteTo`, …), email (`SendEmail`, `SendEmailFromTemplate`, …), team/list membership and
  case/contract messages (see the full list in [Write a plugin](plugins.md)). The sdkmessage name works too:
  `--message Send` renders `OnSendEmail()`, and `--message Win` renders `OnWinQuote()` or `OnWinOpportunity()`
  from `--entity`. `Assign` gets the typed `OnAssign()` too. Any other name is registered through its request type,
  `On<XxxRequest>()`: the SDK class, fully qualified, when one ships (`--message SetState` renders
  `On<Microsoft.Crm.Sdk.Messages.SetStateRequest>()`, which registers on `SetStateDynamicEntity`, the message a
  `SetStateRequest` fires; that request is deprecated, and an Update of `statecode` covers both, so for a state change prefer
  `--message Update` with `--filtering-attributes statecode`), otherwise the request [`vela models --messages <name>`](models.md) (or `--custom-apis <name>`)
  generates, with a note to generate it. Whatever the message, the plugin is `partial` and its handler takes its
  generated `<Handler>Context` (see [Typed step context](plugins.md#typed-step-context-read-images-as-ctxpreimage--ctxpostimage)),
  so its signature stays the same when the step later captures an image. The handler is `Handle<Message>`
  (`HandleUpdate(HandleUpdateContext ctx)`), the same default as `add step`; `--handler <Method>` names it
  (a valid C# method name, else the command fails and writes nothing). `--project <csproj>` (default:
  the single `.csproj` in the current directory), `--force` (overwrite an existing file), `--json` (machine-readable
  report).
- **Each kind takes only its own options.** An option the kind does not use (for example `add service --message`
  or `add step --force`) is rejected with a message rather than silently ignored. `vela add --help` lists each
  kind's options.
- **Filtering attributes on a `Create`/`Update` plugin or step** (also `CreateMultiple`/`UpdateMultiple`):
  these steps require a `FilterOn`, so `--filtering-attributes <Col1,Col2>` is **required** for them. Pass the
  early-bound property name(s) (for example `--filtering-attributes Name,Telephone1` renders
  `FilterOn(e => e.Name, e => e.Telephone1)`). `add` never guesses a column from the model: without the option it
  fails with a message naming it, and writes nothing. On any other message the option is rejected (those steps
  have no `FilterOn`). The interactive wizard asks for the columns.
  Property names are **case-sensitive** (`Name`, not `name`). When the generated model is available (found under
  the solution root, the nearest folder above the project holding a `.sln` or `.slnx`), `add` maps a column logical
  name to its property through the model's `[AttributeLogicalName]` (`telephone1` becomes `Telephone1`), corrects a
  value that differs only in case, and prints a note, so the emitted `FilterOn` compiles; a name that matches no
  property is left as-is with a warning. Without a model, an all-lower-case value is refused (nothing can map it,
  and `e => e.name` would not compile): run `vela models` first or pass the property name.
- **Stage and mode** (`add plugin` and `add step`): `--stage <PreValidation|PreOperation|PostOperation>` and
  `--mode <Sync|Async>` set the step's stage and execution mode. Both are optional and default to
  `PostOperation` + `Sync`. The handler's generated context follows the stage: at a pre-stage it derives from the
  pre-stage context (`<Message>PreContext<T>`, no `ctx.Response`), at PostOperation from `<Message>PostContext<T>`.
  Dataverse only allows asynchronous steps at
  `PostOperation`, so `--mode Async` with a pre-stage is rejected with a clear message.
- `vela add service` scaffolds a table-scoped `I<Name>` interface + sealed implementation (injecting
  `IOrganizationService`) under `<Table>/Services/`, typed on the table's early-bound class. Without `--entity` the
  service is shared by several tables: it goes under `Services/` and takes the late-bound `Entity`. It surfaces the `services.AddScoped<...>(...)` line to drop
  into your plugin's `ConfigureServices(...)`. With `--json` that line is returned in a structured `registration`
  field (omitted for every other `add` kind), so an agent driving the CLI can wire the service up without parsing
  the human prose.
- `vela add test <ClassUnderTest>` scaffolds a `Vela.Testing` test. Run it inside the test project (its
  `RootNamespace` must end in `.Tests`). It reads the class under test from the source of the projects the test
  project references (else the solution), and fails when it finds none, so the test names the real class,
  namespace, steps and unique name.
  - A **plugin** test (`<Plugin>Tests`) drives the plugin through `PluginPipeline`. For each step it covers (the
    one `--message` names, else every step the plugin registers; `--entity` defaults to the plugin's
    `EntityPluginBase<T>`) it writes two tests named after the step's message and stage:
    `Fires_On_<Message>_<Stage>` asserts from the `ExecutionLog` that the step fired, whatever the handler does, and
    the skipped `Does_What_It_Should_On_<Message>_<Stage>` is the skeleton for asserting what the plugin does. A
    second step on the same message and stage gets `_2`, and so on, in the order the plugin registers them. The
    message is sent the way a caller sends it: `Create`/`Update` (and `*Multiple`, with `Targets`) with an entity
    that sets the step's `FilterOn` column(s); `Delete`, `Retrieve`, `RetrieveMultiple` and `Assign` on a seeded
    record; `Associate`/`Disassociate` over a placeholder many-to-many relationship (a TODO to replace with yours).
    Any other message sends the step's request with a TODO for its inputs, in a test skipped until they are set.
  - When `<Plugin>Tests.cs` already exists, `vela add test` **completes** the class instead of failing: it adds the
    two tests of every covered step that has no `Fires_On_<Message>_<Stage>` method yet (a `Fires_On_<Message>`
    method, the name earlier versions generated, counts for the first step on that message), before the class's
    closing brace, plus any `using` the new tests need. Nothing else in the file changes, so your edits are kept.
    Add a step with `vela add step`, rerun `vela add test`, and the new step has its tests. When every step is
    tested already, it says so and exits `0`. A file whose class it cannot find (or whose braces do not balance)
    is left alone and the command exits `2`. `--force` still overwrites the file with a fresh class.
  - A **Custom API** test (`--kind customapi`, `<Class>Tests`) finds the class named `<Name>` or
    `<Name>CustomApi`, or the one registering `<prefix>_<Name>`, and takes its unique name and request type from
    the source. It checks the registered unique name and that every declared output parameter is set, with a
    `WithInputParameter(...)` example for the inputs. `--prefix` is needed only when the unique name cannot
    be read from the source: the test then asserts `<prefix>_<Name>`, and without `--prefix` the command exits `2`. When the test class exists already it has these tests, so there is nothing to
    add: the command says so and exits `0` (`2` when the file has no such class).
- `vela add step <PluginName>` edits an existing **handler-based (multi-step)** plugin in place, appending a
  `RegisterStep().On<Message>()…Handle(Handle<Message>)` registration (where `On<Message>()` is the curated
  method, e.g. `OnUpdate()`, or `On<XxxRequest>()` for an unmodelled message, as for `add plugin`) and a matching
  handler method taking its generated `<Handler>Context`
  (`--message`, optional `--handler <Method>`). The context is nested in the plugin class, so `add step` adds the
  `partial` modifier to the class (and to any type enclosing it) when it is missing; the existing handlers keep
  their parameter types. It refuses a plugin that has no handler-bound step yet (add a
  step ending in `.Handle(...)` first) and any file it can't parse cleanly, so it never corrupts source. It
  accepts the same `--stage`/`--mode` and `--filtering-attributes` options as `add plugin`: pass
  `--filtering-attributes <Col1,Col2>` to emit `FilterOn(e => e.Col1, e => e.Col2)` (identifiers validated and
  wrong casing corrected against the model, as above). As for `add plugin`, the option is required on a
  `Create`/`Update` (or `*Multiple`) step and rejected on any other message. A plugin may have several
  steps on one trigger (message, stage and mode) when they are told apart by their `FilterOn` columns, images,
  `WithStepKey` or `ForRelationship`. `add step` refuses only an exact duplicate - an existing step on the same
  trigger with the same filtering columns and nothing else to tell them apart (the add-step defaults `Create`,
  `PostOperation`, `Sync` are what `add plugin` emits). A step that shares a trigger but filters on other columns
  is added, with a note suggesting `.WithStepKey("...")` to name both. The handler body's TODO
  points at the typed access for the message (`ctx.Request.Target`, or `ctx.Request.Targets` for a `*Multiple`
  step).
- `vela add form-script <entity>` (preview) scaffolds a client-side form script (`src/<entity>/Forms/main.ts` +
  a co-located `main.test.ts`) in the [web-resources project](client-side.md) and registers its entry in
  `web-resources.entries.ts`. Run it inside the `{prefix}.WebResources` folder (or pass `--project <dir>`); it
  refuses to overwrite an existing file (use `--force`), and the `<entity>` is the table logical name.
- `vela add ribbon-script <entity>` (preview) scaffolds a [ribbon command](client-side.md) module
  (`src/<entity>/Ribbon/commands.ts` + a co-located test) in the web-resources project and registers its entry.
  It prints the `<Prefix>.Ribbon.<entity>` namespace the maker points the button at in the Command Designer. The
  entity needs a generated Main form model (run `vela models --forms --entities <entity>` first). Same
  conventions as `form-script` (`--project`, `--force`).

## See also

- [Write a plugin](plugins.md) / [Write a Custom API](custom-apis.md) - flesh out the generated file.
- [Testing with Vela.Testing](testing.md) - the generated test scaffolds a no-connection test.
