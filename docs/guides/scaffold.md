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
    Zava.WebResources/      # opt-in TypeScript form scripts / web resources (add with --web-resources)
  tests/
    Zava.Plugins.Tests/      # xunit tests using Vela.Testing - no connection (omit with --no-tests)
    Zava.Integrations.Tests/ # xunit tests for the Integrations package
  Directory.Build.props        # shared build settings for src/ and tests/
  nuget.config                 # local-packages/ feed for Vela.Plugins / Vela.Testing
```

The tree comments label each project. Two carry detail beyond that:

- `Zava.Plugins` builds a Dataverse plugin package `.nupkg` via the `Microsoft.PowerApps.MSBuild.Plugin`
  SDK. Code is organised **by table**, with each file's namespace matching its folder, and is seeded with
  working examples: a single-step plugin (schedules a follow-up task), a multi-step router plugin,
  table-**bound** and **global** Custom APIs, a cross-table shared service, and two **table-scoped**
  services (one write, one read).
- `Zava.Plugins.Tests` runs those plugins and Custom APIs with [`Vela.Testing`](testing.md), no Dataverse
  connection. Run them with `dotnet test tests/Zava.Plugins.Tests`.

Flags: `--prefix` (default: derived from name), `--output` (default: `.`), `--core-version`
(default: `1.0.0`), `--no-integrations`, `--no-tests`, `--no-console`, `--managed-identity` / `--mi`
(wire an opt-in package-signing step into the Integrations project - see [Sign the plugin package](signing.md)),
`--web-resources` (also scaffold the TypeScript web-resources project, see below; `--web-resources-namespace`
and `--web-resources-folder` set its global name and build folder),
`--force` (overwrite an existing folder). The generated solution references `Vela.Plugins` as
a NuGet package, then follows the `dotnet pack` → `vela plan` → `vela apply` loop from the [Quickstart](../getting-started/quickstart.md).

It also emits a `nuget.config` with a `local-packages/` folder feed, since `Vela.Plugins` (and
`Vela.Testing`, used by the test project) are **not on nuget.org**: point that source at your private
feed, or drop their `.nupkg` files into `local-packages/` (nuget.org is inherited for the Microsoft SDK
and test packages).

## Web resources project (opt-in)

> [!NOTE]
> Preview / work in progress. This is part of the new client-side initiative and may change.

Pass `--web-resources` (or answer the interactive prompt) to also scaffold a TypeScript project for
model-driven **form scripting**, sitting next to the plugin projects in the same solution:

```text
src/
  Zava.WebResources/           # opt-in TypeScript web resources (--web-resources)
    Zava.WebResources.esproj      # JS/TS SDK project: VS "Build" runs npm
    package.json                  # tsup build + Vitest, depends on vela-client
    tsup.config.ts                # globalName (your publisher prefix) + one entry per form
    tsconfig.json
    src/
      account/main.ts             # hand-written form scripts (example)
      generated/                  # regenerated form models (never edited)
    test/account.test.ts          # Vitest example
```

The bundle is built with **tsup** (`npm run build` / `npm run dev`), not the CLI, and exposes a dotted
global named after **your** publisher prefix (`--web-resources-namespace`, default: the project prefix
lower-cased), never "Vela". Its output folder (`--web-resources-folder`, default `web`) mirrors the Dataverse
web resource names and is what a later `vela apply --webresources` deploys. Regenerate the form models under
`src/generated/` with [`vela models --forms --lang ts`](models.md). See the runtime library in the separate
`vela-client` package for `VelaForm` and the typed accessors.
