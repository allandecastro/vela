# Initialize a solution

`vela init` generates a build-ready plugin solution in one step:

```powershell
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --output .
# or run the wizard:
vela init --interactive
```

This creates a **minimal** `ZavaSales/`, with the `.sln` at the root and sibling `src/` and `tests/` folders:

```text
ZavaSales/
  ZavaSales.sln
  src/
    Zava.Shared/            # early-bound models + shared services (net48;net10.0); empty until `vela models`
    Zava.Plugins/           # plugin package -> builds a Dataverse plugin-package .nupkg; no plugin until `vela add`
      ProjectServices.cs    #   assembly-wide services and message sources, empty
      Messages.cs           #   the project's message keys, empty
  tests/
    Zava.Plugins.Tests/     # xunit tests using Vela.Testing - no connection (omit with --no-tests)
  .vela/scaffold-manifest.json  # lets `vela init --force` regenerate without touching your code
  AGENTS.md                    # instructions for AI coding agents: steps, Custom APIs, tests, the CLI loop
  .editorconfig                # code style: usings inside the namespace
  Directory.Build.props        # shared build settings for src/ and tests/
```

It builds, tests and plans unedited, and deploys nothing until you add a plugin:

- `vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated`
  generates the early-bound models.
- `vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins` adds a
  plugin, and `vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests` its test. See
  [Add code with vela add](../guides/add.md).
- Each file's namespace follows its folder (`Zava.Plugins.Account.Plugins`): keep the `using` directives inside a
  block namespace, or `Account` resolves to a namespace (CS0118). See
  [Namespaces and usings](../guides/scaffold.md#namespaces-and-usings).
- `ProjectServices.cs` registers services and message sources for every plugin and Custom API of the assembly, and
  `Messages.cs` holds the message keys: see [Localized messages](../guides/localization.md).
- `--publisher-prefix` is your solution publisher's prefix; Custom APIs are named `<publisherprefix>_<Name>`, and the
  plugin package `<publisherprefix>_Zava.Plugins` (`artifacts/zava_Zava.Plugins.1.0.0.nupkg`).

> [!TIP]
> Want worked examples first? Add **`--samples`** for sample models, services, a single-step and a multi-step
> plugin, bound and global Custom APIs with their tests, and the Integrations and ConsoleApp projects. See
> [Scaffold a solution](../guides/scaffold.md#samples---samples).

> [!TIP]
> For the full set of `vela init` flags and the wizard, see the
> [Scaffold a solution](../guides/scaffold.md) guide.

## Where to go next

- **Write your own code** - [plugins](../guides/plugins.md), [Custom APIs](../guides/custom-apis.md),
  [dependency injection](../guides/dependency-injection.md), [caching](../guides/caching.md).
- **Test without a connection** - [Testing with Vela.Testing](../guides/testing.md).
- **Ship** - `dotnet build -c Release` (packages the plugin into `artifacts/`) → [plan & apply](../guides/plan-apply.md).
