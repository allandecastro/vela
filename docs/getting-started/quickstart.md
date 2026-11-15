# Quickstart

From an empty folder to a registered, tested plugin in five steps. You need the .NET 10 SDK and a Dataverse
environment with an existing solution (here `ZavaSales`, publisher prefix `zava`):

```bash
# 1. Install the CLI (.NET global tool - exposes the `vela` command)
dotnet tool install --global Vela.Cli

# 2. Scaffold a build-ready solution (minimal; add --samples for worked examples), then add a plugin and its test
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --solution ZavaSales
cd ZavaSales
vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated --interactive
vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins
vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests

# 3. Build the solution: the plugin package lands in artifacts/ as a .nupkg (the build packs it; not dotnet pack)
dotnet build ZavaSales.sln -c Release

# 4. Preview what would change in an existing Dataverse solution (read-only)
vela plan  --packages-dir artifacts --solution ZavaSales --interactive

# 5. Apply the changes (idempotent create/update/delete). On a terminal it shows the plan and asks you to
#    confirm; --auto-approve skips the prompt in CI.
vela apply --packages-dir artifacts --solution ZavaSales --interactive
```

`--solution` names a solution that already exists in the environment; `plan` and `apply` require it and add the
components to it. Dataverse imports a plugin package only when its name carries the publisher prefix:
`--publisher-prefix zava` names the package `zava_Zava.Plugins` (built as `artifacts/zava_Zava.Plugins.1.0.0.nupkg`).
Without `--publisher-prefix`, the package is `Zava.Plugins` and `--solution` gives it the prefix. `vela init` records
both values in `PROJECT.md` for you and your agents; the CLI never reads that file, so the commands take them as
flags.

`vela add plugin` writes `src/Zava.Plugins/Account/Plugins/AccountValidator.cs`: a Create PostOperation step
filtered on `Name`, with a `HandleCreate` handler to fill in (`--message`, `--stage` and `--handler` change them).
`vela add test` writes its `PluginPipeline` test, which `dotnet test tests/Zava.Plugins.Tests -c Release` runs (on
Windows; see [Install](install.md#prerequisites) for macOS and Linux).

What you get from `vela init`: a minimal solution that references `Vela.Plugins` as a NuGet package - empty
model and plugin projects plus a no-connection test project, and an `AGENTS.md` that tells AI coding agents how to write steps,
Custom APIs and tests in it - which `vela models` and `vela add` fill. Run
`vela init --samples` instead for seeded plugins, Custom APIs, services and their tests. See [Initialize a solution](initialize-a-solution.md)
for the layout and the [guides](../guides/index.md) to write and test your own code.

> [!NOTE]
> Prefer to add Vela to an **existing** plugin project? Reference the package directly instead of
> scaffolding: `dotnet add src/Zava.Plugins package Vela.Plugins`, and `dotnet add package Vela.Testing` in its
> test project. See [Migrating an existing plugin project](../guides/migrating.md).

## Next steps

- **[What `vela init` scaffolds](initialize-a-solution.md)** - the projects you just generated and where to edit.
- **Author** - [write a plugin](../guides/plugins.md) or a [Custom API](../guides/custom-apis.md).
- **Test** - [run it against an in-memory Dataverse](../guides/testing.md), no connection.
- **Deploy** - [plan then apply](../guides/plan-apply.md) into your environment.

