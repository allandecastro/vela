---
_description: "What is out of scope, and where the harness differs from Dataverse"
---

# Limitations

This page lists what Vela does not do, by design, and the known gaps of each package. Check it before you file a
bug: it tells "by design" from "defect".

Most limits come from three places: the Dataverse plugin sandbox, the deliberately narrow scope of `vela plan` and
`vela apply`, and the in-memory test harness, which models Dataverse faithfully but not completely. Vela is
pre-1.0, so its APIs may still change.

- [Vela.Plugins (runtime)](#velaplugins-runtime)
- [Vela.Cli](#velacli)
- [Vela.Testing (in-memory Dataverse and pipeline)](#velatesting)

## Vela.Plugins (runtime)

- **The cache is synchronous only.** `IPluginCacheService` has no async methods, like the Dataverse pipeline it runs
  in. The eviction policy is not pluggable: the default `PluginCacheService` evicts the oldest entries over its cap
  (1024 by default, set through the `PluginCacheService(int maxEntries)` constructor). See
  [Caching](../guides/caching.md).
- **Dependency injection has transient and scoped lifetimes only.** `SimpleServiceResolver` has no singleton
  lifetime, because a singleton would leak across plugin executions that share a sandbox process. A scope is one
  execution.
- **There is no runtime verbosity switch.** You decide at the call site whether to trace (`TraceMethod`,
  `TraceExecutionSnapshot`), and you turn the automatic traces down per plugin class (`AutoSnapshotDetail`,
  `AutoTraceServiceCalls`, `AutoTraceHandlerEntry`). Whether traces are kept is the environment's plugin trace log
  setting (`Off`, `Exception`, `All`). A plugin cannot read OS environment variables, so a runtime flag would have
  no effect.
- **Managed identity depends on the host.** `AcquireManagedIdentityToken(...)` works only where the runtime provides
  `IManagedIdentityService`; otherwise it throws when called. Extended context members (`EnvironmentId`,
  `TenantId`, `IsApplicationUser`, ...) are read through their SDK interfaces (`IPluginExecutionContext2` to
  `IPluginExecutionContext7`). When the host's context does not implement an interface, its members keep an empty
  default: `null` for ids, `false` for flags, an empty string for `InitiatingUserAgent`, and empty collections for
  the bulk image collections.
- **The sandbox applies.** No filesystem access ([VELA019](analyzers.md#vela019)). Outbound calls go over HTTP or
  HTTPS to a named host, never an IP address ([VELA025](analyzers.md#vela025)). Execution time is limited. See
  [Access external web services](https://learn.microsoft.com/power-apps/developer/data-platform/access-web-services)
  and
  [Set Timeout when making external calls](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins)
  on Microsoft Learn, and [Respect the sandbox](../guides/plugins.md#respect-the-sandbox).
- **Plugins target .NET Framework 4.8 at most.** `vela init` scaffolds plugin projects on `net48`. The CLI warns when
  it reads a package that targets a later framework such as `net481`, and `vela apply` explains the cause when
  Dataverse rejects such a package. See [Target framework](../guides/migrating.md#before-you-start).
- **Step registration follows platform rules, and violations throw.**
  - `MainOperation` is reserved for Custom API handlers and rejected for plugin steps.
  - Filtering attributes apply to `Create`, `Update`, `CreateMultiple` and `UpdateMultiple` only, and Vela requires
    at least one there ([VELA020](analyzers.md#vela020)).
  - Images depend on the stage and the message: none at `MainOperation`, a post-image only at `PostOperation`, no
    pre-image on `Create` or `CreateMultiple`, no post-image on `Delete`, and no image at all on `RouteTo`
    ([VELA007](analyzers.md#vela007)).
- **Bulk-step images arrive only in the image collections.** On `CreateMultiple` (post-image) and `UpdateMultiple`
  (pre- and post-image), Dataverse delivers one image per target in `PreEntityImagesCollection` /
  `PostEntityImagesCollection`, never in `PreEntityImages` / `PostEntityImages`. Those collections can also hold
  images of other steps and of the platform, so read a record's image by index and alias
  (`ctx.GetPreImage(index, alias)`), not by target reference.
- **Custom APIs run at `MainOperation`, synchronously.** That stage and mode are fixed.
- **The typed query is a typed `QueryExpression`.** `Query.For<T>()` adds type safety and column-name mapping. It
  adds no capability beyond `QueryExpression`. `ToFetchXml()` renders the same query as FetchXML, and you can still
  pass a `FetchExpression` to the service.
- **Retry classifies transient faults with a fixed rule.** `RetryPolicy` retries only service-protection faults,
  recognised by their error code (number of requests, combined execution time, concurrent requests), never by
  message text. It waits for the fault's `Retry-After`, and rethrows when that wait exceeds `RetryPolicy.MaxDelay`
  (30 seconds); without one it uses jittered exponential backoff. Timeouts (`TimeoutException`, `WebException`, a
  fault whose message mentions `timeout`) are retried only with `retryOnTimeout: true`, for idempotent operations.
  The classification is not pluggable. Each wait is capped, not their sum: pass `maxElapsed` for an overall budget.
  Service-protection limits do not apply to data operations from a plugin, so the policy rarely fires on
  `ctx.Service` calls. It is meant for calls that leave the sandbox, and for code shared with a client.
- **No retry inside a transaction.** A failed call in a synchronous pre- or post-operation step aborts the database
  transaction, so a replay cannot succeed. Every retry entry point takes the context (`RetryPolicy.Execute(ctx, ...)`,
  or `ExecuteWithRetry(ctx, ...)` / `ExecuteIdempotentWithRetry(ctx, ...)` on the Vela base). Inside a transaction,
  the operation runs once.

## Vela.Cli

`vela plan` and `vela apply` reconcile a focused set of registration objects. Everything else in the environment
is left untouched.

- **What they manage:** plugin packages and their assemblies (uploaded from the `.nupkg`), plugin steps, step
  images, and Custom APIs with their request and response parameters. Web resources (`--webresources`) are a
  work-in-progress preview and will change.
- **What they do not manage:** solutions, SDK message filters (read only), security roles, field security and
  environment variables. Assemblies are created or updated, never deleted. A step's secure configuration is written
  only when `apply` is given its value (`--secure-config`); otherwise the environment keeps it (see
  [Write a plugin](../guides/plugins.md)).
- **One existing, unmanaged solution per run.** `plan` and `apply` require `--solution`. The solution must already
  exist and be unmanaged in the target environment: both commands check it right after connecting and stop
  otherwise. Vela adds the components it writes to that solution.
- **Deletion is limited to Vela's own plugin types.** `apply` removes the steps and Custom APIs of the package's
  Vela plugins (and of a Vela plugin class you deleted) that are no longer in the code. A classic `IPlugin` or
  `CodeActivity` class in the same assembly is not a Vela type: its registrations (made with the Plugin
  Registration Tool, for example) are left untouched and listed under `unmanaged` in the plan. An existing Custom
  API implemented by another assembly is never taken over: `apply` stops with an error that names its owner.
- **Custom workflow activities are not supported.** A `CodeActivity` class is not a Vela type: `plan` and `apply`
  neither register nor remove it, and `Vela.Testing` has no workflow runtime to run it. Keep registering and testing
  workflow activities with your existing tools.
- **Custom API parameter types are limited** to the Dataverse Custom API types: `bool`, `int`, `decimal`, `double`,
  `DateTime`, `Guid`, `string`, `string[]`, `Money`, `OptionSetValue`, `EntityReference`, `Entity` (or an early-bound
  entity) and `EntityCollection`. Any other type (`long`, `float`, enums, multi-select choices, nested or complex
  types) is rejected when the Custom API definition is built.
- **Some Custom API fields are set at creation only.** `IsFunction`, the binding type, the bound entity, the allowed
  custom processing step type and `WorkflowSdkStepEnabled` cannot be changed after creation. `apply` detects such a
  change and fails, rather than reporting an update that never lands: delete and recreate the Custom API to change
  one. A parameter's type, optionality and table are also fixed at creation: `apply` recreates that parameter.
- **Custom API shapes are validated locally.** `Build()` rejects what Dataverse refuses (a function without a
  response property, a workflow-enabled function, a workflow-enabled API with `Entity`, `EntityCollection` or
  `string[]` parameters, invalid or duplicate parameter names), so the error names the API. A Custom API's unique
  name must start with the solution's publisher prefix; `apply` checks it before writing.
- **An entity-scoped step must resolve a message filter.** If a step names a table that has no `sdkmessagefilter`
  for its message (a typo or an unsupported table), `apply` fails rather than registering a step that would fire
  for every table.
- **Stages are normalized.** Asynchronous steps are written at `PostOperation`, and an unrecognized stage value
  defaults to `PostOperation`.
- **Packages only.** `plan` and `apply` read plugin packages (`.nupkg`, built with
  `Microsoft.PowerApps.MSBuild.Plugin`). A bare `.dll` is not supported. Framework, Microsoft and Newtonsoft
  assemblies in the package are not registered.
- **Scanning runs package code, so only scan packages you trust.** Reading the steps and Custom APIs instantiates the
  plugin types and calls `GetStepDefinitions()` / `GetCustomApiDefinition()`, so `plan`, `apply` and `inspect` run
  code from the package. The scan runs in a child `vela` process with no connection, token or secret and an
  allowlisted environment. It is not a sandbox: the code runs with your OS rights. Only scan packages you build or
  trust.
- **Authentication.** A bare `--url` uses the ambient Azure credential (`DefaultAzureCredential`: `az login`,
  environment variables, workload identity and managed identity), the recommended default for CI and agents. Vela
  also supports a connection string, a client secret (environment variable, stdin or argument) and interactive MSAL
  sign-in (sovereign clouds through `--auth-client-id` and `--authority`). Vela has no certificate options of its
  own: a `--connection-string` is passed unchanged to the Dataverse `ServiceClient`, so a certificate connection
  string works (see [Authentication](../configuration/authentication.md#connection-string)). Prefer
  `DATAVERSE_CLIENT_SECRET` or `--client-secret-stdin` over `--client-secret`, which shows in shell history and
  process listings (the CLI warns). `--client-secret-stdin` requires piped stdin.
- **Secure configuration is redacted.** In all `plan` and `inspect` output (the terminal summary, the JSON and any
  `--out` plan file), a secure step configuration appears as a digest (an HMAC keyed per run). Changes are still
  detected, but the value never appears. Unsecure configuration is shown as is.
- **There is no cross-operation transaction.** A failure partway through leaves partial state, and the report lists
  what completed. Writes are idempotent: fix the cause and run `apply` again. `plan`, `apply` and `inspect` never
  wait on a prompt when stdin is redirected or piped, so they are safe for CI and agents. Interactive prompts work
  in a terminal only, and `apply` refuses to write without `--auto-approve` when stdin is not a terminal.
- **`vela add` adds one component per run:** a plugin, service, Custom API, test, step, form script or ribbon script.
  A plugin or service needs an early-bound model type (`--entity`).
- **`vela models` scope.** It generates entity classes, Custom API message classes (`--custom-apis`) and a `Roles`
  class (`--roles`, `--solution-roles`), including typed relationship navigation properties (see
  [Relationship navigation properties](../guides/models.md#relationship-navigation-properties)). A `--roles` name
  not found in the environment is skipped with a warning; the run fails only when none resolve. Out of scope:
  `OrganizationServiceContext` and LINQ (lazy loading and change tracking do not fit the plugin sandbox: use
  `Query.For<T>()`), localized labels in several languages, and custom class or property casing (use
  `pac modelbuilder` for those). Role generation covers table privileges only; miscellaneous privileges (Export to
  Excel, Act on Behalf of Another User, ...) are skipped. See [Generate models](../guides/models.md).
- **`vela sign` signs with a local certificate.** It accepts a certificate by fingerprint (`CurrentUser\My`), a
  `.pfx` file (with a password or a stored credential) or a base64 `.pfx`, and delegates to `dotnet nuget sign`.
  `--pfx-credential` (Windows Credential Manager) is Windows only. Key Vault and HSM signing are out of scope.

## Vela.Testing

The in-memory Dataverse (`InMemoryOrganizationService`) and `PluginPipeline` model the platform faithfully but not
completely. For anything listed below, a single-plugin test can inject its own `IOrganizationService` with the
context builder's `WithOrganizationService(...)`. `PluginPipeline` has no such fallback: it always drives its own
in-memory store, which you configure with the pipeline's `With*` and `As*` methods.

- **`PluginContext` runs one handler, not the registration.** It selects the step by message, stage, mode, entity
  and relationship scope, but does not apply `FilterOn`, the registered images, `.Disabled()`, the step
  configuration or impersonation. It traces a `Vela.Testing:` warning when an `Update` Target holds none of the
  filtered columns, or when a registered image is missing from the context. To test whether a step fires, use
  `PluginPipeline` (see [Testing](../guides/testing.md#run-a-plugin-against-a-fake-context)).
- **`Execute` covers a common set of messages:** `Create`, `Update`, `Delete`, `Retrieve`, `RetrieveMultiple`,
  `CreateMultiple`, `UpdateMultiple`, `Upsert` (by id or alternate key), `Assign`, `SetState`, `Merge`,
  `Associate`, `Disassociate`, `WhoAmI`, the sharing and record-team messages (`GrantAccess`, `ModifyAccess`,
  `RevokeAccess`, `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`, `AddUserToRecordTeam`,
  `RemoveUserFromRecordTeam`), the file block messages, the metadata reads (`RetrieveEntity`,
  `RetrieveAllEntities`, `RetrieveAttribute`), `ExecuteMultiple` and `ExecuteTransaction` (which rolls the whole
  batch back on failure). For any other message, inject your own service.
- **Business messages fire their steps, but their data effect is not simulated.** For `QualifyLead`,
  `WinOpportunity`, `CloseIncident`, `SendEmail` and the other business messages, the harness runs the registered
  steps with the request's inputs and the response's outputs, but it does not reproduce Microsoft's server logic:
  no lead is qualified, no opportunity is closed, no email is sent. Tests cover how your plugins react to the
  message, not what the platform does. The steps fire on the sdkmessage the request runs as (`Send` for
  `SendEmailRequest`), on the message's table, with an empty `ctx.PrimaryEntityId`.
- **Transactions are simulated with store snapshots.** The stage model follows Dataverse: PreValidation of the
  initial operation runs outside the transaction, reads are not transactional, asynchronous steps run after the
  commit, and a swallowed failed write ends the transaction while a swallowed failed read does not. There is no
  locking, isolation level or concurrency: operations run one at a time. Which messages count as reads is a name
  rule (`Retrieve*`, `WhoAmI`, file downloads). A read that a plugin issues inside a write's transaction runs within
  it, and its steps report `IsInTransaction` = `false`, as a top-level read's steps do. `InputParameters["FromSandbox"]`
  is set only on the PreValidation stage of a plugin's own operation.
- **Formatted values are partial.** Reads fill `FormattedValues` for lookups (the referenced record's primary name)
  and for choices whose labels are registered as metadata, in the registered label language only. Money, dates,
  numbers and Yes/No columns get no formatted value. Without metadata, the primary name is read from `name` (or
  `fullname`, `title` or `subject` for the common system tables that use those).
- **The execution context identity is the configured caller.** `ctx.UserId`, `InitiatingUserId`, `BusinessUnitId`
  and `OrganizationId` come from `WithCurrentUser`, `WithCurrentBusinessUnit` and `WithOrganizationId`. A cascaded
  plugin finds its caller's context through `ParentContext`: one level up for PreValidation, two for the later
  stages of Create, Update and Delete, whose direct parent is the operation's stage-30 context.
- **Steps on a global Custom API.** A global API's main operation sees `PrimaryEntityName` = `"none"`. A step on its
  message is declared through `EntityPluginBase<TEntity>` and so carries a table; the harness gives that step's
  context the step's table so the runtime still matches it.
- **Fault codes are partial.** The faults the harness raises itself carry the platform code: for example, not found
  `0x80040217`, duplicate record `0x80040237`, duplicate alternate-key value `0x80060892`, missing privilege
  `0x80040220`, record out of the caller's reach `0x80048306`, undeclared key `0x80060889`, no record for a key
  `0x80060891`, delete blocked by Restrict `0x80040227`, invalid argument `0x80040203`, the transaction faults and
  the Custom API input checks. Any other exception surfaces as an `OrganizationServiceFault` with no `ErrorCode`.
- **No server-side logic.** Formula, rollup and calculated columns, business rules and duplicate detection are not
  simulated.
- **N:N `Associate` and `Disassociate` need a declared relationship.** Declare it with `WithManyToMany(...)` or a
  registered `ManyToManyRelationshipMetadata` (the store has no live metadata otherwise). Associate and Disassociate
  then persist the intersect rows, and `RetrieveMultiple` joins through them. Associating an existing pair again
  throws a `DuplicateRecord` (`0x80040237`) fault, associating records outside the relationship's two tables throws
  an invalid argument (`0x80040203`) fault, and a self-referential N:N maps its two columns by position.
  Over a 1:N relationship declared with `WithRelationship(...)`, Associate sets the child's lookup column and
  Disassociate clears it, from either side. The 1:N relationship is found by the `schemaName` it was declared with,
  or by `{referenced}_{referencing}` when it was declared without one. `Merge` moves the subordinate's N:N rows and reparents its 1:N
  children, except over a relationship whose Merge behaviour is `NoCascade`.
- **`RetrieveMultiple` supports a common subset.** No query hints, `NoLock`, `FilterHint=union` or faithful paging
  cookies: pages are read by page number, 5,000 rows at most. FetchXML support covers a common subset, aggregates
  included; anything outside it throws `NotSupportedException` naming the element or attribute (see
  [Testing](../guides/testing.md#queries)).
- **System columns are a simplified model.** `createdon`, `createdby`, `modifiedon`, `modifiedby`, the owner columns
  and `organizationid` are stamped from the configured actor (`WithCurrentUser`, `WithCurrentBusinessUnit`,
  `WithOrganizationId`), not computed by a server. Without `WithCurrentUser`, a stable default user
  (`DefaultCallingUser`) is used. `statecode`, `statuscode`, `versionnumber` and `overriddencreatedon` backdating are
  stamped too. `WithClock(...)` and `WithIdSequence(...)` make timestamps and ids deterministic; the same clock
  expires the harness's plugin cache. The ownership type is declared explicitly (`AsOrganizationOwned`,
  `AsUserOwned`, `AsVirtual`, `AsElastic`), or read from the `OwnershipType` of registered metadata, or inferred from
  the early-bound `OwnerId` property; a table with none of these is user-owned. Elastic tables are ownable.
  `Seed(...)` stamps nothing, on purpose: it is raw setup.
- **Virtual tables are partial.** `AsVirtual(...)` turns off system-column population. `AsVirtual(name,
  readOnly: true)` also rejects Create, Update and Delete. A virtual table that is not read-only still stores rows
  and lets you read and write them: the semantics of an external data provider are not modelled.
- **Metadata is opt-in.** Table metadata is returned only for what you register with `WithEntityMetadata(...)`
  (built with `FakeEntityMetadata`); `RetrieveEntity`, `RetrieveAllEntities` and `RetrieveAttribute` read it back.
  Alternate keys and autonumber columns behave only as you declare them (`WithAlternateKey`, `WithAutoNumber`): a
  record addressed by `KeyAttributes` that are not a declared key faults.
- **The security model is opt-in and off by default.** Without a `WithSecurity(...)` call there are no privilege or
  access checks. When it is on, the harness models:
  - security roles (one copy per business unit, with `roletemplateid` on system roles), privileges at the four
    access levels, business units, and teams (including the member's privilege inheritance mode);
  - record sharing, including organization-wide access, and access teams;
  - access inherited through parental relationships (Reparent and Share cascades) and 1:N relationship cascades.
    A reparent re-derives access for the moved record only, not its descendants, and an Assign of the parent
    re-derives nothing. A setup call such as `WithSharedRecord` made after the test has started re-derives the
    seeded rows from the store's current state, except those that a Share or Unshare cascade reached at run time;
  - manager and position hierarchy security, and field-level (column) security;
  - the Read privilege on every table a read touches, linked tables included (a read without it throws
    `0x80040220`), and record trimming on joins: a join to a record the caller cannot read drops the primary row on
    an inner join, or omits the linked columns on a left outer join, recursively through nested joins;
  - the Read privilege a new record owner must hold, for owners declared in the model; an owner id the model does not
    know is not checked. A user owner counts Read from any of its roles, whatever its teams' inheritance mode.

  With security on, a lookup set by id must point at an existing record; with it off, a lookup may point at a record
  the test never seeded. A parent's Delete PreValidation steps still see its cascaded children; by PreOperation they
  are gone, because the children's Delete plugins run in between. Step images carry only the columns the calling
  user may read, like a Retrieve: a secured column without field read permission is absent from them. For a step
  that impersonates another user, the harness still filters the images by the calling user's access.

  Not modelled:
  - **Ownership across business units.** Each user or team belongs to one business unit (`User(id, businessUnit,
    roles)`), and its roles apply in that business unit. With security on, a record's `owningbusinessunit` always
    follows its owner's business unit. A role assigned from another business unit, or a record whose owning business
    unit differs from its owner's (modernized business units), is not modelled.
  - **Filtered record ownership** (a preview feature of Dataverse).

See also the [Testing guide](../guides/testing.md).
