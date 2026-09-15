# Limitations & not supported

What Vela deliberately does **not** do, and the known gaps - so you can tell "by design" from "bug" before
you file one. Most constraints come from the Dataverse plugin sandbox, the preview-first `plan`/`apply`
scope, or the in-memory test double being a faithful-but-partial model. Vela is **pre-1.0**: APIs may still
change.

- [`Vela.Plugins` (runtime)](#velaplugins-runtime)
- [`Vela.Cli` (`plan` / `apply` / `init` / `add` / `models` / `sign`)](#velacli)
- [`Vela.Testing` (in-memory CRM & pipeline)](#velatesting)

## Vela.Plugins (runtime)

- **Cache is synchronous only.** `IPluginCacheService` exposes no async methods and is TTL-based with no size
  cap or eviction policy beyond expiry. The plugin pipeline runs synchronously and the cache locks with
  `Monitor` (the sandbox-safe primitive) - do your I/O inside the factory and cache the result.
- **DI lifetimes: transient and scoped only.** `SimpleServiceResolver` has no singleton lifetime - a
  singleton would leak across plugin executions sharing a sandbox process. The scope is one execution.
- **No runtime verbosity switch.** You decide *whether* to trace at the call site (`TraceMethod` /
  `TraceExecutionSnapshot`); *whether traces are persisted* is the environment's **Plugin Trace Log** setting
  (`Off`/`Exception`/`All`). Server-side plugins can't read OS env vars, so a runtime flag would be inert.
- **Managed identity is host-dependent.** `AcquireManagedIdentityToken(...)` works only where the runtime
  exposes `IManagedIdentityService`; otherwise it throws at call time (no early validation). Extended context
  members (`EnvironmentId`, `TenantId`, `IsApplicationUser`, …) are resolved reflectively and fall back to
  `null`/`false` on older hosts.
- **Step registration follows platform rules.** `MainOperation` is reserved for Custom API handlers (rejected
  for plugin steps); `FilteringAttributes` apply to `Create`/`Update` only (and Vela requires at least one there);
  images are constrained by stage/message
  (no images in `PreValidation`; `PreImage` only for `Update`/`Delete` in `PreOperation`; `PostImage` only for
  `Create`/`Update`). Violations throw.
- **No images on bulk messages when deploying.** Dataverse accepts no image on `CreateMultiple`/`UpdateMultiple`,
  so `plan`/`apply` reject a bulk step that carries a pre/post image (the in-memory harness still allows it, to
  simulate per-record bulk images). Register the image on the single `Create`/`Update` step, which also runs for
  bulk operations, and read per-record pre/post images by index, not by target reference.
- **Custom APIs run at `MainOperation`, synchronously** - that stage/mode is fixed.
- **Typed query builder = typed `QueryExpression`.** `Query.For<T>()` adds type safety and attribute-name
  mapping; it does **not** add capabilities beyond `QueryExpression` and does not author FetchXML (you can
  still pass a `FetchExpression` straight to the service).
- **Retry classifies transients with a fixed rule.** `RetryPolicy` retries `TimeoutException`,
  `WebException` and `FaultException<OrganizationServiceFault>` matching `timeout`/`throttle`/`temporar`, with
  attempt-scaled backoff - the classification isn't pluggable.

## Vela.Cli

`plan`/`apply` reconcile a focused set of registration objects; everything else in the environment is left
untouched.

- **Managed:** plugin **packages**/assemblies (uploaded from the `.nupkg`), plugin **steps**, step
  **images**, **Custom APIs** with their request/response parameters, and **web resources** (with
  `--webresources --solution`).
- **Not managed:** solutions (must already exist - `--solution` only adds components to it), SDK message
  filters (read-only lookup), step **enabled/disabled** state, secured/unsecured step **configuration**,
  security roles / field security, environment variables. Assemblies are upserted, never
  deleted.
- **Deletion is scoped to the package.** `apply` removes steps/Custom APIs that exist in Dataverse but are
  absent from the local package's own types - it will not delete unrelated registrations.
- **Custom API parameter types are limited** to Dataverse scalars + SDK types (bool, datetime, decimal,
  float, integer, guid, string, `Entity`, `EntityReference`, `EntityCollection`, `Money`, `OptionSetValue`,
  string array). Multi-select option sets and nested/complex types are not supported (unknown types fall back
  to `String`).
- **Some Custom API fields are create-only.** `IsFunction`, the binding type, the bound entity, the allowed
  custom-processing-step type, and `WorkflowSdkStepEnabled` are fixed when the Custom API is created; Dataverse
  ignores updates to them. `apply` detects such a change and fails fast (delete and recreate the Custom API to
  change one) rather than reporting an update that never lands.
- **Workflow-enabled Custom APIs are validated locally.** When `WorkflowSdkStepEnabled` is set, the Custom API
  must be an action (not a function) and cannot use `Entity`/`EntityCollection`/`StringArray` parameters; `apply`
  fails with a clear message instead of an opaque platform error. Under `--solution`, a Custom API's unique name
  must carry the solution publisher prefix (as web resources and plugin packages do) - `apply` checks this too.
  (Step and Custom API unique-name prefix enforcement applies only under `--solution`.)
- **An entity-scoped step must resolve a message filter.** If a step names an entity that has no
  `sdkmessagefilter` for its message (a typo'd or unsupported entity), `apply` fails rather than registering an
  unfiltered step that would fire for every entity.
- **Stage normalization:** async steps are forced to `PostOperation`; non-standard stage values default to
  `MainOperation`.
- **Packaging:** a plugin-package `.nupkg` (built via `Microsoft.PowerApps.MSBuild.Plugin`) is required - a
  bare `.dll` is not supported. Framework/Microsoft/Newtonsoft assemblies are excluded from registration.
- **Scanning runs package code (trust your packages).** Discovering steps/Custom APIs requires instantiating
  the plugin types and calling `GetStepDefinitions()` / `GetCustomApiDefinition()`, so `plan`/`apply` execute
  code from the scanned `.nupkg`. This is inherent (the definitions come from the plugin itself) - **only scan
  packages you build or trust.** An isolating load context would not change this; the code still runs.
- **Authentication:** connection string, client secret (env var / stdin / arg), and interactive MSAL
  (sovereign clouds via `--auth-client-id` / `--authority`). **No certificate auth** and **no managed-identity
  connection** (managed identity is only relevant to `vela sign` for MI plugins). Prefer `DATAVERSE_CLIENT_SECRET`
  or `--client-secret-stdin` (piped) over `--client-secret` (exposed to shell history / process listings; the
  CLI warns). `--client-secret-stdin` requires stdin to be piped, not an interactive terminal.
- **Secrets in output.** Secure step configuration is **redacted to a hash** in all `plan`/`inspect` output -
  the terminal summary, the JSON, and any `--out` plan file - changes are still detected, but the value never
  appears. Unsecure config is shown verbatim (non-sensitive by design).
- **No cross-operation transaction.** A partway failure leaves partial state; the resume report lists what
  completed. Upserts are idempotent - fix the cause and re-run. `plan`/`apply`/`inspect` never block on a prompt
  under redirected/piped stdin (safe for CI and agents); the optional package-assembly prompt and interactive
  OAuth prompts are terminal-only, and `apply` still refuses to write without `--auto-approve` when there is no
  stdin TTY.
- **`vela add`** generates one file per run (plugin/service/customapi/test/step/form-script/ribbon-script); a
  plugin/service needs an early-bound model type (`--entity`).
- **`vela models`** generates entity classes (typed properties, primary-id sync, XML doc, DataAnnotations,
  option-set enums local/global, multi-select as `IList<Enum>`, alternate-key reference factories) scoped by
  `--solution`/`--entities`, Custom API message classes via `--custom-apis`, and a dependency-light `Roles` class
  (real role privileges + root role id, in the shared models project) via `--roles`/`--solution-roles` (a
  `--roles` name not found in the environment is warned about and skipped; the run only fails when none resolve).
  **Out of scope:** relationship
  navigation properties + `OrganizationServiceContext`/LINQ (lazy loading/change-tracking don't fit the plugin
  sandbox - use `Query.For<T>()`), localized multi-language labels, and custom class/property casing - use
  `pac modelbuilder` if you need those. Role generation covers **table** privileges only - non-entity /
  miscellaneous privileges (Export to Excel, Act on Behalf of Another User, etc.) are deliberately skipped.
- **`vela sign`** accepts a cert by fingerprint (`CurrentUser\My`), `.pfx` (+ password/credential) or
  base64 `.pfx`, and delegates to `dotnet nuget sign`. `--pfx-credential` (Windows Credential Manager) is
  Windows-only; Key Vault / HSM signing is out of scope.

## Vela.Testing

The in-memory CRM (`InMemoryOrganizationService`) and `PluginPipeline` are a faithful-but-partial model. For
anything below, inject your own `IOrganizationService` via `WithOrganizationService(...)`.

- **`Execute` covers a common set:** `Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple`,
  `CreateMultiple`/`UpdateMultiple`, `Assign`/`Associate`/`Disassociate`, the metadata reads
  (`RetrieveEntity`/`RetrieveAllEntities`/`RetrieveAttribute`), and `ExecuteMultiple`/`ExecuteTransaction`
  (the latter rolls the whole batch back on failure). For any other message, inject your own service.
- **Non-CRUD "modelled" messages don't produce their server-side data effect.** A step registered on
  `QualifyLead`, `WinOpportunity`, `SendEmail`, `CloseIncident` and the like fires so you can test your own
  logic, but the record changes the platform would make are not simulated.
- **Execution-context identity is the configured caller.** `ctx.UserId`/`InitiatingUserId`/`BusinessUnitId`/
  `OrganizationId` come from `WithCurrentUser`/`WithCurrentBusinessUnit`/`WithOrganizationId`, and a cascaded
  plugin sees its caller's context as `ctx.ParentContext`.
- **Fault codes are partial.** A not-found `Retrieve`/`Update` throws `FaultException<OrganizationServiceFault>`
  with `ObjectDoesNotExist` (`-2147220969`), and a privilege denial carries `PrincipalPrivilegeDenied`; other
  faults (for example an `ExecuteMultiple` child failure) surface an `OrganizationServiceFault` with no
  `ErrorCode`. No formula/rollup/calculated columns, business rules, or duplicate-detection.
- **N:N `Associate`/`Disassociate` need a declared relationship.** Declare it with `WithManyToMany(...)` or via a
  registered `ManyToManyRelationshipMetadata` (the store has no live metadata otherwise); then Associate/
  Disassociate persist the intersect rows and `RetrieveMultiple` joins through them. `Associate` is idempotent
  (no duplicate-key fault), and a self-referential N:N maps its two columns positionally. A 1:N relationship is
  modelled by `Update`-ing the lookup column.
- **`RetrieveMultiple` is a common subset.** No query hints, `NoLock`, `FilterHint=union`, or faithful paging
  cookies. FetchXML support covers a common subset (incl. aggregates).
- **System columns are a simplified model.** `createdon`/`createdby`/`modifiedon`/`modifiedby`, the owner
  columns and `organizationid` are stamped from the configured actor (`WithCurrentUser` /
  `WithCurrentBusinessUnit` / `WithOrganizationId`) - not server-computed. Without `WithCurrentUser` a stable
  non-empty default user (`DefaultCallingUser`) is used. `statecode`/`statuscode`/`versionnumber` and
  `overriddencreatedon` backdating are also stamped; `WithClock(...)` and `WithIdSequence(...)` make timestamps
  and ids deterministic. Ownership type is inferred from the early-bound `OwnerId` property or declared
  explicitly (`AsOrganizationOwned` / `AsUserOwned` / `AsVirtual` / `AsElastic`); elastic tables are ownable.
  `Seed(...)` deliberately stamps nothing (raw setup).
- **Virtual tables.** `AsVirtual(...)` suppresses all system-column population; `AsVirtual(name, readOnly: true)`
  also rejects Create/Update/Delete (read-only external provider). A non-read-only virtual table still stores
  rows and lets you read/write them - full external-data-provider semantics aren't modelled.
- **Metadata is opt-in.** Table metadata is returned only for what you register with `WithEntityMetadata(...)`
  (build it with `FakeEntityMetadata`); `RetrieveEntity`/`RetrieveAllEntities`/`RetrieveAttribute` read it back.
  Alternate keys and auto-numbers only behave as you declare them (`WithAlternateKey` / `WithAutoNumber`).
- **The security model is opt-in and off by default.** With no `WithSecurity(...)` call there are no
  privilege/access checks (full access), exactly as before. When enabled it models security roles, privileges at
  the four access levels, business units, teams (incl. the Member's-privilege-inheritance mode), record sharing
  (PoA, incl. org-wide), inherited access from parental relationships, 1:N relationship cascades, access teams,
  **manager and position** hierarchy security, field-level (column) security, and link-entity (joined-table)
  read-trimming (a join to a record the caller can't read drops the primary row on an inner join, or omits the
  linked columns on a left-outer join, recursively through nested joins). Behavioural note: a parent's Delete
  plugins fire **after** its cascaded children are removed (the children's Delete plugins run first), rather than
  the parent's pre-stage seeing its children.

---

See also the [Testing guide](../guides/testing.md).
