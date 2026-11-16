---
_description: "What vela init scaffolds and where to edit"
---

# Initialize a solution

This page summarizes what `vela init` gives you and what to do next. Read it after the [Quickstart](quickstart.md),
or before you scaffold your first real solution. The full layout, every flag and regeneration with `--force` are in
[Scaffold a solution (`vela init`)](../guides/scaffold.md).

```powershell
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --solution ZavaSales --output .
# or run the wizard:
vela init --interactive
```

## What you get

A **minimal** solution in `ZavaSales/` that builds, tests and plans unedited, and deploys nothing until you add a
plugin:

- `src/Zava.Shared`: the project for early-bound models and shared services, empty until `vela models`.
- `src/Zava.Plugins`: the plugin package project. `dotnet build -c Release` packs it into
  `artifacts/zava_Zava.Plugins.1.0.0.nupkg`. It starts with `ProjectServices.cs` (assembly-wide services and message
  sources, see [Localized messages](../guides/localization.md)) and `Messages.cs` (the message keys), both empty.
- `tests/Zava.Plugins.Tests`: an xUnit project with `Vela.Testing`, which runs plugins with no connection.
- `PROJECT.md`: the publisher prefix and the solution of each package, for the people and agents who run the
  commands. The CLI never reads it: pass the values as flags (`--solution` on `vela plan` and `vela apply`,
  `--prefix` on `vela add customapi`). See [PROJECT.md](../guides/scaffold.md#projectmd).
- `AGENTS.md`: instructions for AI coding agents on writing steps, Custom APIs and tests in this solution.

See [The generated layout](../guides/scaffold.md#the-generated-layout) for every file.

## Fill it in

```powershell
vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated --interactive
vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins
vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests
```

`vela models` generates the early-bound models from your environment ([Early-bound models](../guides/models.md)).
`vela add plugin` adds a Create PostOperation step with a `HandleCreate` handler, and `vela add test` its
`PluginPipeline` test ([Add a component (`vela add`)](../guides/add.md)).

Namespaces follow folders, so the plugin is in `Zava.Plugins.Account.Plugins`. Keep the `using` directives inside a
block namespace, or `Account` resolves to a namespace (CS0118). See
[Namespaces and usings](../guides/scaffold.md#namespaces-and-usings).

> [!TIP]
> To see worked examples first, add **`--samples`**: sample models, services, single-step and multi-step plugins,
> bound and global Custom APIs with their tests, and the Integrations and ConsoleApp projects. See
> [Samples](../guides/scaffold.md#samples---samples).

## Next steps

- **Write your own code**: [plugins](../guides/plugins.md), [Custom APIs](../guides/custom-apis.md),
  [dependency injection](../guides/dependency-injection.md), [caching](../guides/caching.md).
- **Test without a connection**: [Testing with Vela.Testing](../guides/testing.md).
- **Ship**: build with `dotnet build -c Release`, then [plan and apply](../guides/plan-apply.md).
