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

The CLI-line entries (Added / Changed / Fixed) and the Framework-line entries release on their separate tags
(`cli-vX.Y.Z` / `framework-vX.Y.Z`).

### Changed (Framework)

- **Typed image projections are now nested in the plugin's `partial` class with a short name (breaking).** The
  generated projection type is emitted as a nested type inside the plugin class and named
  `{Handler}_{Pre|Post}Image`, instead of `{PluginClass}_{Handler}_{Pre|Post}Image` in a separate
  `<Namespace>.Generated.Images` namespace exposed via a `global using`. Read it unqualified, for example
  `ctx.PostImageAs<OnUpdated_PostImage>()`. The lambda-handler fallback likewise drops the plugin prefix
  (`{Message}{Stage}_{Pre|Post}Image`), and the `Generated.Images` namespace and its `global using` are removed.
  **Migration:** drop the `{PluginClass}_` prefix at each `PreImageAs`/`PostImageAs` read site and declare the
  plugin class `partial` (see VELA014).

### Added (Framework)

- **VELA014** (analyzer + code fix, ships in `Vela.Plugins`): a plugin that captures a typed image
  (`WithPreImage`/`WithPostImage` with column selectors) must be declared `partial`, so the generated nested
  projection can reopen the class. Without `partial` the build otherwise failed with an obscure `CS0260`; the code
  fix adds the modifier.
- **Typed step contexts.** For a named handler on a Create/Update/Delete step that captures a typed image, the
  generator now also emits a `{Handler}Context` (a subclass of the step's message context) exposing typed
  `ctx.PreImage` / `ctx.PostImage` members. A handler can take `void OnUpdated(OnUpdatedContext ctx)` and read the
  images as members instead of `ctx.PreImageAs<T>()`; `.Handle(OnUpdated)` stays type-free (a new `Handle(Delegate)`
  overload binds the method group via its natural type). Also on the typed `On<TRequest>()` path, where the context
  derives from the generic message context (`MessagePreContext<TRequest, TEntity>` / `MessagePostContext<...>`) so
  non-CRUD messages get the same `ctx.PreImage` / `ctx.PostImage`. Additive and opt-in: the plain context and
  `ctx.PreImageAs<T>()` keep working. Guard rails: **VELA015** (the handler's context must match its step; with a code
  fix) and **VELA016** (a typed-context handler must bind a single step).

### Changed (CLI)

- Scaffolded plugins that capture typed images are now generated `partial` and read the short nested projection
  name (`ctx.PostImageAs<OnAccountCreated_PostImage>()`). Scaffolded lightweight Custom APIs bind their handler via
  a method group (`RegisterCustomApi("...", Handle)`) instead of `nameof(Handle)`.

## [0.26.2] - 2026-09-24 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.26.2`. Correctness and test-harness fidelity hardening from the code
review and a live-environment test campaign.

### Fixed

- **Associate / Disassociate steps now match at runtime regardless of primary entity.** A synchronous step
  registered with `OnAssociate()` / `OnDisassociate()` on an `EntityPluginBase<TEntity>` threw "no registered step
  matching message 'Associate'" because these relationship-global messages carry no primary entity matching the
  step's `TEntity`. The step matcher now skips the entity check for these messages. (Async steps hid this; a sync
  step failed the operation. Found on a real org.)
- **Brace-safe tracing and logging** (`LocalPluginContext`): `ctx.Trace(...)` / `Log(...)` of text containing
  `{` or `}` (for example a serialized snapshot) no longer throws `FormatException`; `Log` also falls back to the
  literal template on an argument/placeholder mismatch instead of throwing.
- **Test-harness fidelity** (`Vela.Testing`): full transaction rollback (records + sharing + access-team grants +
  file blobs + version); `ExecuteMultiple` preserves the fault `ErrorCode` (store and pipeline paths);
  `Include` honours the child query's filter/order/paging; `Associate` / `Disassociate` validate record existence;
  create-on-behalf-of is depth/BU-checked; ordered filters with a null operand yield no match instead of throwing;
  and multi-level deep insert no longer drops grandchildren. Backed by new parity ("fidelity") test suites.

## [0.15.2] - 2026-09-24 (Vela.Cli)

CLI line, published as `cli-v0.15.2`. Security and correctness hardening from the code review.

### Fixed

- The decoded PFX signing certificate written to a temp file and the interactive MSAL token-cache file are now
  restricted to the owner (0600) on macOS/Linux; the config directory is created 0700 on every write path.
- `ResolveCustomApiTypeCode` uses exact `String` / `Entity` element-type matching instead of a substring check, so
  a collection of `EntityReference` is no longer mistyped as an entity collection.

## [0.26.1] - 2026-09-23 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.26.1`. Security patch.

### Fixed

- Enable NuGet central **transitive pinning** so the patched `System.Security.Cryptography.Xml` (8.0.4) overrides
  the vulnerable 8.0.2 that `Vela.Messages` pulled in transitively (GHSA-37gx-xxp4-5rgx and related, High
  severity). A consumer of `Vela.Messages` no longer resolves the vulnerable transitive package. (`Vela.Cli` was
  already patched through a direct reference.)

## [0.26.0] - 2026-09-23 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.26.0` (all three packages ship at this version). Custom API calling
fix plus runtime, harness and analyzer hardening from the maturity review.

### Changed

- **Custom API parameter naming.** Generated typed Custom API request/response classes now key their parameter
  and result bags by the plain parameter name (for example `Pong`) rather than a dotted, fully-qualified name.
  This matches how Dataverse names Custom API parameters and is what makes a Vela-created Custom API callable on a
  real environment (see Fixed). The typed property bodies are unchanged, so code that reads `response.Pong` keeps
  working; only the underlying bag key changed.

### Fixed

- **Custom APIs are callable on Dataverse.** The plugin runtime (`CustomApiBase`) and the typed message bases in
  `Vela.Messages` previously read inputs and published outputs under dotted keys (`{api}.{name}` and
  `{api}.{name}.response`); Dataverse rejects a dot in a parameter or response identifier, so a Custom API created
  by Vela could not be invoked. Inputs and outputs now flow under the plain parameter name.
- The tracing service no longer mutates the caller's `EntityCollection` when down-casting early-bound parameters
  (it builds a new collection of copies).
- The typed image projector traces an attribute whose runtime value type does not match the projection property
  instead of silently dropping it.
- The relationship hydrator skips (and traces) a navigation property that is not writable instead of throwing a
  raw reflection error.
- **Test harness fidelity** (`Vela.Testing`): `ExecuteMultiple` preserves the fault `ErrorCode` on per-item
  failures; `Include` honours the child query's filter/order/top/paging; `Associate`/`Disassociate` validate that
  the target and related records exist; intersect ids are deterministic; deep insert clones the caller's child
  entities before stamping the parent key; `RelationshipRegistry.Add` validates its arguments.
- **Analyzers**: VELA013 also flags target-typed `new(true)` and `new ColumnSet { AllColumns = true }`; VELA010
  unwraps an `as` cast; the image-projection generator no longer collapses two projections that share a handler
  type name but capture different columns; and several code-fix trivia and fix-all edge cases were corrected.

## [0.15.1] - 2026-09-23 (Vela.Cli)

CLI line, published as `cli-v0.15.1`. Makes the Custom API dot-free-name self-heal actually trigger on upgrade.

### Fixed

- The Custom API parameter/response `uniquename` is now part of the plan/apply diff. A Custom API deployed by a
  pre-0.15.0 CLI (with dotted, uncallable names) is otherwise byte-identical in the diff, so `apply` reported "no
  change" and never ran the self-heal. The diff now compares the live `uniquename` against the expected bare name,
  so a drifted API is detected as changed and reconciled (its parameters recreated dot-free) on the next `apply`;
  a correctly-named API does not diff. Verified end to end on a real environment (a re-applied Custom API becomes
  callable and round-trips through its plugin).

## [0.15.0] - 2026-09-23 (Vela.Cli)

CLI line, published as `cli-v0.15.0`. Configurable connection timeout, the Custom API naming fix at apply time,
and quality cleanups.

### Added

- `--timeout <minutes>` on every connected command (`plan` / `apply` / `inspect` / `models`): sets the Dataverse
  request timeout. The default is raised to **10 minutes** (from the SDK default of 4) so a large environment or a
  plugin sandbox cold start does not abort the run; override it up to 60 minutes for extreme cases.

### Fixed

- **Custom API registration is callable.** `vela apply` writes Custom API request-parameter and response-property
  unique names as plain, dot-free identifiers (matching Dataverse's own convention), so the Custom APIs it creates
  can be invoked. A Custom API previously deployed with the old dotted names self-heals (the affected parameter is
  recreated) on the next apply.
- The execution-order (rank) collision scan is scoped to the touched entities when every touched coordinate is
  entity-bound, reading far fewer rows on a large org.
- Generated model file names are sanitised and the resolved path is asserted to stay under the output directory.
- The alternate-key factory method name is escaped after composition, so a keyword schema no longer produces a
  malformed identifier.

### Changed

- Internal de-duplication of package-path resolution, positional / `.csproj` resolution, and temp-directory
  cleanup across the CLI commands (no behavioural change).

## [0.25.0] - 2026-09-22 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.25.0` (all three packages ship at this version). Three plugin-authoring
analyzers and a code-fix suite.

### Added

- Three plugin-authoring analyzers: **VELA011** (warning) a writable field (mutable state) on a plugin type - the
  sandbox reuses instances, so it leaks state and races; **VELA012** (warning) throwing an exception other than
  `InvalidPluginExecutionException` from a plugin, which hides the message from the caller; **VELA013** (warning)
  `new ColumnSet(true)` retrieving every column.
- IDE **code fixes** for VELA003, VELA004, VELA005, VELA006, VELA007 and VELA012, in a new `Vela.CodeFixes`
  assembly bundled inside `Vela.Plugins` alongside the analyzers (no extra reference). For example VELA006 rewrites
  `OnMessage("create")` to `OnCreate()`, and VELA012 changes the thrown type to `InvalidPluginExecutionException`.

## [0.24.0] - 2026-09-22 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.24.0` (all three packages ship at this version). Completes typed
relationships (#207) and adds alternate-key read/upsert.

### Added

- Typed deep insert: `service.CreateWithRelated(entity)` (in `Vela.Context`) creates a record together with the
  related records held in its generated 1:N navigation collections, in one transaction, so you never hand-write a
  `Relationship` schema string. The in-memory test harness supports the deep insert (declare the relationship with
  `WithRelationship`/`WithParentalRelationship`).
- Typed relationship-aware read: `service.RetrieveWithRelated<TEntity>(target, q => q.Include(o => o.Nav), columns)`
  (in `Vela.Context`) retrieves a record together with related records (N:1 parent, 1:N children, and N:N members)
  in one call and hydrates the generated navigation properties. Single-record only (Dataverse nests related records
  on `Retrieve`, one level deep, 5000-row child page cap). The in-memory harness now honors `RelatedEntitiesQuery`
  on retrieve (1:N, N:1 and N:N).
- Typed N:N associate: `service.AssociateRelated(entity)` (in `Vela.Context`) associates the existing records held
  in the generated N:N navigation collections (an N:N link connects records that already exist). `CreateWithRelated`
  ignores N:N collections; it only deep-inserts 1:N children.
- New analyzer **VELA010** (error): a generated relationship navigation property used in a column selector
  (`FilterOn`/`Select`/`Where`/`OrderBy`/`WithPreImage`/`WithPostImage`). The runtime column resolvers now also
  reject a relationship property with a clear error instead of silently building a bogus column.
- Read and upsert by alternate key (in `Vela.Context`): `service.RetrieveByKey<TEntity>(keyRef, columns...)`
  retrieves the record a key-based `EntityReference` points at, early-bound, without knowing its GUID; and
  `service.UpsertByKey(entity, keyRef)` create-or-updates by business key in one call, returning
  `(Created, Target)`. The in-memory test harness now resolves key-based references and executes Upsert
  (create-or-update matched by primary id or alternate key).

## [0.14.0] - 2026-09-22 (Vela.Cli)

CLI line, published as `cli-v0.14.0`. Generation for typed relationships (#207) and typed alternate keys.

### Added

- Typed relationship navigation properties in generated early-bound models (issue #207). `vela models` fetches
  relationship metadata and emits, in the same class file, a typed N:1 parent (single-target lookups only), typed
  1:N child collections, and typed **N:N** collections (marked `ManyToMany`), each annotated with
  `[RelationshipSchemaName(...)]`. A relationship whose target table is not in the generated set, polymorphic /
  Customer / Owner lookups, and self-referential N:N are skipped (kept as the raw `EntityReference`, or no nav).
- Alternate-key factories are now **strongly typed**: `Account.ByAccountNumberKey(...)` takes one typed parameter
  per key column (a composite key takes several, in declared order) instead of `params object[]`, so a wrong type
  or arity fails at compile time. Still returns a key-based `EntityReference`.

## [0.23.0] - 2026-09-22 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.23.0` (all three packages ship at this version).

### Added

- Typed image projections now cover the permissive `OnMessage("...")` / `OnMessage<TMarker>()` trigger path, not
  just the guided (`OnCreate()`...) and typed (`On<TRequest>()`) paths. A string-triggered step that captures
  explicit `WithPreImage`/`WithPostImage` columns gets the same compile-safe `ctx.PreImageAs<T>()` /
  `ctx.PostImageAs<T>()` projection. The projection is named from the message (a string literal is capitalized
  into the name segment, a marker type uses its type name); a message passed as a variable is not statically
  known, so the generator skips it and the untyped image is kept.

## [0.22.0] - 2026-09-22 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.22.0` (all three packages ship at this version).

### Added

- New analyzer **VELA007** (error): a pre-image on a Create step, or a post-image on a Delete step, is rejected -
  the message can never provide it. Targets the permissive `OnMessage("create"|"delete")` path (the typed triggers
  already prevent it via the builder type-state); an unmodelled message is left alone.
- New analyzer **VELA008** (error): two steps in one plugin sharing the same trigger (message + stage + mode) are
  flagged, matching the runtime's one-step-per-trigger rule. Only complete, statically determinable chains in
  `RegisterPluginSteps()` are compared.
- New analyzer **VELA009** (error): `HasChanged` / `HasAnyChanged` in a handler whose step registers no pre-image
  will throw at runtime. Flagged only in the unambiguous case (instance-method handler bound to exactly one step
  chain with no `WithPreImage(...)`, default alias); lambdas, shared handlers, and custom aliases are left alone.

## [0.21.0] - 2026-09-22 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.21.0` (all three packages ship at this version).

### Added

- Telemetry: two more default Application Insights dimensions on every log - `PluginType` (the plugin/Custom API
  class) and `Method` (the handler method currently running). A `BeginLogScope(...)` also now indents the plug-in
  trace log lines written inside it (nested scopes indent further), so the trace log reads as a tree; Application
  Insights still receives the scope as dimensions.
- New analyzer **VELA005**: the same column listed twice in a single `FilterOn(...)` / `WithPreImage(...)` /
  `WithPostImage(...)` call is reported as a build error (a repeated column has no effect). Only lambda column
  selectors are compared; a leading image alias string and the same column across different calls are fine.
- New analyzer **VELA006** (warning): prefer the typed step trigger (`OnCreate()`, `OnUpdate()`, ...) over the
  permissive `OnMessage("create")` string form for a message Vela models. Advisory only - `OnMessage("...")` stays
  valid for unmodelled messages, so it is a warning, not an error (only a literal modelled message name is flagged).

## [0.20.0] - 2026-09-21 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.20.0` (all three packages ship at this version).

### Added

- Read Dataverse environment variables from a handler: `ctx.GetEnvironmentVariable("adc_X")`,
  `ctx.GetEnvironmentVariableAs<T>("adc_X")` (primitives/Guid/DateTime via invariant parsing, complex/JSON types
  via JSON), plus `fallback` overloads and `TryGet...` variants for missing values. The effective value is the
  environment variable value when set, otherwise the definition's default, read through the elevated system
  service. Reads are memoized per execution; pass `cacheFor` to cache across executions on hot paths. Secret-type
  variables are not supported (Key Vault) and throw a clear error.
- Leveled, structured telemetry from a handler: `ctx.LogInformation/LogWarning/LogError/LogDebug/LogTrace/LogCritical(...)`
  (with `LogError(exception, ...)`), `ctx.LogMetric(name, value)`, and `ctx.BeginLogScope((k, v), ...)` for custom
  Application Insights dimensions. Entries carry default pipeline dimensions (CorrelationId, MessageName, Stage,
  Mode, Depth, PrimaryEntityName, PrimaryEntityId) plus any scope properties, and route to Application Insights at
  the correct severity when the environment has it configured. Telemetry never throws and always writes to the
  plug-in trace log, so entries survive when Application Insights is not set up. `ctx.Trace(...)` is unchanged
  (equivalent to `LogInformation`). The test harness adds `PluginContextBuilder.WithLogger(...)` and a
  `RecordingLogger` (`builder.LogCapture`) to assert emitted telemetry.
- New analyzer **VELA004**: a step handler passed to `.Handle(...)` must be an instance method, not a `static`
  method (a static handler cannot reach the plugin instance state and does not fit the handler dispatch). Instance
  method groups and lambdas are unaffected. Bundled in `Vela.Plugins`, reported as a build error.

CLI line, published as `cli-v0.13.0`. Framework unchanged (`0.19.0`).

### Fixed

- Interactive browser login (`--interactive`) now persists its token cache **cross-platform**, so a repeat run
  signs in silently on macOS and Linux instead of re-opening the browser every time. The previous cache went
  through the Dataverse SDK's `TokenCacheStorePath`, which is DPAPI-encrypted and Windows-only. The cache is now
  a file under the config dir (`~/.vela`): DPAPI-encrypted on Windows, a user-only file on macOS/Linux.
  `--fresh-login` forgets the cached account, and `vela logout` removes the cache. Applies to every command that
  connects to Dataverse (`plan`, `apply`, `inspect`, `models`), which share the same cache.

## [0.12.0] - 2026-09-21 (Vela.Cli)

CLI line, published as `cli-v0.12.0`. Framework unchanged (`0.19.0`).

### Added

- `vela models --verify` - a read-only drift check that compares the generated early-bound entity model on disk
  against the live Dataverse schema and reports columns removed, retyped, or added in the org, without writing.
  It exits `5` when the model is out of sync (and `0` when it matches), so CI can fail a build on a stale model
  that would otherwise return silent runtime nulls. Supports `--json` for a machine-readable report.

### Changed

- `plan` / `apply` now diff a changed Custom API **per parameter**: request inputs and response outputs that were
  added, removed, or retyped are listed individually (with a before/after type), instead of the whole API showing
  as one opaque change.

### Fixed

- `plan` and `apply` now surface step execution-order (rank) collision warnings in the default terminal
  summary as a `warnings:` block, not only in `--json` output. `--strict` also prints the collision text
  before failing, instead of just a diagnostic count.
- `apply` now converges a Custom API request-parameter / response-property **type change** by deleting and
  recreating just that one parameter (its `Type` column is create-only), instead of failing the apply. Only the
  affected parameter is touched; the Custom API and its other parameters stay in place. Optionality and entity
  binding still reconcile in place.

## [0.19.0] - 2026-09-21 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.19.0` (all three packages ship at this version).

### Changed - breaking

- Typed custom API request/response classes must now derive from `Vela.Messages.CustomApiRequest` /
  `CustomApiResponse` (passing the API unique name to the base constructor) instead of `TypedRequest` /
  `TypedResponse`. `vela models --custom-apis` generates the new form; **regenerate your custom API models** (or
  update hand-written ones) so their parameters bind to the full Dataverse keys. `TypedRequest`/`TypedResponse`
  remain for standard/SDK messages and are unchanged.

### Fixed

- **Custom API parameters now work on a real environment.** Dataverse names a custom API's parameters by their
  full unique name - `{apiUniqueName}.{param}` for request inputs and `{apiUniqueName}.{prop}.response` for
  response outputs - both in the OData operation and in the plugin's `InputParameters`/`OutputParameters`.
  Previously Vela's runtime and typed request/response used the short property name on both sides, so on a real
  environment a custom API handler read `null` inputs and its outputs were dropped (the in-memory harness mirrors
  short names on both sides, so this was invisible in tests). Now the lightweight `CustomApiBase` translates
  short handler names to and from the full Dataverse keys around dispatch, and the new
  `Vela.Messages.CustomApiRequest` / `CustomApiResponse` bases key their bags with the full names while property
  bodies stay short.
- `ctx.UserService` (and the other wrapped services) now downcast early-bound entities to the base `Entity`
  before every `Create`/`Update`/`Execute`, so passing an early-bound entity (for example `new Account { ... }`)
  from plugin code works even when the plugin assembly is not a proxy-types assembly. Previously this failed
  at runtime on a real environment with a `SerializationException` ("The deserializer has no knowledge of any
  type that maps to the name '&lt;namespace&gt;:Account'"). Covers direct `Create`/`Update` and the `Target` /
  `Targets` entities carried by `CreateRequest`/`UpdateRequest`/`CreateMultiple`/`UpdateMultiple` via `Execute`.
- An unhandled exception in a plugin now surfaces its real cause to the caller. The wrapping
  `InvalidPluginExecutionException` message includes the plugin type and the inner exception's type and message
  (for example `SerializationException: ...`) instead of a generic "an unexpected error occurred", and the
  original exception is preserved as the inner exception. The full stack trace is still written to the plugin
  trace log.
- Generated typed image projections declare reference-type columns as nullable (for example `string? Name`).
  A captured image column can be absent or null, and the generated file is `#nullable enable`, so the previous
  non-nullable properties raised `CS8618` in nullable-enabled consumer projects.

## [0.11.0] - 2026-09-21 (Vela.Cli)

CLI line, published as `cli-v0.11.0`. Scaffold default core version bumped to `0.19.0`.

### Added

- **Transparent, headless auth via the ambient Azure credential.** With only `--url` (and no
  `--connection-string`, client-secret flags, or `--interactive`), `plan`/`apply`/`inspect`/`models` now
  acquire a Dataverse token from the ambient Azure credential chain (`Azure.Identity` `DefaultAzureCredential`):
  a local `az login` session, or a managed identity / workload identity in CI - no browser and no flag. A bare
  `vela apply --url https://org.crm.dynamics.com --solution <name>` just works when you are already `az login`-ed
  or running under a managed identity. Explicit `--connection-string` / client-secret / `--interactive` still
  take precedence.

### Fixed

- Scaffolded plugin projects (`{prefix}.Plugins` and `{prefix}.Integrations`) now carry
  `[assembly: Microsoft.Xrm.Sdk.Client.ProxyTypesAssembly]`, the standard belt-and-suspenders for the
  early-bound serialization issue above (also covers early-bound entities nested in deep-insert graphs, which
  the runtime downcast does not reach).
- `vela models --custom-apis` and the scaffolded custom API samples emit request/response types deriving from
  `CustomApiRequest`/`CustomApiResponse` (see the framework change above).
- **`apply` now redeploys a code-only change.** The registration diff only covers steps and custom APIs, so a
  rebuilt assembly with unchanged step/custom-API definitions used to report "No changes detected; nothing
  executed" and leave the old code running. `apply` now also compares the plugin package content against what is
  deployed (read-only, prefix-aware) and re-publishes it when the binary differs, even when the step/custom-API
  diff is empty. A byte-identical package is still skipped.
- **`apply` no longer fails when a plugin package is updated.** Updating an existing `pluginpackage` wrote the
  `version` field, which Dataverse rejects ("PluginPackage version cannot be updated"), failing apply mid-run
  whenever the package version was bumped. The update now writes only the package content; Dataverse derives the
  version from the uploaded `.nupkg`.
- `apply` reports what it executed instead of a misleading "+0 -0 ~0". After "Apply complete." it now prints
  the executed counts (created / updated / deleted, plus a re-published plugin package when applicable) rather
  than the post-apply re-plan summary, which was always empty precisely because the apply had just succeeded.
- `plan`/`apply`/`inspect` now report progress while reading the environment instead of a single static spinner:
  the status updates through phases ("Connecting…", "Reading plugin steps and custom APIs…", "Reading web
  resources…", "Checking execution-order collisions…"). Scripted / redirected runs are unchanged.
- `vela init --prefix Vela` is now rejected with a clear message. It generated a `Vela.Plugins` project whose id
  matches (case-insensitively) the `Vela.Plugins` package it references, which NuGet rejects with "NU1108: Cycle
  detected", so the solution never restored. Choose a different prefix (for example your publisher prefix).

## [0.18.0] - 2026-09-20 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.18.0` (all three packages ship at this version).

### Added

- **Typed per-record images for bulk steps.** `CreateMultiple` / `UpdateMultiple` handlers read each record's
  captured image as the generated projection with `ctx.PreImagesAs<T>()` / `ctx.PostImagesAs<T>()` (an
  `IReadOnlyList<T?>` aligned with `ctx.GetTargets()`), or a single record with `ctx.PreImageAs<T>(index)` /
  `ctx.PostImageAs<T>(index)`. Additive; the raw `BulkPreEntityImagesCollection` / `BulkPostEntityImagesCollection`
  accessors are unchanged.

## [0.17.0] - 2026-09-20 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.17.0` (all three packages ship at this version).

### Changed

- **BREAKING: typed image projection types are renamed to `{PluginClass}_{Handler}_{PreImage|PostImage}`** (for a
  named-method handler; a lambda handler falls back to `{PluginClass}_{Message}{Stage}_{PreImage|PostImage}`), for
  example `AccountAudit_OnUpdated_PostImage`. This makes the type easy to guess from your own class and method, and
  the plugin-class prefix keeps it unique across plugins in the same namespace. Update any `ctx.PreImageAs<...>()`
  / `ctx.PostImageAs<...>()` type arguments written against the previous `{PluginClass}_{Message}{Stage}_...` name.

### Added

- **The image-projection generator emits a `global using`** for each `<Namespace>.Generated.Images` namespace
  (C# 10+; skipped on older `LangVersion`), so completion offers the projection type as you type
  `ctx.PostImageAs<` with no `using` to add. The generated type also carries an explicit XML doc summary.

## [0.16.0] - 2026-09-19 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.16.0` (all three packages ship at this version).

### Added

- **Typed image projections.** Read a step's captured pre/post image as a generated projection with
  `ctx.PreImageAs<T>()` / `ctx.PostImageAs<T>()` (with an optional alias overload), where `T` exposes only the
  columns declared in `WithPreImage(...)` / `WithPostImage(...)`. Reading a column the step did not capture is a
  compile error instead of a silent `null`, and completion is filtered to what was captured. A build-time
  incremental source generator (shipped in the `Vela.Plugins` analyzer package) emits the projection types into
  `<Namespace>.Generated.Images`, named `{PluginClass}_{Message}{Stage}_{PreImage|PostImage}[_{Alias}]`; it is
  additive and skips anything not statically analyzable (you keep the full early-bound image). Works on the guided
  and the typed `On<TRequest>()` paths.
- **Duplicate-modifier analyzer (`VELA003`), shipped in the `Vela.Plugins` package.** Reports an error when a
  single-value step modifier (`FilterOn` or a `With...` setter) is set more than once on the same step.

### Changed

- **A step's single-value modifiers are set-once.** `FilterOn` and the `With...` setters previously overwrote the
  earlier value when called twice, silently dropping it; the builder now throws at registration and `VELA003`
  flags it at build. `Disabled()` stays an idempotent toggle.
- **Step handlers are invoked through the retained delegate**, with the handler context built from the delegate's
  own parameter type - one rule shared by the guided and typed paths.

### Fixed

- **Lambda step handlers that capture local variables now run.** They were previously resolved by method name and
  silently not found; the actual delegate is now invoked directly.

## [0.15.0] - 2026-09-18 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.15.0` (all three packages ship at this version).

### Added

- **Redesigned plugin step builder: `RegisterStep()`.** A guided, type-safe fluent chain -
  `RegisterStep().On<Message>().<Stage>().<Sync()|Async()>().FilterOn(...).With*(...).Handle(handler)` - replaces
  the previous `RegisterSteps()` builder. The message picks the stage set, the stage constrains the mode (async is
  PostOperation-only) and the available images, and the chain must end with `.Handle(...)`. It also covers the
  permissive `OnMessage("...")` path, the typed `On<TRequest>()` path and the async-only `OnAsyncOnly<TRequest>()`
  path (used by the generated Custom API step extensions). Registration knobs are `With*` modifiers (`WithPreImage`
  / `WithPostImage` / `WithDescription` / `WithExecutionOrder` / `WithStepKey` / `WithImpersonation` /
  `WithSupportedDeployment` / `WithUnsecureConfig` / `WithSecureConfig`) plus `Disabled()`.
- **Step-completion analyzer (`VELA002`), shipped in the `Vela.Plugins` package.** A Roslyn analyzer that reports
  an error when a `RegisterStep()` chain does not end with `.Handle(...)`, so an unfinished step fails in the
  editor and at build.
- **Column-selector analyzer (`VELA001`), shipped in the `Vela.Plugins` package.** A Roslyn analyzer that reports
  an error when a Vela column selector (`Expression<Func<TEntity, object>>`, used by `FilterOn`, the pre/post image
  selectors, typed queries and the change/attribute helpers) targets a member declared on
  `Microsoft.Xrm.Sdk.Entity` (for example `LogicalName`, `Id`, `RelatedEntities`) rather than a generated column
  property. It surfaces in the editor and fails the build. It is automatic (no reference or configuration needed)
  and does not touch plain LINQ `Func<...>` lambdas.

### Changed

- **BREAKING: filtering attributes are declared with `FilterOn(...)` (was `FilteringAttributes(...)`).** It is
  named `FilterOn`, not a `With*` modifier, because it is quasi-mandatory on Create / Update / CreateMultiple /
  UpdateMultiple, unlike the optional `With*` image and settings modifiers.
- **BREAKING: every step must bind a handler with `.Handle(...)`.** A single-step plugin no longer overrides
  `Execute(ctx)`; it binds a typed handler just like a multi-step plugin. This is enforced at registration and by
  the `VELA002` analyzer.
- **BREAKING: the step types moved to the `Vela.Steps` namespace.** `StepDefinition`, `ImageDefinition`,
  `ExecutionMode`, `ImpersonationSource`, `SupportedDeployment` (and the builder surface) now live in
  `Vela.Steps` instead of `Vela.StepBuilder`. Change `using Vela.StepBuilder;` to `using Vela.Steps;`.

### Removed

- **BREAKING: the legacy `RegisterSteps()` builder is removed.** Use
  `RegisterStep().On<Message>()...Handle(...)`. The old fluent surface (`IStepBuilder<TEntity>`, the per-message
  stage interfaces, `IModeSelector` / `IFilteringSelector` / `IStepConfig`, and the permissive / typed-message
  interfaces) is gone.
- **BREAKING: `Register.RegisterPluginStep<TEntity>()` is removed.** Register steps from a plugin's
  `RegisterPluginSteps()` with `RegisterStep()`.
- **BREAKING: the `EntityPluginBase<TEntity>.Execute(ILocalPluginContext)` fallback is removed.** Bind a typed
  handler with `.Handle(...)` instead; a step with no handler now fails fast.
- **BREAKING: `OnWhoAmI()` is gone.** `WhoAmI` is a global, entity-less message: it cannot be registered as an
  entity-bound plugin step (the platform rejects the entity-filtered step), so the typed `OnWhoAmI()` builder
  method and its context are removed to stop offering a registration that can never work. `WhoAmI` stays a message
  constant, remains callable from a handler (`service.Execute(new WhoAmIRequest())`), and can still be generated as
  a typed request with `vela models --messages`.

## [0.10.0] - 2026-09-20 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.10.0`.

### Added

- **`plan` / `apply` warn on plugin step execution-order (rank) collisions.** When a step being deployed shares
  its `rank` with another step on the same message, entity, stage and mode anywhere in the target environment
  (including steps owned by other assemblies), a diagnostic is emitted, since their relative execution order is
  undefined. It shows in `plan` / `apply` output and fails the run under `--strict`.

### Changed

- The default scaffold framework version (`vela init` offline fallback) is now `0.17.0`.

## [0.9.0] - 2026-09-18 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.9.0`.

### Changed

- **`vela init` / `vela add plugin` / `vela add step` and the generated `On<Message>()` step extensions emit the
  redesigned `RegisterStep()` builder.** Scaffolded plugins, the single-file `add plugin` / `add step` output and
  the `vela models` step extensions now use `RegisterStep().On<Message>()...FilterOn(...).Handle(...)` (was
  `RegisterSteps()...FilteringAttributes(...)`), bind a typed handler with `.Handle(...)`, and reference the
  `Vela.Steps` namespace. The default scaffold framework version is now `0.15.0`.

### Fixed

- **`vela models --custom-apis` (all, no names) no longer times out on large environments.** It read every Custom
  API with one query plus two per API (an N+1), which could exceed the service timeout. It now reads all Custom
  APIs, their request parameters and their response properties in three paged queries and joins them in memory.
- **`vela models` writes C# models into the project's `Models/Generated` folder.** Run from a project with no
  `--out`, it wrote the files to the project root even though the inferred namespace is
  `<Prefix>.Shared.Models.Generated`; the files now land where the namespace says. An explicit `--out` or
  `--namespace` is respected.

## [0.8.0] - 2026-09-16 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.8.0`.

### Changed

- **Scaffolded solutions reference `Vela.Messages`** (the new multi-targeted base-types package) from the Shared
  project, and the default core version is `0.14.0`. Generated Custom API request/response contracts now compile in
  Shared and can be called from anywhere (plugins on net462, the ConsoleApp / integrations on net10.0). Closes the
  gap where `vela models --custom-apis` output landed in the SDK-only Shared project and would not build.

## [0.14.0] - 2026-09-16 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.14.0` (all three packages ship at this version).

### Added

- **New `Vela.Messages` package** (multi-targets `net462;net10.0`). It holds the SDK-only request/response base
  types (`TypedRequest` / `TypedResponse`) plus the generic `CreateRequest<T>` / `UpdateRequest<T>` /
  `UpsertRequest<T>` / `SendEmailFromTemplateRequest<T>` / `UpsertResponse`. Because it multi-targets, a generated
  Custom API contract (which derives from `TypedRequest`) now compiles wherever it is called from, including a
  `net10.0` Console app or integration, not only inside a `net462` plugin. `Vela.Plugins` takes a dependency on it,
  so plugin projects get it transitively.

### Changed

- **The `Vela.Messages` base types moved from `Vela.Plugins` into the new `Vela.Messages` package.** They keep the
  same `Vela.Messages` namespace, so source is unchanged (`using Vela.Messages;` still works), and `Vela.Plugins`
  ships type forwarders so already-compiled assemblies remain binary-compatible. No code change is required.

## [0.7.1] - 2026-09-16 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.7.1`.

### Fixed

- **`vela models --solution-roles` now works.** It was mis-registered as a value-taking option, so it errored in
  every form (`--solution-roles` alone reported "requires a value", and with a value reported "requires
  --solution"). It is a boolean flag combined with `--solution <uniquename>`.

## [0.7.0] - 2026-09-16 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.7.0`.

### Added

- **`vela add customapi --prefix <publisherprefix>`** overrides the Custom API unique-name prefix. It defaults to
  the project prefix, but that must match the target solution's Dataverse *publisher* prefix (unrelated to the
  `.NET` project prefix or the environment name); when the default is used, `add` now prints a note so a mismatch
  is caught before `apply --solution` rejects it.

### Changed

- **`vela add plugin --filtering-attributes` now validates against the early-bound model.** Names are
  case-sensitive property names (`Name`, not `name`); when the model is available, a value that does not match a
  real property is flagged with a warning and a suggestion, so the mistake surfaces at generation time instead of
  as a later compile error.

### Fixed

- **Updating a plugin step's image no longer fails.** Changing the columns of an existing pre/post image made
  `apply` throw a platform `NullReferenceException` (0x80040216) from the Dataverse image-update path (even for an
  attributes-only change, which the previous in-place update did not avoid). A changed image is now reconciled by
  delete + recreate, which the platform accepts.
- **Scaffolded plugin projects no longer package a stale assembly.** The plugin package version is pinned to
  `1.0.0` while the assembly auto-increments, so `GeneratePackageOnBuild`'s incremental check could keep an old
  `.nupkg` after a code-only change and deploy a stale assembly (`vela plan` would silently miss new types). The
  generated project now deletes the package before each build so it is always repacked from the fresh assembly.

## [0.6.1] - 2026-09-15 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.6.1`.

### Added

- **`vela update` updates the global tool** by running `dotnet tool update --global Vela.Cli` (pin a version with
  `--version <x.y.z>`). On Windows, where the running executable is locked and cannot replace itself, it explains
  how to run the update from a fresh shell. The "newer version available" notice now also points at `vela update`.

## [0.13.0] - 2026-09-15 (Vela.Plugins + Vela.Testing)

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.13.0`.

### Fixed

- **Test harness: a plugin sees the configured caller's identity, not a random value.** `ctx.UserId`,
  `InitiatingUserId`, `BusinessUnitId` and `OrganizationId` on the execution context now come from the store's
  acting user (`WithCurrentUser` / `WithCurrentBusinessUnit` / `WithOrganizationId`), consistent with what
  `WhoAmI` and `createdby`/`ownerid` stamping report, instead of ctor-default GUIDs.
- **Test harness: `ParentContext` is populated on a cascaded execution.** A plugin fired by another plugin's write
  now sees its caller's context as `IPluginExecutionContext.ParentContext` (with its `SharedVariables`), so
  re-entrancy guards like `if (ctx.ParentContext?.MessageName == "Create")` behave as they do in Dataverse.
- **Test harness: a not-found record raises the platform fault, not `InvalidOperationException`.** Retrieving or
  updating a missing record now throws `FaultException<OrganizationServiceFault>` with the documented
  `ObjectDoesNotExist` code (`-2147220969`), matching a real `IOrganizationService`, so a plugin that catches the
  fault code behaves the same under the harness as in production.

## [0.6.0] - 2026-09-15 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.6.0`.

### Added

- **`vela add plugin` accepts `--filtering-attributes <Col1,Col2>`** to set the filtering attribute(s) for a
  `Create`/`Update` (or `CreateMultiple`/`UpdateMultiple`) step. When omitted, the column is auto-detected from
  the entity's generated early-bound model (the conventional primary-name column). The generated file always
  compiles and applies; when nothing can be inferred, `add` fails with a clear message instead of emitting a
  non-compiling placeholder.
- **`vela models --custom-apis` with no value warns before generating every Custom API.** Bare `--custom-apis`
  emits all Custom APIs in the environment (system ones included, often hundreds of files); it now prints a
  warning pointing at `--custom-apis <a,b,c>` to target specific ones or `--package`/`--packages-dir` to exclude
  the ones you authored in code.

### Changed

- **`vela add plugin` no longer emits the non-compiling `REPLACE_WITH_A_FILTERING_ATTRIBUTE` placeholder.** See
  the `--filtering-attributes` behavior above.
- **The Custom API plan/apply diff shape is built from a single source of truth** (`CustomApiShape`), shared by the
  live read, the local normalizer and the parameter reader, so the shape can no longer drift between the read and
  normalize sides (the divergence class that produces a permanent non-converging "changed").
- **`vela models` generates the roles it can resolve instead of aborting on the first missing one.** A typo'd or
  absent `--roles` name is now warned about and skipped; the run still generates the roles that resolved, and only
  fails when none of the requested roles exist.
- **`vela --help` documents every `vela models` scope.** The top-level help now lists `--custom-apis`,
  `--messages`, `--roles`, `--solution-roles` and `--lang` (previously only `--solution`/`--entities`/`--forms`/
  `--tables` were shown, though the others already worked and are detailed under `vela models --help`).

### Fixed

- **`plan`/`apply`/`inspect` no longer hang under `--interactive` when stdin is redirected** (CI, agents,
  piped input). The optional "package assembly name" prompt now only appears on a real terminal, so a scripted
  run with all inputs supplied no longer blocks on `Console.ReadLine()`.
- **`apply --solution` fails fast with an actionable message when given a NuGet-signed plugin package.** Adding
  the solution publisher prefix rewrites (repacks) the package, which invalidates the signature and was rejected
  by Dataverse with the cryptic `0x80040265` "signature does not cover the package content". `apply` now detects
  the signature up front and explains how to proceed (build/sign the package already named with the prefix, or
  apply without `--solution`).
- **`apply` fails fast instead of silently registering an unfiltered (global) step.** When a step names an entity
  whose `(entity, message)` has no `sdkmessagefilter` (a typo'd or unsupported entity), Vela previously omitted the
  filter and registered a step that fired for **every** entity supporting the message. It now throws a clear error.
- **Custom API deletion is scoped to its plugin type.** Deleting a step during `apply` matched by display name
  alone, which is not unique across plugin types/assemblies and could delete an unrelated step; the lookup is now
  scoped to the resolved plugin type (with an unscoped name match kept only as a last resort when the plugin type
  no longer resolves).
- **`apply` guards the two remaining immutable Custom API fields.** `allowedCustomProcessingStepType` and
  `workflowSdkStepEnabled` join `isFunction`/`bindingType`/`boundEntity` in the "cannot be changed after saved"
  check, so changing them no longer reports a successful update that never converges (a permanent phantom diff).
- **`apply` validates `WorkflowSdkStepEnabled` constraints locally.** A workflow-enabled Custom API must be an
  action (not a function) and cannot use Entity/EntityCollection/StringArray parameters; violations now fail with a
  clear message instead of an opaque platform error at apply.
- **`apply --solution` validates the Custom API unique-name publisher prefix** (as it already does for web
  resources and plugin packages), so a unique name missing the prefix fails with guidance rather than a cryptic
  Dataverse error.
- **Custom API parameter type resolution no longer over-matches on substrings.** The CLR-type to Custom API type
  mapping was an order-dependent `string.Contains` ladder in which any type name containing `"int"` resolved to
  `Integer` (and `Entity` had to be tested after `EntityReference`/`EntityCollection`). It is now an exact
  simple-name table covering the SDK type names, the Custom API type labels and the C# aliases, so an unrelated
  type resolves correctly (or falls back to `String`) instead of being misclassified.

## [0.12.0] - 2026-09-14 (Vela.Plugins + Vela.Testing)

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.12.0`.

### Changed

- **`PluginPipeline` records asynchronous step failures on the new `AsyncStepFailures` collection instead of
  throwing them to the triggering caller**, matching Dataverse (async steps run as decoupled system jobs whose
  failures never reach the synchronous caller). Assert on `pipeline.AsyncStepFailures` to verify an async step
  threw.

### Fixed

- **`CustomApiBase` is thread-safe on first execution.** The Dataverse sandbox concurrently reuses a single
  plugin instance; building the Custom API definition is now serialized (mirroring `EntityPluginBase`), so
  racing first executions can no longer throw or cross-wire the definition.
- **`ctx.HasChanged` detects a polymorphic-lookup table change.** Two lookups with the same id in different
  tables are no longer treated as equal.
- **`IPluginCacheService` can cache a `null`** (for example a negative "not found" result) without throwing on
  retrieval.
- **`GetPreImageMerged`/`GetPostImageMerged` return an independent copy** even when there is no Target, so
  mutating the result cannot corrupt the shared image.
- **`TypedRequest`/`TypedResponse.Get<T>` return `default` for a missing or wrong-typed parameter** instead of
  throwing.
- **Test harness: `ExecuteMultiple`/`ExecuteTransaction` run each child through the plugin pipeline**, so
  registered steps fire (they previously bypassed the pipeline and silently ran none).
- **Test harness: reads are isolated from the store on every path.** `QueryByAttribute`, link-entity aliased
  columns, aggregate group/min/max values, and `Retrieve` with an explicit `ColumnSet` now deep-copy, so
  mutating a returned value cannot corrupt a stored record.
- **Test harness: multi-column group-by no longer merges distinct tuples** (the group key now uses a separator).
- **Test harness: field-level security masks a secured column pulled through a link-entity alias.**

## [0.5.0] - 2026-09-14 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.5.0`.

### Changed

- **BREAKING: `-p` now means `--package` in `vela models`** (consistent with `plan`/`apply`), not `--project`.
  It was a silent cross-command footgun: `vela models --custom-apis -p pkg.nupkg` consumed the package path as
  the project. Use the long form `--project` for the project/namespace-inference path.
- **BREAKING: a Custom API parameter's `type` can no longer be changed on an existing parameter.** The Dataverse
  `customapirequestparameter`/`customapiresponseproperty` `Type` column is create-only, so an update was
  silently ignored and every subsequent `plan`/`apply` reported the same phantom change. `apply` now fails fast
  with guidance to delete and recreate the parameter (mirroring the parent Custom API immutable guard).

### Fixed

- **`vela models` generates compilable C# when a Dataverse label/description spans multiple lines.** A raw
  newline inside a single-line `///` doc comment previously produced uncompilable output.
- **Custom API request-parameter entity binding no longer sticks.** Removing a parameter's bound entity now
  clears `logicalentityname` instead of leaving the stale value, which had caused a perpetual "changed" diff.
- **Multi-target-framework plugin packages resolve the correct assembly.** `ExtractPackage` no longer flattens
  every `lib/**/*.dll` by file name (colliding same-named assemblies across frameworks); it picks a single
  framework, preferring .NET Framework (the sandbox target).
- **`vela sign` cannot deadlock on large `dotnet nuget sign` output** (both output streams are now drained
  concurrently).
- **Non-verbose `apply` no longer prints step / Custom API create/update/delete lines** (the `verbose` flag is
  now honored for them, matching package/assembly/web-resource logging).
- **The Dataverse connection is disposed when it fails to become ready** (previously leaked).
- **Fewer Dataverse round-trips when adding plugin types under an unchanged assembly** (the plugin-type lookup is
  resolved once instead of once per required type).

## [0.4.0] - 2026-09-14 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.4.0`.

### Changed

- **CLI: the `custom-api` scaffold demonstrates the optional request-parameter overload.** The generated
  `AccountEstimateValue` template now shows the `.Request(..., optional: true)` overload and notes that
  optionality applies to request parameters only (response properties are always present, matching Dataverse).

### Fixed

- **CLI: the plugin assembly is no longer re-uploaded when its content and version are unchanged.** Mirrors the
  plugin-package fix: an identical `content` + `version` update only bumped `modifiedon`. The
  "new type + same version" stale-assembly trap stays handled by `GuardAgainstStaleAssemblyVersion`.
- **CLI: `vela models --custom-apis` preserves request-parameter optionality.** The generator now reads
  `customapirequestparameter.isoptional` and emits a `[Required]` attribute on required request parameters, so
  rebuilding the manifest from a regenerated model keeps required parameters required instead of silently making
  every parameter optional. Response properties have no `isoptional` column and are never marked required.

## [0.11.0] - 2026-09-14

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.11.0`.

### Added

- **Framework: early-bound `ctx.PreImage` / `ctx.PostImage` on the curated message contexts.** A pre/post image
  is always a snapshot of the step's own table, so the typed contexts now expose it as the context's `TEntity`
  with no type argument: `ctx.PostImage` on `ICreatePostContext<T>`, `ctx.PreImage` on `IUpdatePreContext<T>` /
  `IDeletePreContext<T>` / `IDeletePostContext<T>`, and both on `IUpdatePostContext<T>`. Each reads the image
  under the default alias and returns null when none is registered; `ctx.GetPreImage<T>("alias")` /
  `ctx.GetPostImage<T>("alias")` remain for custom aliases. Exposed only where the message and stage actually
  produce that image (no pre-image on Create, no post-image on Delete).

### Fixed

- **Framework: plugin / Custom API introspection failures are reported as an Error.** A type that IS a plugin
  or Custom API but throws while yielding its definition was demoted to a Warning and silently excluded from
  the manifest, dropping it from the plan (and risking a delete of the live registration). Both discovery
  paths now emit an Error-severity diagnostic.

## [0.3.0] - 2026-09-13 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.3.0`.

### Added

- **CLI: brand-aligned terminal styling.** Command output now uses the Vela palette (indigo/cyan) with a
  colorized `plan`/`apply`/`inspect` diff (green added, red removed, amber changed), green `✓` completion
  lines for `init`/`add`/`models`/`sign`, a red `✗ Error:` prefix on failures, and an animated spinner while
  the CLI connects to and reads from Dataverse. Color is emitted only on a real terminal and is disabled by
  `NO_COLOR` / `VELA_NO_COLOR`, so piped, redirected, and CI output stays plain text.
- **CLI: `--json` on `plan`/`apply`/`inspect`.** Forces JSON output even on a terminal (equivalent to piping
  or `--out <file>`).

### Changed

- **CLI: `plan`/`apply`/`inspect` print a human-readable summary by default on a terminal.** When stdout is a
  terminal and neither `--json` nor `--out` is given, these commands render the styled summary that previously
  required `--verbose`. Piped/redirected runs, `--json`, and `--out` are unchanged and still emit JSON, so
  scripts and coding agents parse the same contract as before.
- **CLI: `apply` writes to Dataverse by default (BREAKING).** Previously `apply` only previewed unless
  `--execute` was given. It now applies: on a terminal it shows the plan and requires a typed `yes`; in a
  non-interactive session (no stdin TTY) it refuses unless `--auto-approve` (alias `--yes`) is passed, so a
  script never writes unattended by accident. Use `vela plan` to preview, and `vela apply --auto-approve` for
  non-interactive writes. The interactive menu's "No, preview" choice now runs `plan`. The `plan` note no
  longer contains the misleading substring "Dataverse changes were executed" (now "This is a preview; nothing
  was written to Dataverse.").
- **CLI: `models` infers the namespace from the solution `.sln`.** When run at the solution root (no single
  `.csproj` in the current directory) it derives the namespace from a representative project in the sibling
  `.sln` (preferring the `.Shared`/`.Plugins` project), instead of failing with "Could not infer a namespace",
  so it works from the root like `plan`/`apply`. `--namespace` still overrides.

### Removed

- **CLI: the `apply --execute` flag.** Replaced by default execution plus a confirmation gate (see Changed).
  Migrate CI/scripts from `vela apply --execute` to `vela apply --auto-approve` (or `--yes`).

### Fixed

- **CLI: interactive OAuth sign-in uses a loopback redirect URI.** `plan`/`apply --interactive` (and the
  scaffolded sign-in script) built the OAuth connection string with a non-loopback redirect URI, so MSAL's
  system-browser flow failed with "Only loopback redirect uri is supported" before the browser opened. It now
  uses `http://localhost`.
- **CLI: plugin steps on the `Associate`/`Disassociate` messages converge in `plan`/`apply`.** These global
  messages carry no primary entity on their `sdkmessagefilter` (`primaryobjecttypecode` is `none`), so the live
  read always returned an empty `entityLogicalName` while the local definition declared one (e.g. `account`),
  reporting the step as changed on every run. The field is now neutralized for these messages in the diff shape.
- **CLI: the plugin package is no longer re-uploaded on every `apply`.** `UpsertPluginPackage` unconditionally
  wrote the package `content`/`version`, bumping `modifiedon` even when the bytes were identical (and invisible
  in the diff, since packages reconcile out-of-band). It now skips the update when the stored version and
  content already match.

## [0.10.0] - 2026-09-13

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.10.0`.

### Fixed

- **Framework: `On<TRequest>()` (and permissive typed `OnMessage` handlers) whose request name matches a modelled
  message.** A step registered with `On<TRequest>()` binds a handler taking `IMessagePre/PostContext<TRequest>`.
  When `TRequest.RequestName` collided with a Vela-modelled message (e.g. `MergeRequest` -> `Merge`), dispatch
  built the modelled per-message context (`IMergeContext<TEntity>`), which does not implement
  `IMessagePreContext<TRequest>`, so a correctly-written handler threw at runtime. Dispatch now resolves the
  handler's own parameter type first, so these steps fire as documented.
- **Framework: FetchXML condition values of Dataverse types serialize to their underlying value.** An
  `OptionSetValue` / `Money` / `EntityReference` in a `ConditionExpression` was emitted via `ToString()` (the type
  name, e.g. `value="Microsoft.Xrm.Sdk.OptionSetValue"`). `ToFetchXml()` now emits the choice integer, the money
  decimal and the lookup GUID respectively.
- **Framework: FetchXML operator names for the hierarchy, multi-select-choice and fixed-window families.**
  `AboveOrEqual` (`eq-or-above`), `UnderOrEqual` (`eq-or-under`), `DoesNotContainValues` (`not-contain-values`),
  `Last7Days` (`last-seven-days`) and `Next7Days` (`next-seven-days`) are now mapped explicitly instead of falling
  back to kebab-case (which produced unrecognized names such as `above-or-equal` / `does-not-contain-values` /
  `last7-days`).
- **Framework: FetchXML preserves `QueryExpression.NoLock`.** `ToFetchXml()` now emits `no-lock='true'` when the
  query sets `NoLock`, instead of silently dropping the hint.
- **Framework: `IPluginCacheService` no longer retains a per-key lock object for the lifetime of the AppDomain.**
  Cache-miss synchronisation now lives inside each entry (a `Lazy<T>` slot) and is reclaimed together with the
  entry on expiration / `Invalidate` / `Clear`, so a workload using high-cardinality keys no longer accumulates
  lock objects. The factory still runs at most once per key under contention.
- **Framework: Custom API `.Request(...)`/`.Response(...)` accumulate across calls.** A second `.Request(...)`
  (or `.Response(...)`) call cleared the parameters the previous call had added, so only the last call
  survived, silently dropping parameters (and the whole Custom API) from the plan. Multiple calls now
  accumulate, and adding the same parameter twice throws instead of failing silently.
- **Framework: a Custom API request that uses plain auto-properties fails loudly.** The typed request binds its
  input parameters through the `Parameters` bag (`get => Get<T>()`), so a plain auto-property (`{ get; set; }`)
  silently read its default (`Guid.Empty` / `null`). `MapInputToRequest` now detects an input key matching an
  auto-property and throws a clear `InvalidPluginExecutionException` naming the property instead.

## [0.9.0] - 2026-09-13

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.9.0`.

### Added

- **Framework: async-only typed-message step (`IAsyncOnlyModeSelector` / `OnAsyncOnly<TRequest>()`).** New public
  surface backing the CLI's AsyncOnly gate above: `IStepBuilder<TEntity>.OnAsyncOnly<TRequest>()` returns an
  `IAsyncOnlyTypedMessageStepStage<TEntity, TRequest>` whose stages yield an `IAsyncOnlyModeSelector<TNext>`
  (a `.Async()` method, and no `.Sync()` path). Additive; `On<TRequest>()` is unchanged.

### Fixed

- **Framework: FetchXML list operators emit `<value>` child elements even for a single value.** `In` / `NotIn` /
  `Between` / `NotBetween` no longer emit the `value="x"` attribute form (which Dataverse rejects) when given one
  value.
- **Framework: FetchXML operator names for the `eq-userid` / `*-businessid` / `olderthan-x-*` families.** These
  irregular operators are now mapped explicitly instead of falling back to kebab-case (which produced unrecognized
  names such as `equal-user-id` / `older-than-x-months`).
- **Testing: `WinOpportunity` / `LoseOpportunity` resolve the primary entity to the opportunity.** The in-memory
  pipeline now derives the primary record from the request's `OpportunityClose.opportunityid` instead of picking the
  `opportunityclose` activity, so an entity-filtered step registered on these messages fires as expected.

## [0.2.0] - 2026-09-13 (Vela.Cli)

CLI line (`Vela.Cli`), published as `cli-v0.2.0`.

### Added

- **CLI: `vela models --custom-apis` emits a registrability-gated `On<Message>()` step extension (Phase 3c).**
  Alongside the typed `XxxRequest`/`XxxResponse`, the generator now emits an `On<Message>()` builder extension whose
  shape is driven by the Custom API's Dataverse metadata, giving **compile-time registrability**: (1) a
  bound/collection-bound API (`boundentitylogicalname` set) constrains the extension to `IStepBuilder<TEntity>` for
  that entity, so it only compiles on the matching `EntityPluginBase<TEntity>` (a global API emits a generic
  `On<Message><TEntity>()`); (2) an **AsyncOnly** API (`allowedcustomprocessingsteptype`) returns an async-only
  stage exposing only `.Async()`, so a synchronous registration does not compile; (3) a **None** or **private** API
  emits no extension at all (only an explanatory comment), so the blessed path does not compile. The generic
  `On<XxxRequest>()` escape hatch stays for explicit use, and Dataverse still rejects an invalid registration at
  deploy time. The method name is the unique name minus its publisher prefix, PascalCased
  (`zava_AccountEstimateValue` -> `OnAccountEstimateValue`). See `.context/phase3c-customapi-registrability.md`.
- **CLI: `--option=value` inline form and clearer option errors (shared tokenizer).** Every command now accepts the
  `--option=value` form in addition to `--option value`. A misspelled option is still rejected up front, and a
  value option missing its value (last token, or followed by another known option) now fails with
  `Option '--x' requires a value.` instead of silently binding to the next option or dropping the value. Options
  that may legitimately appear bare (`vela models --messages` / `--custom-apis` = all) keep working, and a value
  that itself starts with `-` is still accepted.
- **CLI: the interactive menu offers `models` and `logout`.** Running `vela` with no arguments now includes
  "Generate models" (prompts for a solution or a table list, over an interactive connection) and "Sign out"
  alongside init/add/plan/apply/inspect.
- **CLI: an upgrade notice when a newer version is on nuget.org.** After a command runs, the CLI prints a
  one-line "a newer version is available - run `dotnet tool update -g Vela.Cli`" notice to stderr. The nuget.org
  check is throttled to once per 24h (cached in the user config), best-effort (never blocks or fails a command),
  and silent for `--json` output, a local/dev build, or when `VELA_NO_UPDATE_CHECK` is set.

### Fixed

- **CLI: a step's identity survives a rename.** `plan`/`apply` now recover a step's key from a stable
  `[vela:stepkey=...]` marker written into the step description, falling back to parsing the display name for steps
  registered before this change. Renaming a step (or its plugin type) in Dataverse no longer breaks the match and
  causes a delete-and-recreate that loses the step's id and history; the step is updated in place instead. Existing
  steps pick up the marker the next time they are written (no forced migration).
- **CLI: `vela new --force` preserves user-edited root files.** A regeneration no longer overwrites an existing
  `README.md`, `.gitignore`, or `Directory.Build.props` (they are written only when absent); the managed `.sln` and
  project files are still regenerated, matching the "user files at the solution root are preserved" contract.
- **CLI: TypeScript barrel imports use forward slashes.** The generated `index.ts` builds each module specifier with
  `/` and drops a trailing `.ts` only when present, so a generated file whose path carries OS separators still emits
  a valid import.
- **CLI: `plan`/`apply` only skips the platform-managed Custom API step, not every main-operation step.** When
  reading live steps, only the internal Custom API handler step (its message is the Custom API's unique name) at
  MainOperation is skipped; a genuine step at MainOperation on a standard message is kept so it diffs correctly
  instead of showing as a perpetual "added".
- **CLI: the ignorable "Invalid plug-in registration stage" fault is matched by error code.** `apply` recognizes it
  by the Dataverse error code (`0x80044184`) rather than the English message text, so it stays ignorable on
  non-English organizations (the message match remains as a fallback).
- **CLI: `vela new` / `init` can no longer scaffold outside the target directory.** A solution or web-resources
  folder name is rejected unless it starts with an alphanumeric character (so it can never be `.`/`..`), and a
  defense-in-depth check refuses to write when the resolved target is not under the output directory, so a
  `--force` regeneration can never delete managed folders outside the tree.
- **CLI: generated `On<Message>()` step extensions reference the real early-bound type.** `vela models --messages`
  / `--custom-apis` now resolve each applicable entity's model type name from its Dataverse SchemaName instead of a
  naive PascalCase of the logical name, so the extension compiles for custom entities with underscores
  (e.g. `new_customentity` -> `new_CustomEntity`).
- **CLI: generated request/response and step-extension code is collision- and keyword-safe.** Custom API parameter
  and SDK message field names that are C# keywords or contain invalid characters are escaped/sanitized (with an
  explicit `Parameters`/`Results` key so the runtime name is preserved); two Custom APIs whose names collide once
  the publisher prefix is stripped get distinct, prefixed extension methods; and option-set choices with negative
  values no longer emit invalid enum member identifiers.
- **CLI: `--core-version` is validated** against a NuGet version pattern (blocks `.csproj` injection and typos that
  silently fail `dotnet restore`).
- **CLI: `plan`/`apply` converge in more cases.** A Custom API parameter declared with an equivalent type
  (`System.Int64`/`long`, `System.Single`) is canonicalized to the stored form before diffing, so it no longer
  reports a perpetual "changed"; a secure configuration that could not carry its plaintext secret (no stable step
  key) now fails clearly instead of writing the redacted digest to Dataverse; and a duplicate Custom API / web
  resource identity or duplicate local step key fails with a clear message instead of an opaque error or a silently
  dropped step.
- **CLI: argument parsing.** A boolean flag no longer swallows the positional package path
  (`vela plan --strict foo.nupkg`), and the unknown-option check no longer lets a value option hide a following
  known option. `vela add step` locates the `RegisterPluginSteps()` method by its declaration rather than the first
  textual mention.
- **CLI: `vela new` / `init` rejects Windows reserved solution names** (`CON`, `NUL`, `COM1`, ... and
  `CON.txt`), which would otherwise scaffold an unusable folder on Windows.
- **CLI: `vela models --entities` is case-insensitive.** Entity names are lower-cased to Dataverse's logical-name
  form, so `--entities Account` resolves the same as `account`.
- **CLI: `RetrieveAll` no longer mutates the caller's `QueryExpression`.** Its `PageInfo` is restored after paging,
  so a query object reused after a read is unaffected.
- **CLI: clearer diagnostics.** An unknown option no longer dumps the generic top-level usage (the message already
  points at the right `vela <command> --help`), and a `.csproj` whose `RootNamespace`/`AssemblyName` cannot be read
  now warns (with the reason) instead of silently falling back to the file name.

## [0.8.0] - 2026-09-13

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.8.0`.

### Added

- **Framework: early-bound `ctx.Request.Target` on Create/Update.** `ctx.Request` on `ICreatePreContext` /
  `ICreatePostContext` / `IUpdatePreContext` / `IUpdatePostContext` is now a Vela-owned typed request
  (`Vela.Messages.CreateRequest<TEntity>` / `UpdateRequest<TEntity>`) whose `Target` is the early-bound
  `TEntity`, so a handler reads `ctx.Request.Target` typed instead of the SDK request's base `Entity`. This is
  the single typed target accessor (see the Changed entry below removing `ctx.Target`). Reading
  `ctx.Request.Target` as a base `Entity` still compiles (`TEntity : Entity`). The raw
  SDK request stays reachable via `ctx.GetRequest<CreateRequest>()`. First step of the typed-requests design
  (`.context/typed-requests-design.md`).
- **Framework: `On<TRequest>()` step entry - typed message, no magic string.** Register a step with the message
  derived from the request type: `RegisterSteps().On<CalculateAccountScoreRequest>().PostOperation().Sync().Handler(OnScore)`
  where `void OnScore(IMessagePostContext<CalculateAccountScoreRequest> ctx)`. The message name comes from the
  request's `RequestName` (no string literal), and the request type is stated once (the handler's context type is
  inferred - no restating on `Handler`). `OnMessage("...")` / `OnMessage<T>()` / `.Handler<TRequest>(...)` remain.
- **Framework: `TypedRequest` / `TypedResponse` base + `ToResponse<T>()`.** Authoring or generating a message
  request/response is now one line per property via `get => Get<T>(); set => Set(value);` (over `Parameters` /
  `Results`, `[CallerMemberName]`-backed) instead of hand-rolled `Parameters.Contains(...)` blocks. Call a message
  and type its result with `service.Execute(new XxxRequest{...}).ToResponse<XxxResponse>()`. `vela models`
  generation, the scaffold, and `CreateRequest<TEntity>` / `UpdateRequest<TEntity>` all use this shape.
- **Framework: SendEmailFromTemplate has an early-bound `ctx.Request.Target`; `Upsert` typed request/response.**
  `ISendEmailFromTemplatePreContext`/`PostContext` now use a Vela-owned `SendEmailFromTemplateRequest<TEntity>`
  (typed `Target` plus typed `TemplateId`/`RegardingId`/`RegardingType`). New `Vela.Messages.UpsertRequest<TEntity>`
  (typed `Target`) and `UpsertResponse` (`RecordCreated`, `Target`), reachable via `On<UpsertRequest<TEntity>>()`.

### Changed

- **Framework (BREAKING): `ctx.Target` removed from all message contexts.** The `Target` context property (and the
  `IEntityTargetContext<TEntity>` interface behind it) is gone from the curated Create/Update/SendEmailFromTemplate
  contexts and from the two-argument generic `IMessagePreContext<TRequest, TEntity>` /
  `IMessagePostContext<TRequest, TEntity>`. Read the primary record via `ctx.Request.Target` (early-bound `TEntity`
  on Create/Update, where `ctx.Request` is a Vela-owned typed request; the SDK's `Entity` / `EntityReference` for the
  rest). The `ctx.GetTarget<T>()` extension remains as the message-agnostic early-bound accessor (for example on
  `SendEmailFromTemplate`, whose `ctx.Request.Target` is the base SDK `Entity`). Part of the typed-requests design
  (`.context/typed-requests-design.md`).
- **Framework (BREAKING): `CustomApiBase<TRequest, TResponse>` now requires `OrganizationRequest`/`OrganizationResponse`
  types instead of POCOs.** The type constraints move from `where TRequest : class, new()` /
  `where TResponse : class, new()` to `where TRequest : OrganizationRequest, new()` /
  `where TResponse : OrganizationResponse, new()` (and likewise on `Register.RegisterCustomApi<TRequest, TResponse>()`
  and `ICustomApiBuilder<TRequest, TResponse>`). The request/response now expose typed properties over the SDK
  `Parameters`/`Results` bags, so a **single** generated type serves all three usages: authoring the Custom API,
  calling it (`service.Execute(new XxxRequest { ... })`), and reacting to it (`OnMessage<XxxRequest>()...Handler<XxxRequest>(...)`).
  This unifies the authoring surface with the `vela models --custom-apis` output. At execution the framework now
  hydrates the request over the live `InputParameters` and copies the response's `Results` back to `OutputParameters`
  (the previous reflect-and-convert POCO mapping, including JSON-string coercion, is removed). Only the request/response's
  own declared properties become Custom API parameters; the inherited `OrganizationRequest`/`OrganizationResponse`
  members never do. Migration: change each `XxxRequest`/`XxxResponse` to derive from `OrganizationRequest`/`OrganizationResponse`
  and expose properties over `Parameters`/`Results` (set `RequestName` in the constructor), as the scaffold and the
  [Custom API guide](guides/custom-apis.md) now show. Part of the typed-requests design (`.context/typed-requests-design.md`).

## [0.1.26] - 2026-09-13

CLI line (`Vela.Cli`), published as `cli-v0.1.26`.

### Added

- **CLI: `vela models --messages` emits an entity-constrained `On<Message>()` step extension.** Alongside the
  typed `XxxRequest`/`XxxResponse`, the generator now emits an `On<Message>()` builder extension per applicable
  entity (from the message's `sdkmessagefilter` `primaryobjecttypecode`), constrained to `IStepBuilder<TEntity>`
  and delegating to `On<XxxRequest>()`. This gives **compile-time entity-applicability** for generated messages
  (e.g. `OnQualifyLead()` only compiles on an `EntityPluginBase<Lead>`), matching the curated messages. A global
  message (`primaryobjecttypecode` `none`) or one applying to more than 8 entities emits a single generic
  `On<Message><TEntity>()` instead. The early-bound entity types are assumed to be generated into the same
  namespace.

### Changed

- **CLI: scaffold's offline framework-version fallback is now `0.8.0`.** When the latest published framework
  version cannot be resolved online, `vela new` falls back to this default for the `Vela.Plugins` / `Vela.Testing`
  package references. Bumped from `0.6.0` so an offline scaffold references a framework that includes
  `Vela.Messages.TypedRequest` (required by the request/response classes `vela models --messages`/`--custom-apis`
  generate). Override with `--core-version`.

## [0.7.0] - 2026-09-08

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.7.0`.

### Added

- **Framework: typed `ctx.Request` on the permissive `OnMessage(...)` path.** A custom or non-modelled message
  step can now bind `.Handler<TRequest>(...)` (where `TRequest` is any `OrganizationRequest` with a parameterless
  constructor - an SDK request or a Vela-generated Custom API request) and receive a strongly-typed context:
  `IMessagePreContext<TRequest>` (pre-stage, typed `ctx.Request`) or `IMessagePostContext<TRequest>` (post-stage,
  typed `ctx.Request` plus a base `OrganizationResponse` `ctx.Response`). The handler's context is built from the
  request type declared on its parameter, so the long tail of messages and consumed Custom APIs get a typed
  request without a hand-authored per-message context. The curated `On<Message>()` surface and the untyped
  `OnMessage(...).Handler(ILocalPluginContext)` overload are unchanged.
  For a message whose request carries a full entity `Target`, bind the same `.Handler<TRequest>(...)` to a handler
  taking `IMessagePreContext<TRequest, TEntity>` / `IMessagePostContext<TRequest, TEntity>` to also get an
  early-bound `ctx.Target` (the step's own `TEntity`, i.e. the plugin's entity - no extra type argument) plus typed
  `ctx.PreImage` / `ctx.PostImage` (no `<TEntity>` in the body), on top of the typed `ctx.Request`. Messages whose
  target is an `EntityReference` keep reading `ctx.Request.Target`.

## [0.1.25] - 2026-09-08

CLI line (`Vela.Cli`), published as `cli-v0.1.25`.

### Added

- **CLI: `vela models --messages <a,b,c>`.** Generates typed `XxxRequest` / `XxxResponse` classes for any
  Dataverse SDK message (`sdkmessage`), not just Custom APIs, by reading the message's request/response fields
  from metadata and mapping each field's CLR type to a C# type. The output mirrors `--custom-apis` (a
  `RequestName`-setting `OrganizationRequest` over `Parameters` and an `OrganizationResponse` over `Results` with
  a `From(OrganizationResponse)` factory), so the generated request plugs straight into the Phase 1
  `OnMessage(...).Handler<TRequest>(...)` typed handler. Files are written into a `Messages/` subfolder of the
  output directory to keep the Shared project tidy. Combinable with the other `vela models` scopes over the
  same connection.
- **CLI: `vela models --messages` with no value generates all consumable messages.** A bare `--messages`
  (no list) now generates every registerable message in the environment - Microsoft's Event Framework
  "available events" set: non-private messages that have a visible `sdkmessagefilter` allowing a custom
  processing step (minus the deprecated `SetStateDynamicEntity`/`RemoveRelated`/`SetRelated`/`Execute`). It
  then skips messages backed by a Custom API (owned by `--custom-apis`, which also keeps your own authored
  Custom APIs out), and messages that already ship an SDK request class - both the core
  `Microsoft.Xrm.Sdk.Messages.*` types (reflected over the referenced `Microsoft.Xrm.Sdk` assembly, e.g.
  `CreateRequest`) and the Sales/business `Microsoft.Crm.Sdk.Messages.*` types (e.g. `QualifyLeadRequest`,
  `WinOpportunityRequest`), matched against a curated, non-exhaustive supplement because `Vela.Cli` does not
  reference `Microsoft.CrmSdk.CoreAssemblies`; an unlisted Crm.Sdk message simply generates a harmless duplicate.
  A targeted `--messages a,b,c` still generates exactly the named messages, only warning (never skipping) when
  one already has an SDK request class.
### Changed

- **CLI: `vela models --custom-apis` now writes into `Messages/`.** Generated Custom API request/response files
  land in the `Messages/` subfolder of the output directory (previously flat), unifying them with `--messages`
  output - a Custom API is a message.
- **CLI: `vela models --custom-apis` with no value generates every Custom API.** A bare `--custom-apis` (no list)
  now generates typed request/response for every Custom API in the environment, mirroring bare `--messages`. A
  list still generates exactly the named ones. (Together, bare `--messages` + bare `--custom-apis` cover the whole
  consumable surface with no overlap: `--messages` excludes Custom-API-backed messages, `--custom-apis` owns them.)
  Pass your built plugin package(s) with `--package <file.nupkg>` (repeatable) and/or `--packages-dir <folder>`
  (plus `--package-assembly <name>`) to exclude the Custom APIs you authored in code from bare `--custom-apis` -
  you already have those classes, so they are scanned out of the ALL set (a note reports how many were excluded).
  A targeted `--custom-apis a,b,c` is never filtered - the named ones are always generated.
- CLI: the offline-fallback framework version (`DefaultCoreVersion`, used only when the nuget.org lookup fails
  and no `--core-version` is passed) is bumped from `0.5.0` to `0.6.0` to match the current framework line.

## [0.6.0] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.6.0`.

### Changed

- **BREAKING - `ctx.TargetReference` and `ctx.TargetCollection` are removed from the message contexts.** A handler now
  reads a reference or bulk target straight off the SDK-typed request: `ctx.Request.Target` (the `EntityReference` for
  `Delete`/`Retrieve`/`Associate`/`Disassociate`/`Merge`/`GrantAccess`/`ModifyAccess`/`RevokeAccess`/`AddToQueue`/
  `RouteTo`/...) and `ctx.Request.Targets` (the `EntityCollection` for `CreateMultiple`/`UpdateMultiple`). `ctx.Target`
  stays, but **only** on the messages whose request carries a full entity `Target` (`Create`/`Update`/
  `SendEmailFromTemplate`), where it returns the early-bound `TEntity` the request's base `Entity` cannot. The two
  removed accessors were pure aliases of `ctx.Request.Target` / `ctx.Request.Targets` (an `EntityReference` /
  `EntityCollection` has no early-bound form, so they added no typing), and their per-message presence was the main
  source of "why is `ctx.Target` missing here" confusion. Migration: `ctx.TargetReference` -> `ctx.Request.Target`,
  `ctx.TargetCollection` -> `ctx.Request.Targets`. The `ctx.GetTargetRef()` / `ctx.GetTargets()` extension helpers are
  unchanged.

### Fixed

- **The in-memory harness now surfaces the created id to a Create Post handler.** A `Create` PostOperation step's
  context now exposes `OutputParameters["id"]` (and a `CreateMultiple` Post step exposes `OutputParameters["Ids"]`),
  exactly as Dataverse does, so `ctx.Response.id` / `ctx.Response.Ids` resolve when testing a post-create handler -
  on both the `crm.Create(...)` pipeline and the direct `PluginContext.For<T>()...Execute(plugin)` path. Previously
  `ctx.Response.id` read back `Guid.Empty` in the harness.

## [0.1.24] - 2026-09-06

CLI line (`Vela.Cli`), published as `cli-v0.1.24`.

### Changed

- The scaffolded `AccountPostCreatePlugin` is now a typed post-stage handler (`.Handler(OnAccountCreated)` with
  `void OnAccountCreated(ICreatePostContext<Account> ctx)`) that reads `ctx.Response.id` and a post-image, instead of
  the single-step `Execute(ILocalPluginContext)` override, so the scaffold showcases the stage-typed `ctx.Response`.
- The `AccountMessageReference` cheat-sheet reads reference / bulk targets via `ctx.Request.Target` /
  `ctx.Request.Targets` (the removed `ctx.TargetReference` / `ctx.TargetCollection` accessors), keeping `ctx.Target`
  only where the message carries a full entity target.

## [0.5.0] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.5.0`.

### Changed

- **BREAKING - message contexts are split by stage.** The single `I{Message}Context<TEntity>` per message is
  replaced by two: `I{Message}PreContext<TEntity>` (a pre-stage handler - `PreValidation` / `PreOperation` -
  exposing `ctx.Request` and the typed target only) and `I{Message}PostContext<TEntity>` (a PostOperation
  handler - `ctx.Request` **and** `ctx.Response`). The stage you pick determines which context the handler
  receives, so reading `ctx.Response` from a pre-stage handler no longer compiles (before, it was accessible but
  always empty). Migration: rename each handler's parameter from `I{Message}Context<TEntity>` to
  `I{Message}PostContext<TEntity>` for a PostOperation step, or `I{Message}PreContext<TEntity>` for a
  PreValidation/PreOperation step. A handler that only reads `ctx.Request` / `ctx.Target` needs the matching
  Pre/Post type for its stage; the `.Handler(...)` binding, the target accessors and everything else are
  unchanged. Permissive `OnMessage("...")` steps still take the plain `ILocalPluginContext`.

## [0.1.23] - 2026-09-06

CLI line (`Vela.Cli`), published as `cli-v0.1.23`.

### Changed

- Scaffolded plugins use the stage-split contexts (`I{Message}PostContext` on PostOperation handlers,
  `I{Message}PreContext` on the PreOperation Delete handler). Offline fallback framework version bumped to `0.5.0`.

## [0.4.4] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.4.4`.

### Fixed

- **Dependency injection now works inside typed message handlers.** `ctx.GetService<T>()` /
  `ctx.GetRequiredService<T>()` threw `"Context type 'I{Message}Context' does not expose the execution service
  resolver"` (or returned `null`) when called from a message-typed handler, because the resolver lookup was a
  concrete `LocalPluginContext` type check that the typed `StepContext` wrapper failed. The wrapper now forwards
  the per-execution resolver (via a new `IServiceResolverContext`), so DI resolves from any handler - typed or
  plain. Affected 0.3.0-0.4.3 (any typed handler that resolves a service, e.g. the scaffolded `ContactRouterPlugin`).

## [0.4.3] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.4.3`. Additive.

### Added

- `.Sync()` / `.Async()` step-builder shortcuts for `.Mode(ExecutionMode.Synchronous)` /
  `.Mode(ExecutionMode.Asynchronous)` - `RegisterSteps().OnUpdate().PostOperation().Sync().FilteringAttributes(...)`.
  The same shortcuts are on the `Vela.Testing` `PluginContextBuilder`. `.Mode(...)` still works.

## [0.1.22] - 2026-09-06

CLI line (`Vela.Cli`), published as `cli-v0.1.22`.

### Changed

- The scaffolded `AccountRelatedContactsPlugin` now showcases two more built-ins: it runs a strongly-typed
  **`QueryExpression`** (`Query.For<Contact>()...Build()`, alongside the existing `ToFetchXml()`) and serves the
  result through the built-in **`IPluginCacheService`** (read-through, 5-minute TTL via `GetOrCreate(...)`).

## [0.1.21] - 2026-09-06

CLI line (`Vela.Cli`), published as `cli-v0.1.21`.

### Changed

- Scaffolded plugins and `vela add` emit the `.Sync()` / `.Async()` shortcuts instead of
  `.Mode(ExecutionMode.Synchronous)`. The offline fallback framework version is bumped to `0.4.3` (the release
  that adds the shortcuts).

## [0.4.2] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.4.2`. Additive.

### Added

- Typed contexts for 7 more messages: `WhoAmI` (global message - `ctx.Response.UserId`/`BusinessUnitId`/
  `OrganizationId`), `WinQuote` and `ReviseQuote` (like Win/LoseOpportunity), `FulfillSalesOrder`, and the
  opportunity product-getters `GetQuoteProductsFromOpportunity`, `GetSalesOrderProductsFromOpportunity`,
  `GetInvoiceProductsFromOpportunity`. None carry a primary `Target`, so their handlers read message-specific
  request fields (`ctx.Request.QuoteClose`, `ctx.Request.OpportunityId`, ...) and, in a Post step, `ctx.Response`.

## [0.1.20] - 2026-09-06

CLI line (`Vela.Cli`), published as `cli-v0.1.20`.

### Changed

- `vela add plugin --message <M>` emits the typed `On<M>()` for the 7 messages added in framework `0.4.2`.

## [0.4.1] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.4.1`.

### Fixed

- **Test harness fires plugin steps for every modelled message.** The in-memory pipeline
  (`PluginPipeline` / `InMemoryOrganizationService`) previously only ran registered steps for
  Create/Update/Delete/Associate/Disassociate/SetState; executing any other message (the sharing messages, and
  the sales / queue / email / membership / case / contract messages added in 0.4.0) went straight to the store
  and fired nothing. Now `crm.Execute(new <Message>Request { ... })` fires the registered Pre/Post steps like any
  other plugin, with the request parameters exposed as `ctx.Request` (typed) and the response `Results` as
  `ctx.Response` in the Post stage. The data effect is not simulated for messages the fake does not model (a fake
  cannot reproduce the server logic), so the store still performs the operation when it recognises the request
  and the steps otherwise fire around a no-op. Messages with no registered steps are unchanged.

## [0.4.0] - 2026-09-06

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.4.0`. Additive (non-breaking):
extends the typed message set introduced in 0.3.0.

### Added

- **27 more modelled messages** get a typed `On<Message>()` builder and an `I<Message>Context<TEntity>` exposing
  the standard SDK `ctx.Request` / `ctx.Response` (from `Microsoft.Crm.Sdk.Messages`), inferred with no cast:
  - Sales lifecycle: `QualifyLead`, `WinOpportunity`, `LoseOpportunity`, `ConvertQuoteToSalesOrder`,
    `ConvertSalesOrderToInvoice`, `GenerateQuoteFromOpportunity`, `GenerateSalesOrderFromOpportunity`,
    `GenerateInvoiceFromOpportunity`.
  - Queues: `AddToQueue`, `RemoveFromQueue`, `PickFromQueue`, `RouteTo`, `ReleaseToQueue`.
  - Email: `SendEmail`, `SendEmailFromTemplate`, `DeliverIncomingEmail`, `DeliverPromoteEmail`.
  - Team / marketing-list membership: `AddMembersTeam`, `RemoveMembersTeam`, `AddListMembersList`,
    `AddMemberList`, `RemoveMemberList`.
  - Case / contract: `CloseIncident`, `CancelContract`, `CloneContract`.
  - Access (read): `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`.
- The messages whose request carries a primary record also expose it already typed: `ctx.Target`
  (`SendEmailFromTemplate`), `ctx.TargetReference` (`AddToQueue`, `RouteTo`, `RetrievePrincipalAccess`,
  `RetrieveSharedPrincipalsAndAccess`). The rest read message-specific request properties (for example
  `ctx.Request.LeadId`, `ctx.Request.OpportunityClose`, `ctx.Request.QueueItemId`).

## [0.1.19] - 2026-09-06

CLI line (`Vela.Cli`), published as `cli-v0.1.19`.

### Changed

- `vela add plugin --message <M>` now emits the typed `On<M>()` for the 27 messages modelled in framework
  `0.4.0` (sales lifecycle, queues, email, team/list membership, case/contract, access reads). Any unmodelled
  message still renders the permissive `OnMessage("...")`.
- The generated `AccountMessageReference.cs` cheat-sheet also showcases the account-valid sharing reads
  (`RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`) and points to the table-specific message
  families modelled in 0.4.0.

## [0.3.0] - 2026-09-05

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.3.0`.

### Added

- **Typed request/response per message.** A step handler now receives a message-typed context that exposes a
  strongly-typed `ctx.Request` (and, in a `PostOperation` step, `ctx.Response`) for the modelled messages -
  `Create`, `Update`, `Delete`, `CreateMultiple`, `UpdateMultiple`, `Retrieve`, `RetrieveMultiple`, `Associate`,
  `Disassociate`, `Merge`, `GrantAccess`, `ModifyAccess`, `RevokeAccess`. `ctx.Request` is the standard SDK
  request (`CreateRequest`, `UpdateRequest`, `MergeRequest`, ...), inferred from the message with no cast:
  `void OnUpdated(IUpdateContext<Account> ctx)` reads `ctx.Request.Target`, `void OnMerged(IMergeContext<Account> ctx)`
  reads `ctx.Request.SubordinateId`. New `ctx.GetRequest<TRequest>()` / `ctx.GetResponse<TResponse>()` context
  helpers expose the same typed views over the pipeline's Input/Output parameters. The primary record is also
  exposed already typed on the context, with no cast or type argument: `ctx.Target` (the early-bound entity for
  Create/Update), `ctx.TargetReference` (the `EntityReference` for Delete/Retrieve/Associate/Disassociate/Merge/
  GrantAccess/ModifyAccess/RevokeAccess) and `ctx.TargetCollection` (the `EntityCollection` for CreateMultiple/
  UpdateMultiple). A step registered through `OnMessage("...")` (a message Vela does not model) still receives the
  plain `ILocalPluginContext` and binds an untyped `.Handler(Action<ILocalPluginContext>)` (so a permissive step
  works inside a multi-step router; read its inputs with `ctx.GetRequest<TRequest>()` / `ctx.GetInputParameter`).

### Removed

- **BREAKING - the untyped `target` handler parameter is gone.** Step handlers and the single-step `Execute`
  override no longer receive a fabricated `TEntity target` second argument, and `.Handler(...)` no longer accepts
  a method **name string** or an `Action<ILocalPluginContext, TEntity>`. Handlers are now bound by **method group**
  to a message-typed context (`.Handler(OnUpdated)` where `void OnUpdated(IUpdateContext<Account> ctx)`), which is
  checked at compile time - binding an `OnUpdate()` step to an `ICreateContext<Account>` handler no longer compiles.
  The single-step override becomes `Execute(ILocalPluginContext ctx)`. Migration:
  - Multi-step: change each handler from `void Handle(ILocalPluginContext ctx, TEntity target)` to
    `void Handle(I<Message>Context<TEntity> ctx)` (or keep `ILocalPluginContext ctx` when you do not need the
    typed request), and change `.Handler(nameof(Handle))` / `.Handler("Handle")` to `.Handler(Handle)`.
  - Single-step: change `Execute(ILocalPluginContext ctx, TEntity target)` to `Execute(ILocalPluginContext ctx)`
    and read the record with `ctx.GetTarget<TEntity>()` (Create/Update), `ctx.GetTargetRef()`
    (Delete/Associate/Disassociate), or `ctx.GetRequest<TRequest>()`.
  - The framework no longer materializes a typed target from an `EntityReference`/`EntityMoniker` or an empty
    entity for target-less messages; read the id directly from the request or `ctx.GetTargetRef()` instead.

## [0.1.18] - 2026-09-05

CLI line (`Vela.Cli`), published as `cli-v0.1.18`. Aligns scaffolding with the framework `0.3.0` typed-handler API.

### Changed

- Scaffolded plugins and `vela add` now use the typed-handler API: multi-step handlers bind by method group to the
  message-typed context (`.Handler(OnX)` where `void OnX(IUpdateContext<Account> ctx)`), and single-step plugins
  override `Execute(ILocalPluginContext ctx)` (reading the record via `ctx.GetTarget<T>()` / `ctx.GetRequest<...>()`).
- `vela add plugin --message <M>` now emits the typed `On<M>()` for the newly modelled `Merge`, `GrantAccess`,
  `ModifyAccess` and `RevokeAccess` messages (added to the CLI's known-message list); the deprecated `SetState` /
  `SetStateDynamicEntity` / `Assign` still render `OnMessage("...")`.
- The offline fallback framework version referenced by `vela init` is bumped to `0.3.0` (it normally resolves the
  latest published version from nuget.org).

### Added

- `vela init` now generates an `AccountMessageReference.cs` cheat-sheet: one dormant (`.Disabled()`) handler per
  modelled message, showcasing every typed context and the `ctx.Target` / `ctx.TargetReference` /
  `ctx.TargetCollection` accessors. Safe to deploy (nothing fires) and meant to be copied then deleted.

## [0.2.1] - 2026-09-03

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.2.1`.

### Changed

- Docs only: the `Vela.Plugins` package readme (`NUGET.md`) plugin example now uses the typed stage chain
  (`.PostOperation()` instead of `.Stage(PluginStages.PostOperation)`), matching the 0.2.0 API. No code change;
  `Vela.Testing` is re-published only to keep the two framework packages in lockstep.

## [0.2.0] - 2026-09-03 (Vela.Plugins + Vela.Testing)

Framework line (`Vela.Plugins` + `Vela.Testing`), published as `framework-v0.2.0`.

### Changed

- `PluginCacheService` now takes its per-key lock from a `ConcurrentDictionary<string, object>` (one monitor per
  key) instead of a fixed 64-slot striped `object[]` pool addressed by hashing. This is a true per-key lock -
  unrelated keys never serialize on a shared stripe - and it drops the `LockStripeCount` constant and the
  hash-and-mask. Behavior is unchanged (same-key stampede protection; a cache-miss factory still runs once per
  key), lock objects are still never removed, and there is no public API change.
- **BREAKING - an invalid image/stage combination is now a compile error.** The step builder replaces the enum
  `.Stage(PluginStages.X)` with typed stage methods - `.PreValidation()`, `.PreOperation()`, `.PostOperation()`
  - and the (message, stage) pair you pick only exposes the images that combination actually supports. A wrong
  image no longer compiles, in the IDE, with no CLI needed: `OnDelete().PostOperation()....PostImage()` (Delete
  has no post-image), `OnUpdate().PreValidation()....PreImage()` (no images before the operation), and
  `OnCreate().PostOperation()....PreImage()` (Create has no prior state) all fail to build. `OnMessage("...")`
  for a non-modelled message stays permissive (both images offered; the platform validates at deploy). The
  runtime check in `Build()` remains as a backstop for out-of-order/reflection assembly, and is relaxed to only
  enforce the modelled messages (Create/Update/Delete and their bulk variants). Migration: replace
  `.Stage(PluginStages.PreValidation|PreOperation|PostOperation)` with the matching `.PreValidation()` /
  `.PreOperation()` / `.PostOperation()` call. The `PluginContextBuilder` test helper keeps its own
  `.Stage(PluginStages.X)` (a separate API) unchanged.

### Removed

- **BREAKING - `OnSetState()` / `OnSetStateDynamicEntity()` step methods.** `SetState` and
  `SetStateDynamicEntity` are deprecated by Dataverse (update `statecode`/`statuscode` via an `Update` instead),
  the test harness already models them as an `Update`, and the framework never registered images on them - so
  the typed methods were dead surface. Migration: register on `OnUpdate()` targeting `statecode`/`statuscode`,
  or use the permissive `OnMessage("SetState")` escape hatch if you must target the legacy message. The
  `PluginMessages.SetState` / `SetStateDynamicEntity` constants and the harness `SetStateRequest` emulation are
  kept.
- **BREAKING - the old step-builder interfaces** (`IStepMessageBuilder`, `IStepStageBuilder`,
  `IStepModeBuilder`, `IStepImageBuilder`, `IFilteringStepMessageBuilder`, `IFilteringStepStageBuilder`,
  `IFilteringStepModeBuilder`) are replaced by the typed stage chain (`ICreateStepStage`, `IUpdateStepStage`,
  `IDeleteStepStage`, `IReferenceStepStage`, `IPermissiveStepStage`, the capability terminals `INoImageStep` /
  `IPreImageStep` / `IPostImageStep` / `IPreAndPostImageStep`, and the generic `IStepConfig` / `IModeSelector` /
  `IFilteringSelector`). Code that named the old interface types directly must move to the new ones; the fluent
  chain itself reads the same apart from the stage method.

## [0.1.17] - 2026-09-03

CLI line (`Vela.Cli`), published as `cli-v0.1.17`.

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

- `vela init` now scaffolds plugin steps with the **typed stage chain** (`.PreValidation()` /
  `.PreOperation()` / `.PostOperation()` instead of `.Stage(PluginStages.X)`), matching the framework 0.2.0 API,
  and the offline-fallback framework version the scaffold pins is now **0.2.0** (online, `vela init` still
  auto-resolves the latest published `Vela.Plugins` from nuget.org). Regenerating a solution requires framework
  0.2.0 or later.
- `vela add` follows the framework 0.2.0 message surface: `--message SetState` / `SetStateDynamicEntity` now
  scaffolds the permissive `OnMessage("SetState")` (the typed `OnSetState()` was removed), and `--message
  CreateMultiple` / `UpdateMultiple` now scaffolds the typed `OnCreateMultiple()` / `OnUpdateMultiple()` with the
  required filtering placeholder (previously they fell through to a step that threw at registration).
- `vela init` no longer emits a `nuget.config` or a `local-packages/` folder. That was a workaround from before
  `Vela.Plugins` / `Vela.Testing` were published; they now resolve from **nuget.org** (the default source), so a
  generated solution restores and builds out of the box. Add your own `nuget.config` only for a private feed.
- When `plan` / `apply` / `inspect` can't read a plugin's registrations, the error now hints that a **stale
  package** is a common cause ("the package may be out of date - rebuild it (dotnet build -c Release)"). This is
  the usual reason the failure names an old message/stage that no longer matches the edited source.
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
