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

- Ribbon wiring foundation: a pure `RibbonDiffXml` generator turns a `RibbonRegistration` (commands + enable/
  display rules) into classic ribbon XML with `JavaScriptFunction` actions and `CustomRule` rules, each
  referencing its command web resource via `Library="$webresource:<name>"` (the web-resource solution
  dependency). Output is validated against the official Microsoft Ribbon XSDs. Deploying the generated wiring
  (modern `appaction` / solution import) is a follow-up.
- `vela plan` / `vela apply --webresources <folder>` deploy web resources (form scripts, CSS, images, HTML, ...)
  as a third registration kind, mirroring the folder verbatim: each file's unique name is its path relative to the
  root (forward slashes), the type is inferred from the extension, and a content hash drives the diff. Changed and
  created resources are published with `PublishXmlRequest`. Web resources reconcile independently and additively
  (a plugin-only run never touches them and vice versa; deletions are scoped to the web-resource set); a
  `--webresources` run needs no `--package`; and each name is validated to carry the solution's publisher prefix.
- `vela init --web-resources` additionally scaffolds an opt-in `{prefix}.WebResources` TypeScript project for
  model-driven form scripting, next to the plugin projects in the same solution: `package.json`,
  `tsconfig.json`, `tsup.config.ts` (bundler config with the JS global name and one entry per form), a `.esproj`
  so Visual Studio "Build" runs npm, Vitest, `.gitignore`, and the `src/generated` (regenerated) vs `src`
  (hand-written) split. `--web-resources-namespace` sets the exposed global (default: the project prefix,
  lower-cased) and `--web-resources-folder` the build output folder (default `web`). Interactive `vela init`
  prompts for it (off by default).
- `vela models --forms --lang ts` generates TypeScript `FormModel` files for the scoped tables' published Main
  and Quick Create forms, for the vela-client runtime to consume. Each form's FormXML is parsed (validated
  against the official Microsoft `FormXml.xsd`) and joined with the table metadata, emitting one
  `<entity>.<form>.form.ts` const that `satisfies FormModel` plus a shared `option-sets.ts` of numeric enums.
  Field kinds, option sets and lookup targets follow the same mapping as the C# models. Generation is
  deterministic and all-or-nothing, reuses the existing `--solution` / `--entities` scope and connection flags,
  reads published forms only, and disambiguates same-named forms of one table.
- `vela models --json` prints a machine-readable report of the generated files (output directory, count and file
  names) instead of the human summary, on both the C# and `--forms` paths.

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
