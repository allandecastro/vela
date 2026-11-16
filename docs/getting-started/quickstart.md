---
_description: "From an empty folder to a tested, registered plugin: install, init, models, add, build and test, plan, apply"
---

# Quickstart

This page takes you from an empty folder to a tested plugin registered in Dataverse, in five steps. It is for a C#
developer who has [installed the prerequisites](install.md#prerequisites): the .NET 10 SDK and a Dataverse
environment with an existing unmanaged solution (here `ZavaSales`, publisher prefix `zava`).

```bash
# 1. Install the CLI (a .NET global tool that exposes the `vela` command)
dotnet tool install --global Vela.Cli

# 2. Scaffold a minimal solution, generate the Account model, then add a plugin and its test
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --solution ZavaSales
cd ZavaSales
vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated --interactive
vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins
vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests

# 3. Build (the build packs the plugin package into artifacts/), then run the tests
dotnet build ZavaSales.sln -c Release
dotnet test tests/Zava.Plugins.Tests -c Release

# 4. Preview what would change in the ZavaSales solution (read-only)
vela plan  --packages-dir artifacts --solution ZavaSales --interactive

# 5. Apply the changes. On a terminal it shows the plan and asks you to type "yes"; --auto-approve skips the prompt in CI.
vela apply --packages-dir artifacts --solution ZavaSales --interactive
```

`--interactive` signs you in through the browser and caches the token under `~/.vela`, so later runs are silent. For
app registrations and CI, see [Plan and apply](../guides/plan-apply.md).

## What each step produces

**Scaffold.** `vela init` writes a minimal solution that references `Vela.Plugins` and `Vela.Testing` as NuGet
packages: an empty model project, an empty plugin project, a no-connection test project, and an `AGENTS.md` that
tells AI coding agents how to write steps, Custom APIs and tests in it. `vela init --samples` adds seeded plugins,
Custom APIs, services and their tests instead. See [Initialize a solution](initialize-a-solution.md) for the layout.

**Model.** `vela models` connects to the environment and writes the early-bound `Account` class into `Zava.Shared`.
The plugin and its test use it.

**Plugin.** `vela add plugin` writes `src/Zava.Plugins/Account/Plugins/AccountValidator.cs`: a Create PostOperation
step filtered on `Name`, with a `HandleCreate` handler for your logic. `--message`, `--stage` and `--handler` change
those defaults. See [Add a component (`vela add`)](../guides/add.md).

**Test.** `vela add test` writes `tests/Zava.Plugins.Tests/Account/Plugins/AccountValidatorTests.cs`. It drives the
plugin through `PluginPipeline`, which simulates the Dataverse pipeline in memory: one test asserts that the step fires, and a skipped
one is the skeleton for asserting what the plugin does. `dotnet test` runs .NET Framework tests on Windows only; on
macOS and Linux, run them with Mono (see [Install](install.md#prerequisites)).

**Build.** `dotnet build -c Release` packs the plugin package into `artifacts/` as a `.nupkg`. Use the build, not
`dotnet pack`.

**Plan and apply.** `--solution` names the solution the components are added to; `plan` and `apply` require it, and
refuse a solution that is managed in the target environment. Dataverse imports a plugin package only when its name
carries the publisher prefix: `--publisher-prefix zava` names the package `zava_Zava.Plugins` (built as
`artifacts/zava_Zava.Plugins.1.0.0.nupkg`). Without `--publisher-prefix`, the package is `Zava.Plugins` and `apply`
gives it the prefix of the `--solution` publisher. `apply` is idempotent: a second run with no change does nothing.

`vela init` records the publisher prefix and the solution in `PROJECT.md` for you and your agents. The CLI never
reads that file, so the commands take them as flags.

> [!NOTE]
> To add Vela to an **existing** plugin project instead of scaffolding one, reference the packages directly:
> `dotnet add src/Zava.Plugins package Vela.Plugins`, and `dotnet add package Vela.Testing` in its test project.
> See [Migrating an existing plugin project](../guides/migrating.md).

## Next steps

- **Understand the layout**: [what `vela init` scaffolds](initialize-a-solution.md) and where to edit.
- **Author**: [write a plugin](../guides/plugins.md) or a [Custom API](../guides/custom-apis.md).
- **Test**: [run it through PluginPipeline](../guides/testing.md), with no connection.
- **Deploy**: [plan then apply](../guides/plan-apply.md) into your environment.
- **Follow a full example**: the [end-to-end example](../guides/end-to-end.md) adds an image and reads the trace log.
