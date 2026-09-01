# Changelog

All notable changes to Vela are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

- Vela ships as **two independent release lines**:
  - **Framework** - `Vela.Plugins` + `Vela.Testing`, versioned **together** (the test harness reuses the
    runtime's internals via `InternalsVisibleTo`, so the two must stay in lockstep) and tagged `framework-vX.Y.Z`.
  - **CLI** - `Vela.Cli`, versioned on its own cadence and tagged `cli-vX.Y.Z`.
- **Pre-1.0 (`0.y.z`):** the public API is still settling. A **minor** bump (`0.y`) may contain breaking
  changes; a **patch** bump (`0.y.z`) is reserved for backwards-compatible fixes. Breaking changes are called
  out under a **Changed**/**Removed** heading in the entry.
- **From 1.0 onward:** standard SemVer - **major** for breaking changes, **minor** for backwards-compatible
  features, **patch** for backwards-compatible fixes.
- A line is released by pushing its tag (`framework-vX.Y.Z` or `cli-vX.Y.Z`), which packs and publishes only
  that line's package(s). The **Unreleased** section below accumulates changes until the next tag, at which
  point it is renamed to that version with a date.

## [Unreleased]

CLI line only.

### Added

- `plan` / `apply` / `inspect` / `models` `--interactive` now **remember the last Dataverse URL** (in
  `~/.vela/config.json`, or `$VELA_CONFIG_DIR`) and offer it as the prompt default - press Enter to reuse it
  instead of re-typing the org URL every run. The URL is not a secret, so it is a plain preferences file.
- `--interactive` browser login now **persists the token** across runs, so a repeat run is silent instead of
  re-opening the browser. It uses the Dataverse SDK's own OAuth token cache (`TokenCacheStorePath` ->
  `~/.vela/token.dat`), which is DPAPI-encrypted on Windows (Keychain on macOS, libsecret on Linux) - no extra
  dependency and no MSAL version change. `--fresh-login` forces a new prompt for the run, and the new **`vela
  logout`** command clears the cached token and the remembered URL. (A custom `--authority` still uses the
  previous per-run token flow, which has no persistent cache.)

### Changed

- The scaffolded form example now wires `defineForm(Model, Class, { namespace: '<prefix>.Forms.<entity>' })`, so a
  form web resource exposes its handlers at `<prefix>.Forms.<entity>.onLoad` / `.onSave` - consistent with the
  ribbon's `<prefix>.Ribbon.<entity>.<command>`. The JS global name and both namespaces are lower-cased. The
  `export { onLoad, onSave }` is kept (the entries guard and unit tests rely on it).

### Fixed

- `plan` / `apply` / `inspect` no longer leave a `vela-tools\<guid>` temp folder behind (or warn `Access to the
  path '...dll' is denied`) on Windows. The scanner now byte-loads the package assemblies into an isolated load
  context instead of `Assembly.LoadFrom`, which held the `.dll` files open for the process lifetime, so the
  extracted folder can be deleted right after the scan.
- `plan` / `apply` / `inspect` now reject an unknown or misspelled option (e.g. `--packages-dur`) with a clear
  `Unknown option '...'` error instead of silently ignoring it. Previously a typo in `--packages-dir` dropped the
  package input and only surfaced later as a confusing `Missing package input` (after prompting, in interactive
  mode).
- The same unknown-option guard now covers `init`, `add`, `models` and `sign` (each had its own parser that
  silently ignored unrecognized flags), and every command honors `--help` / `-h` / `-?` **anywhere** in its args
  (printing usage and exiting success) - previously only `add` / `models` did, and only as the first argument, so
  `vela init --help` / `vela sign --help` did nothing useful. A bare `--` is treated as a positional separator.
- Scaffolded solutions now package the plugin with **`dotnet build -c Release`**, not `dotnet pack`. The Power
  Platform plugin SDK bundles the dependent assemblies during the build (from the publish output), so a cold
  `dotnet pack` emitted an assembly-less package (`NU5128`) that `vela plan` rejected with "No assemblies found in
  package". The plugin projects set `<PackageOutputPath>` so the Release build drops the package into `artifacts/`,
  the `vela init` next steps and generated README/docs use `dotnet build`, and the opt-in signing target (which
  runs `AfterTargets="Pack"`, fired by `GeneratePackageOnBuild`) now documents `dotnet build -c Release`.

## [0.1.16] - 2026-08-28

CLI line only.

### Added

- Discoverability for ribbon commands: `vela init --web-resources` now prints a `vela add ribbon-script <entity>`
  next step, and the scaffolded web-resources project ships a `README.md` documenting form scripts and a Ribbon
  commands section (scaffold, plus wiring the button at `<Prefix>.Ribbon.<entity>.<command>` in the Command
  Designer).

## [0.1.15] - 2026-08-28

CLI line only.

### Added

- Ribbon commands (preview). `vela models --forms` now emits, next to each entity's Main form model, a typed
  command-context alias `export type <Entity>Cmd = FormCommandContext<typeof <Entity>Main>;` (from
  `vela-client/ribbon`), re-exported by the root barrel. `vela add ribbon-script <entity>` scaffolds
  `src/<entity>/Ribbon/commands.ts` (a `formCommand` action + a `formRule` enable rule wired with
  `bootstrapCommands`, over the entity's generated Main form model) and a co-located `fakeFormCommand` test, and
  registers the tsup entry; it prints the
  `<Prefix>.Ribbon.<entity>` namespace the maker points the button at in the Command Designer (deploy is a web
  resource, maker-wired, no appaction generated). The scaffold ships the `<Entity>Cmd` alias in `@generated`, its
  `vela-client` dependency is bumped to `^0.1.6`, and the entries guard is ribbon-aware (Forms entries export
  onLoad/onSave, Ribbon entries export at least one command).

### Changed

- The root barrel re-exports type-only symbols (an interface or type alias with no same-name value, such as the
  new `<Entity>Cmd` alias) with `export type { ... }` so they survive isolated-modules transpilation.

## [0.1.14] - 2026-08-28

CLI line only.

### Fixed

- `vela models --tables`: a multi-select choice column with no option metadata is now typed `number[]` (a
  collection of option values) and listed in the `defineTable` `multiSelect` descriptor, instead of `unknown`.
- The root `index.ts` barrel now re-exports every named `export` form (`type` / `function` / `class` / `let` /
  `var`, with `declare` / `async` / `abstract`), not only `const` / `interface` / `enum`, so future generated
  symbols are never silently left out.

## [0.1.13] - 2026-08-28

CLI line only.

### Added

- `vela models --tables` now types **activity party lists** (email `to`/`from`/`cc`, appointment attendees, ...).
  An activity table exposes one `<entity>_activity_parties?: ActivityParty[] | null` collection (in place of the
  individual PartyList attributes, which share it, distinguished by `participationtypemask`), imports
  `ActivityParty` from `vela-client/webapi`, and its `defineTable` carries `activityParties:
  '<entity>_activity_parties'`. Non-activity tables are unchanged. Requires `vela-client@0.1.5`.

## [0.1.12] - 2026-08-28

CLI line only.

### Changed

- The redundant `--lang` flag is no longer advertised on `vela models --forms` / `--tables` (their output is
  TypeScript, selected by `--forms` / `--tables`, not by `--lang`). It stays accepted for back-compat (`--lang ts`
  is a no-op there; an explicit non-`ts` value is still rejected), and the generated-file banners are unchanged.

## [0.1.11] - 2026-08-27

CLI line only.

### Changed

- The scaffolded web-resources example now demonstrates **both testing layers**: the form rules (via `fakeForm`)
  and a form method that calls `createWebApi()`, tested with a fake ambient Web API via
  `fakeForm(model, class, { webApi })` + `createFakeWebApi()` (asserting on `form.instance` and calling
  `form.restore()`). The scaffold's `vela-client` dependency is bumped to `^0.1.4`.

## [0.1.10] - 2026-08-25

CLI line only.

### Changed

- `vela models --forms` and `--tables` can now be **combined in one run**: form models and table types then share
  one `option-sets/` folder (the enum files are the union) and a single complete `index.ts` barrel that re-exports
  both. A single-scope run still barrels only its own scope. This is the recommended way to regenerate a
  web-resources project's `src/generated/` (`vela models --forms --tables --out src/generated --overwrite`), so
  the `@generated` barrel covers form models, table types and enums together. Documented the `--overwrite` needed
  to regenerate into a folder that already has generated files (the scaffold ships example files there).

## [0.1.9] - 2026-08-25

CLI line only.

### Added

- `vela models --forms` / `--tables` also emit a root `<out>/index.ts` **barrel** that named-re-exports every
  generated symbol (form-model consts, table interfaces + their `defineTable` consts, option-set enums), so a
  consumer writes `import { AccountMain, Contact, account_customertypecode } from './generated'` instead of many
  deep imports. The re-exports are named (never `export *`) so esbuild keeps tree-shaking, a name is exported once
  (a table's shared type/value name never double-exports), and the barrel is emitted only when there is a symbol.

### Changed

- `vela models --forms` / `--tables` no longer require `--lang ts`: their output is TypeScript only, so `--lang`
  is optional and defaults to `ts` (an explicit non-`ts` value is still rejected). `--lang ts` keeps working.
- `vela add form-script <entity>` now refuses to overwrite an existing file (use `--force`), matching the rest of
  `vela add`, instead of silently skipping it. Registering the tsup entry stays idempotent.
- The scaffolded web-resources project exposes a `@generated` path alias (tsconfig `paths`, resolved by tsup and
  Vitest) pointing at `src/generated`, and the example form script imports the generated symbols through the
  barrel (`import { AccountMain, Contact, account_customertypecode } from '@generated';`) instead of deep paths.

## [0.1.8] - 2026-08-25

CLI line only.

### Changed

- `vela init --web-resources` now scaffolds a **working** example instead of a stub: the `account` form script uses
  typed field accessors, a typed `onChange` rule, a typed `createWebApi()` query (reading a polymorphic lookup
  annotation) and a save guard, wired with `defineForm`, plus a `fakeForm` Vitest. It ships a small example model
  set under `src/generated/` (each file headed `// EXAMPLE - regenerate with: vela models ...`), so
  `npm install && npx tsc --noEmit && npm test` are green out of the box. The scaffold's `vela-client` dependency
  is bumped to `^0.1.3`.

## [0.1.7] - 2026-08-25

CLI line only.

### Added

- `vela models --tables --lang ts` pairs each record interface with a same-named `defineTable<Name>({ entity,
  primaryId, multiSelect })` runtime descriptor (imported from `vela-client/webapi`; the `multiSelect` key is
  omitted when the table has none), and emits a typed logical-name annotation for multi-target lookups
  (`'_<name>_value@Microsoft.Dynamics.CRM.lookuplogicalname'?: 'account' | 'contact'`); single-target lookups omit
  it.

### Changed

- `vela models --forms --lang ts` now writes each form model to `forms/<entity>/<slug>.form.ts` (the folder is the
  table's lower-cased logical name and the file name drops the entity prefix), instead of a flat
  `<entity>.<slug>.form.ts`. Table types (`--tables`) are unchanged (`tables/<logicalname>.ts`).
- The generated enums move from a single `option-sets.ts` to an `option-sets/` folder: global choices in
  `option-sets/global.ts` (each global defined once, shared across tables), and a table's local choices in
  `option-sets/<entity>.ts`. A consuming file imports its locals from `<prefix>option-sets/<entity>` and its
  globals from `<prefix>option-sets/global`, where the prefix is depth-adjusted (`../` for a table, `../../` for a
  form). Shared by both `--forms` and `--tables`.

### Fixed

- `vela models --forms --lang ts` sanitizes the exported const name to a valid TypeScript identifier: a form named,
  for example, `Profile Web Form (Enhanced)` now emits `export const ContactProfileWebFormEnhanced` instead of a
  name with spaces and parentheses that did not compile. Collisions are disambiguated as before.

## [0.1.6] - 2026-08-25

CLI line only.

### Fixed

- The scaffolded `{prefix}.WebResources` project now typechecks: its generated `test/entries.test.ts` imports
  `node:url` / `node:path`, but the scaffold shipped no Node types, so `tsc --noEmit` failed with `TS2307`
  (Vitest and tsup pass because esbuild resolves the `node:` scheme at runtime, but a CI typecheck would break).
  `@types/node` is added to the project's `devDependencies` and `"node"` is listed in the `tsconfig`
  `compilerOptions.types` array.

## [0.1.5] - 2026-08-25

CLI line only.

### Added

- `vela models --tables --lang ts` generates TypeScript **Web API record interfaces** for the scoped tables (the
  Web API twin of the C# early-bound models), so `retrieveMultiple<T>('account').select(...)` is typed without
  hand-writing `T`. It emits one `tables/<logicalname>.ts` per table
  (`export interface <Pascal> extends DataverseRecordMeta`), importing the same shared `option-sets.ts` as
  `--forms`. Columns map by type (string/memo to `string`, the numeric family to `number`, boolean, date/time to
  ISO `string`, choice to its enum, multi-select to an enum array, file/image to `string`); a lookup is exposed as
  its GUID under `_<name>_value`; every non-key column is optional and nullable and the primary key is required.
  Deterministic (columns in metadata order, primary key first), reusing the existing scope and connection flags.
- `vela add form-script <entity>` scaffolds a client-side form script (`src/<entity>/Forms/main.ts` and a
  co-located `main.test.ts`) in the web-resources project and registers its entry in `web-resources.entries.ts`.
  Idempotent, resolves the project from `--project` or the current directory, lower-cases the folder and
  PascalCases the referenced form model.
- Generated TypeScript files carry a self-documenting banner naming the command that regenerates them
  (`// Generated by "vela models --forms --lang ts" - do not edit by hand.`), so a hand-edit is obviously wrong and
  a vela-client drift-guard snapshot has a stable first line to assert.
- Ribbon utility (optional): a pure `RibbonDiffXml` generator turns a `RibbonRegistration` (commands + enable
  rules) into classic ribbon XML with `JavaScriptFunction` actions and `CustomRule` enable rules, each
  referencing its command web resource via `Library="$webresource:<name>"`, validated against the official
  Microsoft Ribbon XSDs. This is a building block only: registering a command on a button (the command bar
  wiring) is left to the developer (Power Apps command designer or a hand-authored `RibbonDiffXml`); automating
  the full deploy is tracked as low priority in the backlog.
- `vela plan` / `vela apply --webresources <folder>` deploy web resources (form scripts, CSS, images, HTML, ...)
  as a third registration kind, mirroring the folder verbatim: each file's unique name is its path relative to the
  root (forward slashes), the type is inferred from the extension, and a content hash drives the diff. Changed and
  created resources are published with `PublishXmlRequest`. Web resources reconcile independently and additively
  (a plugin-only run never touches them and vice versa; deletions are scoped to the web-resource set); a
  `--webresources` run needs no `--package`; and each name is validated to carry the solution's publisher prefix.
- `vela init --web-resources` additionally scaffolds an opt-in `{prefix}.WebResources` TypeScript project
  (depends on `vela-client` `^0.1.2`) for model-driven form scripting, next to the plugin projects in the same
  solution: `package.json`, `tsconfig.json`, `tsup.config.ts` (the JS global name; imports the entry map and sets
  an `outExtension` so bundles are named `.js`, not `.global.js`), `web-resources.entries.ts` (the entry map,
  maintained by `vela add form-script`), a `.esproj` so Visual Studio "Build" runs npm, Vitest with a co-located
  example (`src/<entity>/Forms/main.ts` + `main.test.ts`) and an `entries.test.ts` guard that every entry exports
  `onLoad`/`onSave`, `.gitignore`, and the `src/generated` (regenerated) vs `src` (hand-written) split.
  `--web-resources-namespace` sets the exposed global (default: the project prefix, lower-cased) and
  `--web-resources-folder` the build output folder (default `dist`). Interactive `vela init` prompts for it (off
  by default).
- `vela models --forms --lang ts` generates TypeScript `FormModel` files for the scoped tables' published Main
  and Quick Create forms, for the vela-client runtime to consume. Each form's FormXML is parsed (validated
  against the official Microsoft `FormXml.xsd`) and joined with the table metadata, emitting one
  `<entity>.<form>.form.ts` const that `satisfies FormModel` plus a shared `option-sets.ts` of numeric enums.
  Field kinds, option sets and lookup targets follow the same mapping as the C# models. Generation is
  deterministic and all-or-nothing, reuses the existing `--solution` / `--entities` scope and connection flags,
  reads published forms only, and disambiguates same-named forms of one table.
- `vela models --json` prints a machine-readable report of the generated files (output directory, count and file
  names) instead of the human summary, on both the C# and `--forms` paths.
- `--json` is now also available on `vela init` (solution, root, file count, project names), `vela sign` (package,
  method, timestamp) and `vela add` (description + created files), so scripts and agents get structured output
  from the whole command surface.

## [0.1.4] - 2026-08-21

CLI line only.

### Fixed

- `vela --help` no longer shows the pre-rename `dataverse-framework` command in its examples, and the header is
  the `Vela CLI` brand instead of the raw `Vela.Cli` assembly name. The top-level usage is now rendered through
  Spectre.Console (teal banner and aligned command list in a terminal, plain text when redirected) and lists
  every `add` subcommand (`plugin`, `service`, `customapi`, `test`, `step`). Internal temp directories are named
  `vela-tools` rather than `dataverse-framework-tools`.
- Bumped the pinned `System.Security.Cryptography.Xml` from `8.0.3` to `8.0.4`. `8.0.3` had picked up its own
  advisories (NU1903); `8.0.4` is the latest 8.x servicing patch and clears them.

## [0.1.3] - 2026-08-20

CLI line only.

### Changed

- `vela init` resolves the latest published `Vela.Plugins` version from nuget.org at scaffold time (falling back
  to a built-in default when offline), so scaffolded projects track the framework without a CLI release per
  framework version. `--core-version` still pins a specific version.

### Fixed

- Removed em dashes from the scaffold-generated README and code comments (house style is hyphens).

## [0.1.2] - 2026-08-20

CLI line only; the framework packages are unchanged.

### Fixed

- `vela init` scaffolded projects referencing `Vela.Plugins`/`Vela.Testing` `1.0.0`, which does not exist (the
  published framework is `0.1.x`), causing NU1102 restore errors. The scaffold default (`--core-version`) is now
  `0.1.1`.
- Scaffolded Shared/ConsoleApp projects now pin a patched `System.Security.Cryptography.Xml` over the Dataverse
  SDK's advisory transitive version (NU1903).

## [0.1.1] - 2026-08-20

### Fixed

- Package READMEs now render as plain markdown on nuget.org. The `<img>` logo and `<br>` tags were raw HTML,
  which the gallery escapes to literal text; removed them (the package icon already provides the branding).
  Code is identical to 0.1.0.

## [0.1.0] - 2026-08-20

First public release, published to NuGet.org as `framework-v0.1.0` (`Vela.Plugins` + `Vela.Testing`) and
`cli-v0.1.0` (`Vela.Cli`). Everything below ships in this release.

### Added

**Runtime (`Vela.Plugins`)**

- Fluent plugin/Custom API base types (`EntityPluginBase<T>`, `CustomApiBase`), strongly-typed context helpers,
  lightweight DI and caching.
- **Step lifecycle** - `.Disabled()` marker (steps default to enabled) and `.UnsecureConfig(...)` /
  `.SecureConfig(...)`, all reconciled by `vela plan`/`apply` (statecode transition + configuration). (#40)
- **Typed attribute/context helpers** - `Contains`/`ContainsAny` (attribute presence), `HasChanged`/
  `HasAnyChanged` (Target vs registered pre-image), and `GetFormattedValue`. (#49, #50)
- **Typed FetchXML** - `Query.For<T>()` (and any `QueryExpression`) now serializes to a FetchXML string with
  `ToFetchXml()`, for the messages/tools that require FetchXML (Rollup, saved queries, pasting from FetchXML
  Builder). The query is authored typed (compile-time IntelliSense + typo-safety, editor-agnostic), so a
  table/column typo is a build error. FetchXML-only extras (emit-only) via `FetchXmlOptions` and builder knobs:
  `LateMaterialize()`, `AggregateLimit(n)`, `CountColumnDistinct(…)`, `OrderByAggregate(alias)`, `UtcDateGrouping()`,
  `RowAggregate(…)`, `RawChoiceOrder()`, `FromRetainedData()`, `SqlOptions("…")`. `TracingOrganizationService` now
  logs the FetchXML string of a `FetchExpression`.
- **Shared-variable helpers** - `ctx.Tag`, `ctx.GetSharedVariable(key)` and `ctx.TryGetSharedVariable<T>(...)`
  read the client-supplied `tag`, a value set by an earlier plugin, or one inherited from the parent context.
- **File/Image column helpers** - `IOrganizationService.UploadFile(...)`, `DownloadFile(...)` and `DeleteFile(...)`
  (`using Vela.Files;`) drive the platform's block upload/download protocol in one call, so a plugin can read or
  write a whole file without chaining `InitializeFileBlocksUpload`/`UploadBlock`/`CommitFileBlocksUpload` (and the
  download/delete equivalents) by hand. Issued by message name, so they work against a live org and the test harness.
- **Bulk step registration** - `OnCreateMultiple()` / `OnUpdateMultiple()` register a step on the bulk
  `CreateMultiple`/`UpdateMultiple` messages (filtering attributes required, same as Create/Update). The step
  reads the whole batch via `ctx.BulkTargets` and per-row images via `ctx.BulkPre/PostEntityImagesCollection`.

**CLI (`vela`)**

- **`vela add`** scaffolds a plugin, **service** (#41), Custom API, **test** (#44) or adds a **step** to an
  existing multi-step plugin (#45). `--message` accepts every message with a dedicated builder method
  (Create/Update/Delete/Retrieve/RetrieveMultiple/Associate/Disassociate/SetState) plus arbitrary custom SDK
  messages via `OnMessage("...")` (#41); `--function`/`--action` and `--bound`/`--global` flags for Custom APIs
  (#46); a guided `vela add --interactive` wizard and an "add" entry in the bare-`vela` menu (#47).
- **`vela models`** - home-grown early-bound generator over Vela's existing Dataverse connection (single auth):
  scope by `--solution` (from the solution's components) and/or `--entities`. Emits `partial` classes with the
  primary id synced to `Entity.Id`, XML doc, DataAnnotations (`[StringLength]`/`[Range]`), self-contained
  `[RequiredLevel(...)]` (the full four-value Dataverse required level, not a lossy boolean) and
  `[LookupTargets(...)]` (a lookup / Customer / polymorphic column's target tables) facet attributes, option-set
  enums (local nested + global shared), multi-select choices as `IList<Enum>`, and alternate-key reference
  factories; `--custom-apis` generates typed `OrganizationRequest`/`OrganizationResponse` message classes. (#51-#56)
- **`vela models --roles`/`--solution-roles`** - generates a `Roles` class (for the **test project**) from the
  environment's security roles: per role, its `Name`, root `Id` (roles are duplicated per business unit - the root
  role's id) and a configurator that reuses the harness `Role(name, id, RoleBuilder)` API. Maps each role
  privilege's depth mask → `AccessLevel` and verb → `Privilege`, deduping to the root role and skipping non-table
  privileges, so tests configure the harness with the **real** role privilege configuration. (#24, #25, #26)

- **Stale-assembly-version guard** - `vela apply` fails fast when an apply would register a **new plugin type**
  under an **unchanged assembly version**, which Dataverse would silently break at runtime with *"The plug-in type
  could not be found in the plug-in assembly"* (it caches assemblies by version). The error tells you to bump
  `AssemblyVersion` and rebuild. Projects scaffolded by `vela init` now **auto-increment `AssemblyVersion`**
  (`1.0.*`) so the cache is never stale.

**Test harness (`Vela.Testing`)**

- In-memory `IOrganizationService` with a real `RetrieveMultiple` (filters, link-entities, ordering, paging,
  Distinct and a FetchXML subset incl. aggregates), and `Execute` handling Create/Update/Delete/Retrieve/
  RetrieveMultiple, `CreateMultiple`/`UpdateMultiple`, Assign/Associate/Disassociate, and
  `ExecuteMultiple`/`ExecuteTransaction` (with rollback). (#30, #31, #48)
- **In-memory metadata** - `WithEntityMetadata(...)` + `FakeEntityMetadata`; N:N `Associate`/`Disassociate` via
  `WithManyToMany(...)` (+ a strongly-typed overload) or derived from registered metadata. (#33, #35, #36, #48)
- **Metadata from the early-bound type** - `FakeEntityMetadata.From<TEntity>()` (and the
  `WithEntityMetadata<TEntity>()` shorthand) derives table metadata by reflecting over a class generated by
  `vela models`: `[AttributeLogicalName]` properties → attributes (CLR type → `AttributeTypeCode`), generated
  option-set enums → picklists, the `{logicalname}id` column → primary id, a `name` column → primary name,
  ownership inferred from an `ownerid` column, and max length, numeric range, required level and lookup targets
  recovered from the generated `[StringLength]` / `[Range]` / `[RequiredLevel]` / `[LookupTargets]` annotations
  (numeric range coerced to each attribute's value type). No connection and no hand-written metadata; chain
  `On(...)` then the facet setters to override anything else. Multi-select/file/image/partylist columns are
  skipped (declare them with `For(...)` when needed).
- **File/Image column block protocol** - `Execute(...)` handles the file messages over an in-memory blob store
  keyed by fileid: `InitializeFileBlocksUpload`/`UploadBlock`/`CommitFileBlocksUpload`,
  `InitializeFileBlocksDownload`/`DownloadBlock`, and `DeleteFile`. A committed File column stores its fileid
  (content fetched via download); an Image column holding bytes inline is downloaded directly. Matched by
  `RequestName` (call with a plain `OrganizationRequest`), since these have no typed request class in the SDK.
- **System columns on create** - audit, owner columns, `organizationid`, `statecode`/`statuscode`,
  `versionnumber`, `overriddencreatedon`; `AssignRequest`; ownable elastic tables; a stable default calling
  user; opt-in `AsVirtual(readOnly: true)`. (#31, #42, #48)
- **Deterministic fixtures** - injectable clock (`WithClock(...)`) and id sequence (`WithIdSequence(...)` +
  `SequentialIds`). (#31, #42)
- **Shared variables / `tag`** - `Service.Execute(CreateRequest/UpdateRequest/DeleteRequest/Associate/Disassociate)`
  now runs the registered steps and promotes the request `tag` to a shared variable visible to every plugin in
  the operation (and inherited by cascaded writes); `PluginContextBuilder.WithTag(...)` sets it in unit tests.
- **Owner/state Update split** - an `Update` carrying `ownerid` or `statecode`/`statuscode` alongside standard
  fields now fires the Update step **twice** (standard fields, then owner/state fields), like the platform.
  `Service.Execute(SetStateRequest)` is mapped to a `statecode`/`statuscode` Update so Update plugins run (the
  status defaults from the state when omitted), mirroring how `AssignRequest` is modelled as an `ownerid` update.
- **Bypass parameters** - `Service.Execute(...)` honours `BypassCustomPluginExecution` (skip every step),
  `BypassBusinessLogicExecution` = `CustomSync`/`CustomAsync` (skip by execution mode) and
  `BypassBusinessLogicExecutionStepIds` (skip specific steps, matched against the step key). The data is still
  written; only the plugins are skipped. Bypass applies to the whole top-level operation and its cascade.
- **Bulk pipeline firing** - `Service.Execute(CreateMultipleRequest/UpdateMultipleRequest)` now runs the
  registered steps as one transaction, mirroring how Dataverse merges the single- and multiple-operation
  messages: single `Create`/`Update` steps fire once per row, and `OnCreateMultiple()`/`OnUpdateMultiple()` steps
  fire once with the whole `Targets` collection (and per-row pre/post image collections). The merge runs both
  ways - a bulk step also fires for a plain `Create`/`Update` (a one-row batch). `UpdateMultiple` filtering is
  evaluated per row for single steps and "any row matches" for a bulk step.
- **Opt-in security model** - `WithSecurity(...)` declares business units (with hierarchy), named roles, users,
  teams and membership; the service then enforces the table privileges (Create/Read/Write/Delete/Assign/Append/
  AppendTo/Share) at **User/BusinessUnit/ParentChild/Organization** depth over the user and every team they belong
  to. A denied write throws `FaultException<OrganizationServiceFault>` (`PrincipalPrivilegeDenied`), and
  `RetrieveMultiple` - including aggregates - is security-trimmed before paging. Record **sharing** (PoA) via
  `GrantAccess`/`ModifyAccess`/`RevokeAccess` (clamped to the sharer's own rights), with `RetrievePrincipalAccess` /
  `RetrieveSharedPrincipalsAndAccess` and a `WithSharedRecord(...)` arrange helper. Changing `ownerid` (Assign or a
  plain Update) needs the **Assign** privilege and moves the owning business unit; setting a lookup needs
  **Append/AppendTo**. **1:N relationship cascades** via `WithParentalRelationship(...)` /
  `WithReferentialRelationship(..., restrictDelete:)`: cascade **Delete** (Cascade/RemoveLink/Restrict), cascade
  **Assign**, cascade **Share/Unshare**, and **inherited access** (in a parental relationship a child inherits the
  caller's access to its parent). **Off by default** - tests that never call `WithSecurity(...)` behave exactly as
  before (full access). (#17, #18, #19, #21, #23) Team **Member's-privilege-inheritance** mode via
  `Team(id, bu, TeamPrivilegeInheritance.TeamOnly, roles)`.
- **Role membership is queryable** - with security configured, the model materializes the platform's membership
  tables (`role`, `systemuserroles`, `teamroles`, `teammembership`) into the store, so a plugin can resolve a
  user's roles the way it does against Dataverse (`RetrieveMultiple` over `systemuserroles`, or a join to `role`).
  `Role(name, id, RoleBuilder)` carries the role's real id (e.g. a generated `Roles.XId`); these tables are always
  readable. Pairs with `vela models --roles` for real, generated role configurations. (#26)
- **Access teams, hierarchy and field-level security** - `WithAccessTeamTemplate(...)` + `AddUserToRecordTeam` /
  `RemoveUserFromRecordTeam` grant a template's rights on a record; `ManagerHierarchy(depth)` + `Manager(...)` and
  `PositionHierarchy(depth)` + `Position(...)` / `PositionParent(...)` give a higher user read/write on a direct
  report's records and read-only deeper (within depth) - the manager model within the report's BU or its parent,
  the position model across business units - reaching a report whether it owns the record, its team owns it, or the
  record is shared to it; `SecureColumn(...)` + `FieldPermission(...)` hide a field-secured column on read and
  reject it on write without permission (user or team). (#28, #29, #30)
- **Link-entity read-trimming** - a `RetrieveMultiple` that joins (`LinkEntity`) to a record the caller can't read
  now trims the join like Dataverse: an **inner** join drops the primary row, a **left-outer** join keeps it but
  omits the linked columns, applied recursively through nested joins (and to `Any`/`All` related-record filters).
  Only active when security is configured.

**Docs**

- Usage-first README, `CONTRIBUTING.md`, `docs/LIMITATIONS.md`, `BACKLOG.md` (#30, #32, #37), and this
  `CHANGELOG.md` with a documented versioning policy (#43).

### Changed

- **A plugin registering two steps with the same trigger now fails fast.** When more than one step shares the
  same message + stage + mode + entity, `GetStepDefinitions()` throws a clear error (they can't be told apart at
  runtime, and their auto step-keys would otherwise collide). Merge them into one step or use a different
  stage/mode. (Internal: the CLI's `SdkConstants.PipelineStage` values were realigned to the platform's
  10/20/30/40 - no behavior change. They now **derive** from the public `Vela.Constants.PluginStages` enum
  (linked into the CLI as source) so the two definitions are a single source of truth and can't drift.)
- **Create/Update steps now require filtering attributes.** `OnCreate()`/`OnUpdate()` route through a
  filtering-required builder chain, and `Build()` throws if no filtering attribute is declared (an unfiltered
  Create/Update step fires on every change - list the attributes that should trigger it). `FilteringAttributes`
  is no longer offered on messages where it doesn't apply (Delete, etc.). `Build()` also rejects duplicate
  filtering attributes, duplicate image attributes, and duplicate image aliases. `vela init` / `vela add`
  generate the filtering call accordingly.
- **`Vela.Cli` and the scaffolded `Shared`/`ConsoleApp` projects target .NET 10** (current LTS) instead of
  .NET 8. `Vela.Plugins` / `Vela.Testing` and generated plugin/test projects stay on `net462` (Dataverse sandbox).
- **`vela init` layout** - the generated solution now puts the `.sln` and `Directory.Build.props` at the
  **root**, with `src/` (Shared/Plugins/Integrations/ConsoleApp) and `tests/` (test projects) as siblings,
  matching the conventional `src`/`tests` split (test projects moved out of `src/`).
- `EntityPluginBase<T>` now resolves a strongly-typed target for **every** message - a full entity for
  Create/Update, an id-only entity from the `EntityReference` for Delete/Associate/Disassociate/Retrieve (and
  from `EntityMoniker` for SetState), and an empty typed entity for messages with no primary record. (#41)
- `vela plan`/`apply` and `vela models` page through all records (registration reads, solution components,
  Custom API parameters) instead of capping at one 5000-row page. (#34, #58)
- (Internal) The live step read and the local step definition now build their plan/apply diff shape through a
  **single shared builder** (`DefinitionNormalizer.BuildStepShape`) that owns the field set, field order and
  image ordering - so the two sides can't drift and silently break idempotence (the bug class fixed in #68).
  No behavior change.
- Sample/scaffold namespace renamed `Contoso` → `Zava`. (#30)

### Fixed

- **`vela plan`/`apply` now fail fast when a plugin can't be introspected.** If a discovered plugin/Custom API
  type throws from `GetStepDefinitions()`/`GetCustomApiDefinition()` (or lacks the expected constructor/method),
  the scan no longer demotes it to a soft diagnostic and continues - which left the type contributing no local
  steps and made the diff mark its existing live registrations for **deletion** unless `--strict` was set. It now
  hard-fails with an actionable message and a parse exit code.
- Delete/Associate/Disassociate/RetrieveMultiple/SetState plugins no longer throw at runtime when the message
  carries no `Entity` Target. (#41)
- `vela models` no longer truncates a large solution's components or a Custom API's parameters at 5000 rows. (#58)
- `vela models` now types **ActivityParty** columns (partylists, e.g. email `from`/`to`/`cc`) as `EntityCollection`
  instead of `object`. (Polymorphic / `Customer` / `Owner` lookups remain `EntityReference` - the correct
  early-bound type; they can't be strong-typed to a single table.)
- `vela models` now types **File** columns as `Guid?` (the `fileid`) and **Image** columns as `byte[]` (the
  thumbnail), detected via `FileAttributeMetadata`/`ImageAttributeMetadata` instead of falling through to `object`.
- **`HasChanged` now detects partylist (`EntityCollection`) changes.** They were compared via `ToString()`,
  which returns the type name for both sides, so a changed partylist was always reported as unchanged.
- **`vela plan`/`apply` are now idempotent for asynchronous and unranked steps.** The local diff shape applies
  the same coercions Dataverse performs on write - async steps to PostOperation (stage 40), rank clamped to ≥ 1 -
  so a re-`plan` after `apply` reports no change (previously every run re-wrote the step, and async steps without
  an explicit step key were deleted and recreated).
- **`vela plan`/`apply` no longer silently treat a failed Custom API read as "no Custom APIs"**, which made the
  diff recreate APIs that already existed; the read error now surfaces with a connection/parse exit code.
- Plugin config deserialization now raises `InvalidPluginExecutionException` for malformed JSON instead of
  leaking a raw `Newtonsoft.Json` exception to the platform.
- Step definition build is now thread-safe, so the Dataverse sandbox concurrently reusing a cached plugin
  instance can no longer corrupt the one-time step registration on first execution.
- `vela models` now writes all files or none (it pre-checks for existing output before writing) and returns
  the same typed exit codes as `plan`/`apply` (connection/file-not-found/parse) instead of a generic code.

**Testing (`Vela.Testing`)**

- Negative query operators (`NotEqual`/`NotIn`/`NotBetween`/`DoesNot…`/`NotLike`) now exclude rows whose
  attribute is null, matching Dataverse's three-valued logic (previously they matched null rows).
- **Ordered comparisons (`GreaterThan`/`GreaterEqual`/`LessThan`/`LessEqual`/`Between`) on a null attribute no
  longer throw** *"cannot compare the supplied values"* - a null value is now treated as non-matching (SQL
  three-valued logic), so e.g. a numeric `gt`/`lt` filter (including via FetchXML) over a table with some null
  values returns the right rows instead of failing.
- `Create`/`Update`/`Retrieve` now deep-copy attribute values, so mutating a retrieved record (or the entity
  passed to a write) no longer aliases and corrupts the in-memory store.
- `Update` now always server-stamps `modifiedon`/`modifiedby`/`versionnumber`, ignoring any caller-supplied
  value, as Dataverse does. (Use `WithClock` to pin timestamps deterministically.)
- The `avg` aggregate over a group with no values now returns `null` (as Dataverse does, and consistent with
  `min`/`max`) instead of `0`.
- Alternate-key values on a cloned `EntityReference` are now deep-copied, so mutating a retrieved record's key
  value no longer aliases and corrupts the in-memory store.

### Security

**CLI (`Vela.Cli`)**

- **Secure step configuration is redacted in plan output.** Secure config can hold secrets; it is now hashed in
  the `plan`/`inspect` JSON (and any `-o` plan file) instead of shown verbatim - changes are still diff-detected,
  but the value never appears. Unsecure config remains plaintext (non-sensitive by design).
- `--client-secret-stdin` now requires stdin to be **piped**; on an interactive terminal it refuses rather than
  reading a typed (and echoed) secret.
- `vela sign` redirects the child `dotnet nuget sign` output (surfaced only on failure) and wipes the decoded
  `.pfx` bytes from memory after writing the temp certificate.
- Pinned a patched `System.Security.Cryptography.Xml` over the vulnerable transitive version (NU1903).
- Documented that `plan`/`apply` **run code from the scanned package** (plugin types are instantiated to read
  their definitions) - only scan packages you build or trust. See the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).
