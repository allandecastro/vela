# Initialize a solution

`vela init` generates a complete, build-ready plugin solution in one step:

```powershell
vela init --name ZavaSales --prefix Zava --output .
# or run the wizard:
vela init --interactive
```

This creates `ZavaSales/` with the `.sln` at the root and sibling `src/` and `tests/` folders:

```text
ZavaSales/
  ZavaSales.sln
  src/
    Zava.Shared/            # early-bound Account/Contact models + shared services (net462;net10.0)
    Zava.Plugins/           # plugin package -> builds a Dataverse plugin-package .nupkg
      Account/                 #   organised by table: Plugins/ Services/ CustomApis/
      Contact/
      GlobalCustomApis/        #   unbound Custom APIs
    Zava.Integrations/      # second plugin package (omit with --no-integrations)
    Zava.ConsoleApp/        # .NET 10 interactive browser login (omit with --no-console)
  tests/
    Zava.Plugins.Tests/      # xunit tests using Vela.Testing - no connection (omit with --no-tests)
    Zava.Integrations.Tests/ # xunit tests for the Integrations package
  Directory.Build.props        # shared build settings for src/ and tests/
  nuget.config                 # local-packages/ feed for Vela.Plugins / Vela.Testing
```

- `Zava.Shared` - early-bound `Account`/`Contact` models and shared services (multi-targets `net462;net10.0`).
- `Zava.Plugins` - plugin package that builds a Dataverse plugin-package `.nupkg` (via the
  `Microsoft.PowerApps.MSBuild.Plugin` SDK). Code is organised **by table** (`Account/`, `Contact/`)
  with `Plugins/`, `Services/` and `CustomApis/` subfolders, plus `GlobalCustomApis/` for unbound APIs;
  each file's namespace matches its folder. Seeded with a single-step plugin (schedules a follow-up
  task), a multi-step router plugin, table-**bound** + **global** Custom APIs, a cross-table shared
  service, and two **table-scoped** services (one write, one read).
- `Zava.Integrations` - a second plugin package (same layout, integration-themed names). Omit with `--no-integrations`.
- `Zava.Plugins.Tests` - a unit-test project (xunit) that runs the seeded plugins/Custom APIs with
  `Vela.Testing` - no Dataverse connection.

> [!TIP]
> For the full set of `vela init` flags and the wizard, see the [Scaffold a solution](../guides/scaffold.md) guide.

## Where to go next

- **Write your own code** - [plugins](../guides/plugins.md), [Custom APIs](../guides/custom-apis.md),
  [dependency injection](../guides/dependency-injection.md), [caching](../guides/caching.md).
- **Test without a connection** - [Testing with Vela.Testing](../guides/testing.md).
- **Ship** - `dotnet pack` → [plan & apply](../guides/plan-apply.md).
