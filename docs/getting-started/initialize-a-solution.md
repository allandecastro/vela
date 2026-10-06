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
    Zava.WebResources/      # opt-in TypeScript form scripts / web resources (add with --web-resources)
  tests/
    Zava.Plugins.Tests/      # xunit tests using Vela.Testing - no connection (omit with --no-tests)
    Zava.Integrations.Tests/ # xunit tests for the Integrations package
  Directory.Build.props        # shared build settings for src/ and tests/
```

The comments above label each project. Generated code is organised **by table**, with each file's
namespace matching its folder. `Zava.Plugins` ships working examples: a single-step plugin (schedules a
follow-up task), a multi-step router plugin, table-**bound** and **global** Custom APIs, a cross-table
shared service, and two **table-scoped** services (one write, one read). `Zava.Plugins.Tests` runs those
plugins and Custom APIs with `Vela.Testing`, no Dataverse connection.

> [!TIP]
> Want a bare starting point instead of the examples? Add **`--minimal`** (alias `--no-demos`) to get just the
> empty `Shared` and `Plugins` projects - it builds unedited and you fill it in with `vela models` and
> `vela add`. See [Scaffold a solution](../guides/scaffold.md#minimal-demo-free-scaffold).

> [!TIP]
> For the full set of `vela init` flags and the wizard, see the
> [Scaffold a solution](../guides/scaffold.md) guide.

## Where to go next

- **Write your own code** - [plugins](../guides/plugins.md), [Custom APIs](../guides/custom-apis.md),
  [dependency injection](../guides/dependency-injection.md), [caching](../guides/caching.md).
- **Test without a connection** - [Testing with Vela.Testing](../guides/testing.md).
- **Ship** - `dotnet build -c Release` (packages the plugin into `artifacts/`) → [plan & apply](../guides/plan-apply.md).
