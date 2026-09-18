# Add a plugin, service, Custom API, test or step (vela add)

Once a solution exists, generate a **single** plugin, service, Custom API or test file into a project (or add a
step to an existing plugin) without writing the boilerplate by hand. Namespaces are inferred from the target project's `RootNamespace`, and files
follow the same by-table layout as `vela init` (so they drop straight into a scaffolded solution):

```powershell
# run inside the project folder, or pass --project <csproj>
vela add plugin AccountValidatorPlugin --entity Account --message Update
vela add service AccountFollowUpService --entity Account  # table-scoped interface + implementation
vela add customapi RecalculateTotals                  # global (unbound), lightweight Custom API
vela add customapi ExportContact --entity Contact     # typed Custom API bound to a table

# run inside the test project (RootNamespace ending in .Tests)
vela add test AccountValidatorPlugin --entity Account            # test for a plugin
vela add test RecalculateTotals --kind customapi                # test for a global Custom API

# add a step to an existing multi-step plugin (edits the file in place)
vela add step AccountRouterPlugin --entity Account --message Update

# or run the guided wizard (prompts for kind/name/entity/options)
vela add --interactive
```

- `--entity <ModelType>` is the early-bound class name (e.g. `Account`); it sets the `EntityPluginBase<T>`
  type and the `<Table>/` folder. Required for a plugin and a service; omit on a Custom API to generate a global one.
- **Custom API ergonomics:** `--function`/`--action` choose the operation type (default `Action`), and
  `--bound`/`--global` make the binding explicit instead of inferring it from `--entity` (`--bound` requires
  `--entity`; `--global` is unbound). The generated unique name is `<prefix>_<Name>`; `<prefix>` defaults to a
  lower-cased form of the project prefix, but that must match your target solution's **publisher prefix** or
  `apply --solution` rejects it. Override it with `--prefix <publisherprefix>` (for example `--prefix dv` renders
  `dv_<Name>`). See [the publisher prefix](custom-apis.md#the-publisher-prefix).
- `--message <Message>` (plugin, default `Create`): the messages Vela models get a strongly-typed `On...()`
  call - the CRUD/bulk set (`Create`, `Update`, `CreateMultiple`, `UpdateMultiple`, `Delete`, `Retrieve`,
  `RetrieveMultiple`), relationships/sharing (`Associate`, `Disassociate`, `Merge`, `GrantAccess`, `ModifyAccess`,
  `RevokeAccess`, `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`), sales lifecycle (`QualifyLead`,
  `WinOpportunity`, `LoseOpportunity`, the `Convert*`/`Generate*` messages), queues (`AddToQueue`, `RouteTo`, …),
  email (`SendEmail`, `SendEmailFromTemplate`, …), team/list membership and case/contract messages (see the full
  list in [Write a plugin](plugins.md)); any other name is
  registered as a custom SDK message via `OnMessage("...")`. The deprecated `SetState` / `SetStateDynamicEntity`
  and `Assign` messages have no typed method (update `statecode`/`statuscode` or `ownerid` with `Update`
  instead), so they too render `OnMessage("...")`. `--project <csproj>` (default: the single `.csproj` in the
  current directory), `--force` (overwrite an existing file).
- **Filtering attributes on a `Create`/`Update` plugin** (also `CreateMultiple`/`UpdateMultiple`): these
  messages require at least one filtering attribute. Pass `--filtering-attributes <Col1,Col2>` with the
  early-bound property name(s) that should trigger the step (for example `--filtering-attributes Name,Telephone1`
  renders `WithFiltering(e => e.Name, e => e.Telephone1)`). If you omit it, `add` auto-selects a sensible
  default column from the entity's generated early-bound model (its primary-name column) and marks it with an
  `auto-selected` comment so you can adjust it. The generated file always compiles and applies; if no column can
  be inferred (no generated model found), `add` fails with a clear message asking for `--filtering-attributes`
  rather than emitting code that would not compile. Property names are **case-sensitive** (`Name`, not `name`):
  when the model is available, `add` warns and suggests the correct casing if a value does not match a real
  column, so a typo surfaces at generation time instead of as a later build error.
- `vela add service` scaffolds a table-scoped `I<Name>` interface + sealed implementation (injecting
  `IOrganizationService`) under `<Table>/Services/`, and prints the `services.AddScoped<...>(...)` line to drop
  into your plugin's `ConfigureServices(...)`.
- `vela add test <ClassUnderTest>` scaffolds a `Vela.Testing` test (`<ClassUnderTest>Tests`) pre-wired with
  `PluginContext.For<…>()…Execute(…)`. Run it inside the test project (its `RootNamespace` must end in `.Tests`);
  use `--kind customapi` for a Custom API test (and omit `--entity` for a global one).
- `vela add step <PluginName>` edits an existing **handler-based (multi-step)** plugin in place, appending a
  `RegisterStep().On<Message>()…Handle(Handle<Message>)` registration (where `On<Message>()` is the curated
  method, e.g. `OnUpdate()`, or `OnMessage("...")` for an unmodelled message, not the generic `On<TRequest>()`
  overload) and a matching handler method
  (`--message`, optional `--handler <Method>`). It refuses single-step plugins (those using the `Execute(...)`
  override) and any file it can't parse cleanly, so it never corrupts source.
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
