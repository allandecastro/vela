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
  `--entity`; `--global` is unbound).
- `--message <Message>` (plugin, default `Create`): `Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple`/
  `Associate`/`Disassociate`/`SetState` get a strongly-typed `On...()` call; any other name is registered as a
  custom SDK message via `OnMessage("...")`. `--project <csproj>` (default: the single `.csproj` in the current
  directory), `--force` (overwrite an existing file).
- `vela add service` scaffolds a table-scoped `I<Name>` interface + sealed implementation (injecting
  `IOrganizationService`) under `<Table>/Services/`, and prints the `services.AddScoped<...>(...)` line to drop
  into your plugin's `ConfigureServices(...)`.
- `vela add test <ClassUnderTest>` scaffolds a `Vela.Testing` test (`<ClassUnderTest>Tests`) pre-wired with
  `PluginContext.For<…>()…Execute(…)`. Run it inside the test project (its `RootNamespace` must end in `.Tests`);
  use `--kind customapi` for a Custom API test (and omit `--entity` for a global one).
- `vela add step <PluginName>` edits an existing **handler-based (multi-step)** plugin in place, appending a
  `RegisterSteps().On<Message>()…Handler(nameof(Handle<Message>))` registration and a matching handler method
  (`--message`, optional `--handler <Method>`). It refuses single-step plugins (those using the `Execute(...)`
  override) and any file it can't parse cleanly, so it never corrupts source.

## See also

- [Write a plugin](plugins.md) / [Write a Custom API](custom-apis.md) - flesh out the generated file.
- [Testing with Vela.Testing](testing.md) - the generated test scaffolds a no-connection test.
