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
    Zava.Shared/            # early-bound models + shared services (net462;net10.0); empty until `vela models`
    Zava.Plugins/           # plugin package -> builds a Dataverse plugin-package .nupkg; empty until `vela add`
  tests/
    Zava.Plugins.Tests/     # xunit tests using Vela.Testing - no connection (omit with --no-tests)
  .vela/scaffold-manifest.json  # lets `vela init --force` regenerate without touching your code
  Directory.Build.props        # shared build settings for src/ and tests/
```

It builds, tests and plans unedited, and deploys nothing until you add a plugin:

- `vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated`
  generates the early-bound models.
- `vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins` adds a
  plugin, and `vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests` its test. See
  [Add code with vela add](../guides/add.md).
- `--publisher-prefix` is your solution publisher's prefix; Custom APIs are named `<publisherprefix>_<Name>`.

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
