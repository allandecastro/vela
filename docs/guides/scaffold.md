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
(the `Vela.Plugins` / `Vela.Testing` version the generated projects reference; default: the latest published
framework version, resolved from nuget.org), `--no-integrations`, `--no-tests`, `--no-console`,
`--managed-identity` / `--mi`
(wire an opt-in package-signing step into the Integrations project - see [Sign the plugin package](signing.md)),
`--web-resources` (also scaffold the TypeScript web-resources project, see below; `--web-resources-namespace`
and `--web-resources-folder` set its global name and build folder),
`--force` (overwrite an existing folder). The generated solution references `Vela.Plugins` as
a NuGet package, then follows the `dotnet build -c Release` → `vela plan` → `vela apply` loop from the [Quickstart](../getting-started/quickstart.md).

`Vela.Plugins` and `Vela.Testing` resolve from **nuget.org** (the default package source), so the generated
solution restores and builds out of the box with no `nuget.config`. Add your own only if you use a private feed.

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
    tsup.config.ts                # globalName (your publisher prefix); imports the entry map
    web-resources.entries.ts      # the entry map (name -> source); vela add form-script maintains it
    tsconfig.json
    src/
      account/Forms/main.ts       # example form script (typed accessors + a typed Web API query)
      account/Forms/main.test.ts  # co-located Vitest example (rules + save guard)
      generated/                  # models (ships an EXAMPLE set; vela models overwrites it)
        index.ts                  #   the root barrel, imported as @generated
    test/
      entries.test.ts             # guard: every entry must export onLoad/onSave
```

The scaffold ships a **working** example, not a stub: the `account` form script compiles and its Vitest passes
out of the box (`npm install && npm test`), against a small example model set under `src/generated/` (each file
carries an `// EXAMPLE - regenerate with: vela models ...` header). Run [`vela models`](models.md) to replace it
with the real models from your environment. Form scripts import generated symbols through the `@generated` alias
(a tsconfig path to `src/generated`, resolved by tsup and Vitest), which points at the generated root barrel.

The bundle is built with **tsup** (`npm run build` / `npm run dev`), not the CLI, and exposes a dotted
global named after **your** publisher prefix (`--web-resources-namespace`, default: the project prefix
lower-cased), never "Vela". Its output folder (`--web-resources-folder`, default `dist`) mirrors the Dataverse
web resource names and is what a later `vela apply --webresources` deploys. Regenerate the form models under
`src/generated/` with [`vela models --forms`](models.md). See the runtime library in the separate
`vela-client` package for `VelaForm` and the typed accessors.

Each form web resource is one **entry** in `web-resources.entries.ts` (`<prefix>_/js/<entity>/Forms/main`
mapped to its `src/<entity>/Forms/main.ts` source). Add one with:

```powershell
vela add form-script account   # scaffolds src/account/Forms/main.ts (+ main.test.ts) and registers the entry
```

`vela add form-script <entity>` refuses to overwrite an existing file (use `--force`); its entry-map insertion
stays idempotent. The `<entity>` is the table logical name (lower-cased for the folder, PascalCased for the
referenced form model).
