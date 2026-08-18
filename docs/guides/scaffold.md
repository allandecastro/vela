# Scaffold a new solution (vela init)

Generate a complete, build-ready plugin solution in one step:

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
  [`Vela.Testing`](testing.md) - no Dataverse connection. Run with
  `dotnet test tests/Zava.Plugins.Tests`. Omit with `--no-tests`.
- `Zava.Integrations.Tests` - the matching test project for the Integrations package (generated when both
  the Integrations project and tests are included).
- `Zava.ConsoleApp` - a .NET 10 console app that signs in to Dataverse **interactively** (browser).
  Omit with `--no-console`.

Flags: `--prefix` (default: derived from name), `--output` (default: `.`), `--core-version`
(default: `1.0.0`), `--no-integrations`, `--no-tests`, `--no-console`, `--managed-identity` / `--mi`
(wire an opt-in package-signing step into the Integrations project - see [Sign the plugin package](signing.md)),
`--force` (overwrite an existing folder). The generated solution references `Vela.Plugins` as
a NuGet package, then follows the `dotnet pack` → `vela plan` → `vela apply` loop from the [Quickstart](../getting-started/quickstart.md).

It also emits a `nuget.config` with a `local-packages/` folder feed, since `Vela.Plugins` (and
`Vela.Testing`, used by the test project) are **not on nuget.org**: point that source at your private
feed, or drop their `.nupkg` files into `local-packages/` (nuget.org is inherited for the Microsoft SDK
and test packages).
