# Scaffold a new solution (vela init)

Generate a build-ready plugin solution in one step:

```powershell
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --output .
# or run the wizard:
vela init --interactive
```

By default the scaffold is **minimal**: the structure and nothing to delete. It creates `ZavaSales/` with the
`.sln` at the root and sibling `src/` and `tests/` folders:

```text
ZavaSales/
  ZavaSales.sln
  src/
    Zava.Shared/            # early-bound models + shared services (net48;net10.0); empty until `vela models`
    Zava.Plugins/           # plugin package -> builds a Dataverse plugin-package .nupkg; no plugin until `vela add`
      ProjectServices.cs    #   the assembly's service registrar, empty
      Messages.cs           #   the project's message keys and their source, empty
    Zava.Integrations/      # opt-in second plugin package, same two files (add with --integrations)
    Zava.WebResources/      # opt-in TypeScript form scripts / web resources (add with --web-resources)
  tests/
    Zava.Plugins.Tests/     # xunit + Vela.Testing, no connection (omit with --no-tests); empty until `vela add test`
    Zava.Integrations.Tests/ # with --integrations (omit with --no-tests); empty until `vela add test`
  .vela/scaffold-manifest.json  # what init wrote, so `--force` never touches your code (commit it)
  AGENTS.md                    # instructions for AI coding agents: steps, Custom APIs, tests, the CLI loop
  Directory.Build.props        # shared build settings for src/ and tests/
```

The minimal solution builds, tests and plans unedited: the package registers nothing, so `vela plan` reports no
changes, and no demo step is ever deployed.

Each plugin project starts with two assembly-wide files, generated in both modes:

- `ProjectServices.cs` - an empty [`IServiceRegistrar`](dependency-injection.md#share-services-across-the-assembly),
  declared with `[assembly: Vela.DI.VelaServiceRegistrar(typeof(Zava.Plugins.ProjectServices))]`. The services and
  message sources it registers apply to every plugin and Custom API of the assembly, before each one's own
  `ConfigureServices`. It registers nothing until you uncomment or add a line.
- `Messages.cs` - an empty `ProjectMessages` class for the message key constants and the `DictionaryMessageSource`
  that holds their texts; register its `Source` in `ProjectServices` once it has a key
  ([Localized messages](localization.md)).

The plugin projects also generate their XML documentation file (`GenerateDocumentationFile`), so a public type or
member without a `<summary>` is a CS1591 warning; the generated code and the files `vela add` writes document theirs.
`Zava.Shared` generates its documentation file too, for the IntelliSense of the models, but silences CS1591: the
models `vela models` writes there do not document every member. The test projects do not generate one.

Fill it in:

```powershell
vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated
vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins
vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests
dotnet build ZavaSales.sln -c Release
dotnet test tests/Zava.Plugins.Tests -c Release
vela plan --packages-dir artifacts --interactive
```

### Samples (`--samples`)

Pass **`--samples`** to see every pattern working instead. It adds, to the same layout:

```text
  src/
    Zava.Shared/            # + hand-written Account/Contact models, a cross-table GreetingService
    Zava.Plugins/
      Account/                 #   organised by table: Plugins/ Services/ CustomApis/
      Contact/
      GlobalCustomApis/        #   unbound Custom APIs
    Zava.Integrations/      # second plugin package (omit with --no-integrations)
    Zava.ConsoleApp/        # .NET 10 interactive browser login (omit with --no-console)
  tests/
    Zava.Plugins.Tests/      # + tests for every sample
    Zava.Integrations.Tests/ # xunit tests for the Integrations package
```

`Zava.Plugins` then ships working examples: a single-step plugin (schedules a follow-up task), a multi-step router
plugin, table-**bound** and **global** Custom APIs, a cross-table shared service, two **table-scoped** services (one
write, one read) and `AccountMessageReference`, a cheat-sheet with one step per typed message. The cheat-sheet is an
`abstract` class: it compiles, but neither `vela apply` nor Dataverse registers it, so it is never deployed. `Zava.Plugins.Tests` runs those plugins and Custom APIs
with [`Vela.Testing`](testing.md), no Dataverse connection: `dotnet test tests/Zava.Plugins.Tests`. Each file's
namespace matches its folder. Generate a samples solution next to your real one to copy from it.

### Flags

- `--prefix` (default: derived from the name) - the .NET namespace / assembly prefix.
- `--publisher-prefix` - the Dataverse publisher prefix of your solution (2-8 characters, letter first,
  alphanumeric). It names the Custom APIs (`<publisherprefix>_<Name>`) and is recorded in the projects as
  `VelaPublisherPrefix`, which `vela add customapi` uses. It also prefixes the plugin package ids
  (`zava_Zava.Plugins`, `zava_Zava.Integrations`), as Dataverse requires to import a package, so `vela plan` and
  `vela apply` accept them without `--solution`; the assemblies keep the project names (`Zava.Plugins.dll`).
  Without it, the prefix is derived from `--prefix`; with `--samples` a derived value that is not a valid publisher
  prefix is refused, and a valid one prefixes the package ids the same way. A minimal scaffold without
  `--publisher-prefix` keeps unprefixed package ids (`Zava.Plugins`): `vela apply --solution` adds the solution's
  prefix.
- `--samples` - add the demo code and the Integrations / ConsoleApp projects (see above). `--minimal` /
  `--no-demos` are accepted aliases of the default.
- `--integrations` - add the `Zava.Integrations` plugin project (and `Zava.Integrations.Tests` unless `--no-tests`)
  without `--samples`: a second, empty plugin package with the same build setup as `Zava.Plugins`. With `--samples`
  it is included, with its demo code, unless `--no-integrations`; `--integrations` with `--no-integrations` is refused.
- `--managed-identity` / `--mi` - wire an opt-in package-signing step into the Integrations project (see
  [Sign the plugin package](signing.md)). It implies `--integrations`, and is refused with `--no-integrations`. The
  Integrations package id gets the publisher prefix (`zava_Zava.Integrations`), so a signed package needs no rename
  at `vela apply --solution`; pass `--publisher-prefix` when the one derived from `--prefix` is not valid.
- `--output <dir>` (default: `.`) - the parent folder: the solution is created in `<dir>/<SolutionName>`, so
  `--output ~/Desktop --name ZavaDemo` gives `~/Desktop/ZavaDemo`.
- `--no-tests`; with `--samples`: `--no-integrations`, `--no-console`.
- `--core-version` (also `--core-version=X`) - the `Vela.Plugins` / `Vela.Testing` version the projects reference;
  default: the latest published framework version, resolved from nuget.org.
- `--web-resources` - also scaffold the TypeScript web-resources project (see below); `--web-resources-namespace`
  and `--web-resources-folder` set its global name and build folder (they warn and are ignored without
  `--web-resources`).
- `--force` - regenerate into an existing folder (see below). `--verbose` prints a failure's details, `--json` a
  machine-readable report.

The generated solution references `Vela.Plugins` as a NuGet package, then follows the `dotnet build -c Release` →
`vela plan` → `vela apply` loop from the [Quickstart](../getting-started/quickstart.md).

### Regenerate with `--force`

`vela init` records every file it writes, with a content hash, in `.vela/scaffold-manifest.json`. Run it again with
`--force` (for example to add `--samples` or `--web-resources`, or to change `--core-version`) and it:

- writes the files that are missing and overwrites only the ones you have **not edited** since it wrote them;
- removes a file it generated earlier that the new options no longer include (a dropped project), again only if
  unedited, and prunes the folders this empties;
- **keeps** everything else: your own plugins and tests, edited samples, and every existing file when the folder
  has no manifest (a scaffold from an older CLI). It lists them; delete a kept file to have it regenerated.

Commit the manifest, so a `--force` on another clone knows which files are generated.

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
