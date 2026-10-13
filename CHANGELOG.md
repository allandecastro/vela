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

### Changed (Framework, breaking)

- **Breaking:** `PluginContext` resolves the trigger from the plugin's registration part by part. The message, stage
  and mode a test does not set come from the registered steps that match the parts it does set and the primary
  entity, so `.OnCreate()` alone runs a plugin's only `Create` step (it used to keep the fake defaults, PostOperation
  and synchronous, and fail with "no registered step matching message 'Create'"). When several steps match, or none,
  `Execute` / `Build(plugin)` throw an `InvalidOperationException` listing the registered steps
  (`Create/PreOperation/Sync on account [step-key]`). A multi-step plugin run with no trigger now throws the same way
  instead of running under the fake defaults (Update, PostOperation, synchronous). A test that relied on those
  defaults states its trigger.
- **Breaking:** `PluginContext` sets `ctx.PrimaryEntityId` to the `Target`'s id (an entity or an `EntityReference`),
  and to `Guid.Empty` without a Target or on a global message such as `Associate`. It used to be a random id unless
  `WithPrimaryEntity(name, id)` set it, which still wins, as does an id set on `Context`.
- **Breaking:** running a Custom API through `PluginContext` validates its request against the definition with the
  same faults as `PluginPipeline`: a missing required input (`0x80040203`), an input of the wrong type or an
  undeclared one (`0x80040315`), a bound API without its `Target`. `.WithoutRequestValidation()` turns it off for a
  test that sends an invalid request on purpose.
- **Breaking:** a Custom API run through `PluginContext` (`Execute(api)` / `Build(api)`) sees the primary entity of
  its binding, as with `PluginPipeline` (verified live): `PrimaryEntityName` `"none"` and an empty `PrimaryEntityId`
  for a global API, the bound table and the `Target` id for an entity-bound API, the bound table and an empty id for
  an entity-collection-bound API. It used to see the builder's default (`account`). `WithPrimaryEntity(...)` still
  wins.

### Added (Framework)

- **`ctx.Sides()` on the Associate / Disassociate contexts** splits the link by the plugin's table instead of by the
  caller's direction. These messages have no primary entity, so the `T` of `EntityPluginBase<T>` does not filter
  them and `ctx.Request.Target` is whichever end the caller passed (associating contacts from an account gives an
  account `Target` to a contact plugin). `Sides()` returns `ThisSide` (the records of `T`), `OtherSide` and
  `Relationship`, the same whichever end the caller started from; on a self-referential relationship `Target` is
  this side. The plugins guide (Relationship scope) now documents this.

- `PipelineExecutionEntry.HandlerInvoked` and `PipelineExecutionEntry.SkipReason` report whether a step's handler ran.
  An entry in `PluginPipeline.ExecutionLog` means the platform invoked the step. `HandlerInvoked` is `true` only once
  the handler is actually called. When Vela's runtime skips the handler (a `.ForRelationship(...)` scope that does
  not match, `.SkipWhenDepthAbove(n)`, the plugin-level depth guard), `SkipReason` holds the line it traced; a failure
  before the handler (constructor, service registration) leaves `HandlerInvoked` `false` with no `SkipReason`.
  `ToString()` also shows a handler that did not run.

### Changed (Framework)

- `PluginContext` reads the step registration before running the plugin whatever the trigger, so an invalid
  registration fails with its own reason ("Could not read the step registration of ...") instead of the plugin's
  generic "An unexpected error occurred. Correlation id: ..." message.
- `PluginContext` traces a warning in `TraceCapture`, prefixed `Vela.Testing:`, when the resolved step filters on
  attributes (`FilterOn`) that the `Update` Target holds none of, or registers an image the context does not carry. It
  still runs the handler: use `PluginPipeline` to assert whether a step fires.
- The "no registered step matching" error of the runtime dispatch also names the stage, the mode and the entity.

### Fixed (Framework)

- `PluginPipeline.ExecutionLog` was documented as the steps that fired, but it also listed steps whose handler
  Vela's runtime skipped, with nothing to tell them apart. Those entries now carry `HandlerInvoked = false` (see
  above), and the docs say an entry means "invoked by the platform".

### Added (CLI)

- **`vela init --integrations`** adds the `{prefix}.Integrations` plugin project, and its `{prefix}.Integrations.Tests`
  project unless `--no-tests`, without `--samples`: an empty second plugin package with the same build setup as
  `{prefix}.Plugins`. `--managed-identity` now implies it, so it adds the Integrations project with its signing step
  instead of being ignored without `--samples`. With `--samples` the behaviour is unchanged. `--integrations` or
  `--managed-identity` together with `--no-integrations` is refused.

### Changed (CLI)

- **`vela add test` reads the class under test from source**, in the projects the test project references (else the
  solution), and fails with a clear message when it finds none. A plugin test takes the plugin's namespace, its
  entity (`--entity` is now optional) and its steps from the source. A Custom API test finds the class named
  `<Name>` or `<Name>CustomApi`, or the one registering `<prefix>_<Name>`, and takes its class, namespace, unique
  name and request type from it: `vela add test AccountEstimateValue --kind customapi` now tests the sample
  `AccountEstimateValueCustomApi` and its `EstimateValueRequest`. The Custom API test file is named after the class.
- **A generated plugin test drives the step through `PluginPipeline`** and asserts from its `ExecutionLog` that the
  step fired, instead of asserting the trace line the `add plugin` handler writes (which a real handler removes).
  The request is sent the way a caller sends that message, setting the step's `FilterOn` columns on a
  `Create`/`Update`. A second, skipped test is the skeleton for asserting what the plugin does.
- The Custom API test from `vela add test --kind customapi` ships its output test skipped
  (`Skip = "TODO: set the inputs <Api> requires"`): `PluginContext` now validates the request against the definition,
  so the test needs the API's required inputs before it can run. A bound API's test supplies a `Target` of its table.

### Fixed (CLI)

- The `vela solution show` help example passed the publisher prefix to `vela init --prefix`, which is the namespace
  prefix. It now feeds `vela init --publisher-prefix`, and a second example feeds `vela add customapi --prefix`.
- The `AGENTS.md` that `vela init` writes no longer describes how to skip the `vela apply` confirmation; it says to
  apply only when the user asks.
- The C# blocks of the scaffolded `AGENTS.md` now start with the `using` directives they need, so a block pasted
  into a file created by `vela add` compiles (the Custom API block failed without `using Vela.Messages;`).
- The scaffolded README, `AGENTS.md` and the `vela init` next steps now show the same `vela models` command
  (`--entities account,contact --project src/<Prefix>.Shared/<Prefix>.Shared.csproj --interactive`), instead of three variants.

- **`vela add test --message Associate`/`Disassociate` no longer seeds an entity `Target`**: the test associates two
  records over a (TODO) many-to-many relationship through the pipeline. `Delete`, `Retrieve` and `Assign` act on a
  seeded record by reference, `RetrieveMultiple` sends a query, and any other message sends the step's own request.
- **`--filtering-attributes` with a column logical name compiles.** `add plugin`/`add step` map `telephone1` to its
  property through the model's `[AttributeLogicalName]`, not only by case-insensitive property name, and find the
  model under a `.slnx` solution root too. Without a model, an all-lower-case value is refused with a message to run
  `vela models` or pass the property name, instead of emitting a `FilterOn(e => e.name)` that does not compile.

- `vela models --custom-apis` with `--package`/`--packages-dir` now excludes only the **typed** Custom APIs authored
  in the package (`CustomApiBase<TRequest, TResponse>`, whose request/response classes are already in your code).
  Lightweight Custom APIs (`RegisterCustomApi(name, handler)`) have no request class, so they are generated again
  from the environment, with a note of how many were kept.

- The bound Custom API test scaffolded by `vela add test --kind customapi --entity <Table>` sets the request's `Target`
  to a record of the bound table, which `PluginContext` now requires as Dataverse does.

## [0.33.0] - 2026-10-12 (Vela.Messages + Vela.Plugins + Vela.Testing)

### Changed (breaking)

- **Breaking:** `OnReschedule()` now accepts only the tables Dataverse has a Reschedule filter for: `appointment`,
  `serviceappointment` and `recurringappointmentmaster`. On any other table `Build()` rejects the step instead of
  `vela apply` failing on the missing sdkmessagefilter.

### Fixed

- Registration errors now name a generic request or message as C# spells it (`On<CreateRequest<Account>>()`,
  `UnknownMessage<Account>`), not by its metadata name (``CreateRequest`1``): both the one-way trigger error and the
  "Cannot resolve the message" error.
- **A step on a sibling table no longer diverts a business message from the store.** An opportunity `Win` step made
  a `WinQuoteRequest` take the modelled path and return an empty response. The pipeline now checks the request's
  primary entity with the same rules it uses to fire the steps.
- **On a `SetStateRequest`, a `SetStateDynamicEntity` pre step that changes `State` or `Status` now changes the
  state written.** The inner Update is built from the inputs as the PreValidation and PreOperation steps left them.
  (The `SetStateDynamicEntity` operation nested in an `Update` of `statecode` still writes the row's own values.)
- **With security on, a lookup with an empty id and no key now faults as a missing record** on Create and Update,
  instead of persisting a dangling lookup.
- **A lookup set by alternate key is now checked like the same lookup set by id.** The key is resolved before the
  existence and record-scoped `AppendTo` checks, so a caller with User or Business Unit `AppendTo` can no longer link
  by key to a record outside that scope.
- **Merge on a self-referential N:N drops the row that linked the subordinate to the master**, instead of turning it
  into a master-to-master self-link.
- **Deleting a record deletes its files.** `DeleteFile` on a file id whose record is gone now faults as not found,
  instead of succeeding without any Write check.
- **`RetryPolicy` re-checks `maxElapsed` after each wait.** When a wait overruns the budget (scheduling, a GC pause),
  no further attempt starts and the last fault propagates.
- The setup error for a plugin that declares nothing now names `RegisterPluginSteps()`, not the removed
  `RegisterSteps`.
- Associate/Disassociate steps scoped to different relationships no longer run a handler twice: the executing step
  now picks its own definition before the relationship scope is checked.
- Two steps whose images share an alias and columns but read different request properties (`Target` and
  `SubordinateId` on Merge) now get distinct step keys instead of being rejected. Existing step keys are unchanged.
- VELA008 now flags two chains with the same constant `ForRelationship(...)` scope, and a constant
  `WithStepKey(...)` used twice in a plugin. It also compares a type-stated trigger under the sdkmessage it
  registers on, so `On<SendEmailRequest>()` and `On<SendFaxRequest>()` collide on `Send`.
- VELA011 now covers readonly `IList`, `IDictionary`, `Collection<T>`, `ObservableCollection<T>`,
  `KeyedCollection<TKey, TItem>`, `PriorityQueue<TElement, TPriority>`, `NameValueCollection`, `StringCollection`
  and `OrderedDictionary` fields.
- VELA021 now names the alias the fallback read needs: `ctx.GetPreImage<T>("SubordinatePreImage")` for a skipped
  subordinate image, and the custom alias when there is one. The parameterless form read the master image.
- VELA024 now flags `ConfigureAwait(...).GetAwaiter().GetResult()` on `Task`, `Task<T>`, `ValueTask` and
  `ValueTask<T>`.
- **`WithStepKey(...)` rejects a key that cannot be persisted.** A key is stored in a one-line
  `[vela:stepkey=...]` marker in the step description, so a key with a line break, another control character or
  `]` (such as `"phase\ntwo"`) could not be read back, and the step lost its stable identity. It now throws an
  `ArgumentException` that says why.

## [0.24.1] - 2026-10-12 (Vela.Cli)

### Fixed

- **A quoted secret is masked whole.** The error and log redactor read a connection-string value only up to the
  next `;`, so `ClientSecret="abc;def"` printed as `ClientSecret=***;def"`. A quoted value (`"..."` or `'...'`, with
  a doubled quote as the escape) is now masked up to its closing quote, and an unterminated one to the end of the
  text.
- **A created step's disable is never split from its create.** With `--batch-size 1`, or at a chunk boundary, the
  update that disables a just-created `.Disabled()` step went out in another `ExecuteMultiple` than the create, so a
  crash between the two left the new step enabled. The pair now always shares one request.
- **A removed Custom API is re-checked right before its batched delete.** Its owner was checked when the deletes
  were collected, but they ran later. An API detached or taken over by another plugin type in between is now left
  in place, with its parameters, and the run fails with the ownership message.
- **An unchanged plugin package over 4 MB compares as unchanged when Dataverse reports no chunking support.**
  The stored file is then downloaded in one block, as Dataverse requires, instead of 4 MB blocks.
- **A step key is read only from the trailing `[vela:stepkey=...]` line.** A marker quoted elsewhere in a
  `WithDescription(...)` text was taken for the step's key, and the description cut at it.
- **A declared label that differs from its fallback only in case is a change.** A Custom API display name or
  description, or a step description, declared as `zava_dothing` for the unique name `zava_DoThing` compared equal
  to a live `zava_DoThing`, so `plan` hid the change. Only a label still at its fallback is compared without case.
- **`vela add --message` resolves an sdkmessage to the request on the entity's table.** `--message Close` on
  `Quote` produced `OnCloseIncident()`, and `--message SetStateDynamicEntity` an `On<SetStateDynamicEntityRequest>()`
  that does not exist. They now give `On<CloseQuoteRequest>()` and `On<SetStateRequest>()`. An sdkmessage with no
  request on the table (`Close` on `Account`), or with several (`Execute`), fails with the request names to pass.
- **The plugin package framework hint covers a wrapped fault and net481.** A rejection wrapped by `ServiceClient`
  missed the net472-net48 hint, and a net481 package was told it registers on Dataverse 9.2.26092 or later. net481
  now gets its own message: Dataverse supports plug-ins up to net48.
- **The `vela init` samples trace ids only.** The message reference's `OnCreate` traced the account name.
- **The scaffolded AGENTS.md matches `--no-tests`.** It no longer names `Vela.Testing` or has a "Write a test"
  section when the solution has no test project.

## [0.32.0] - 2026-10-12 (Vela.Messages + Vela.Plugins + Vela.Testing)

### Changed (breaking)

- **`On<SendFaxRequest>()` and the request-typed forms of the 13 messages that gained a typed trigger now throw
  (#405).** Use the typed trigger (`OnSendFax()`, `OnRecalculate()`, ...); VELA023 offers the fix. See Added below.
- **`OnMessage(string)` is removed: every step trigger names its message by a type (#403).** Use a curated trigger
  (`OnCreate()`, ...), `On<XxxRequest>()` over an SDK request or one generated by `vela models --messages` /
  `--custom-apis` (which also emits an entity-constrained `On<Message>()`), or `OnMessage<TMarker>()` over a class
  marked `[CustomMessage("...")]`. `OnMessage<TMarker>()` / `On<TRequest>()` on a type with no `RequestName`,
  `[CustomMessage]` or `[SdkMessage]` now throws instead of registering the step on the type's name. Analyzer **VELA006** (prefer the
  typed trigger over `OnMessage("create")`) and its code fix are retired; VELA007, VELA008, VELA020 and the typed
  image projection now read the message from the request or marker type. `SetState` joins the request table:
  `On<SetStateRequest>()` registers on `SetStateDynamicEntity`, the message a state change fires, so VELA022 now
  reports only a type pinned with `[SdkMessage("SetState")]`. The test harness keeps
  `PluginContext.For<T>().OnMessage(...)` and `PluginContext.ForMessage(...)`.
- **The test harness follows Dataverse in more edge cases (#451).** A test that relied on the old harness
  behaviour now fails the way it would against Dataverse:
  - `ne`, `not-in`, `not-like`, `not-begin-with` and `not-end-with` match rows whose column is null; `like` (even
    `'%'`) and `ne-userid` no longer do. Distinct compares text case-insensitively. (Verified live.)
  - A record addressed by `KeyAttributes` that are not exactly one key declared with `WithAlternateKey` faults with
    `0x80060889`, also on a table with no key. A declared key that matches nothing faults with `0x80060891` (it was
    `0x80040217`). (Verified live.)
  - `QueryByAttribute` with a different number of values and attributes faults with `0x8004110F`; a missing value
    was compared as null. (Verified live.)
  - A query with no page size returns at most 5,000 rows with `MoreRecords`, and a larger page size is capped at
    5,000. FetchXML `top` with `returntotalrecordcount` faults with `0x80040203` (verified live).
  - An `Update` with `ownerid = null` faults with `0x80040203`; it reassigned the record to the caller. (Verified
    live.)
  - With security on, a lookup set by id to a missing record faults (`0x80040217` on Create, `0x80040502` on Update).
    (Verified live.)
  - Grant, Modify and Revoke access (verified live), and adding or removing a record-team member, fault with
    `0x80040217` on a record that does not exist.
  - A delete blocked by a Restrict relationship faults with `0x80040227` (verified live), and downloading a missing
    record or an empty file column faults with `0x80040217`, instead of a bare `InvalidOperationException`.
  - `SequentialIds.At(0)` and `SequentialIds.Starting(0)` throw: index 0 was `Guid.Empty`. An index above
    `SequentialIds.MaxIndex` throws `ArgumentOutOfRangeException` instead of `FormatException`.
  - `PluginContextBuilder.Execute` throws, naming the plugin, when reading the plugin's step registration fails. It
    used to run the plugin under the default trigger.
  - Step pre- and post-images (bulk image collections included) leave out a secured column the calling user has no
    field read permission on, as a `Retrieve` does. They carried the stored value.
- **Runtime edge cases that were silent now fail or behave as documented (#447).**
  - The typed requests' `Target` (`CreateRequest<T>`, `UpdateRequest<T>`, `UpsertRequest<T>`,
    `SendEmailFromTemplateRequest<T>`) throws the same `InvalidPluginExecutionException` as `ctx.GetTarget<T>()`
    when `Target` is absent or not an entity, instead of returning null.
  - `AssociateRelated` throws, naming each offending entry, when a related record has no id, and associates nothing.
    It used to skip those records.
  - `RetrieveWithRelated`: including the same relationship twice throws. The second `Include` used to replace the first.
  - `UpsertByKey` upserts a copy keyed by exactly the given key. It no longer merges into the entity's existing
    `KeyAttributes` or modifies the entity, and it rejects an entity with an id or a key without key attributes.
  - A lightweight Custom API handler must take exactly `ILocalPluginContext`. A derived parameter type was accepted
    and then failed at invocation.
  - `StepDefinitionReader.GetStepDefinitions(assembly)` throws, listing every plugin or type it could not read. It
    used to skip them silently. The new overload with a diagnostics collection keeps the best-effort scan.
  - `IsBulkOperation` and `BulkTargets` are set only for CreateMultiple, UpdateMultiple and UpsertMultiple, not for
    any message with an `EntityCollection` input named `Targets`.
  - The injected `IEnvironmentVariableReader` rejects Secret-type variables, like the context extensions did.
  - An explicit `ctx.TraceExecutionSnapshot(phase)` defaults to `TraceSnapshotDetail.Metadata`, like the automatic
    snapshot. Pass `TraceSnapshotDetail.Full` to trace values.
  - A message template whose placeholders do not match its arguments throws an `InvalidPluginExecutionException`
    naming the key and the language, from `TryGetMessage` too, instead of a bare `FormatException`.
- **One way to register a step per message.** `On<TRequest>()`, `OnAsyncOnly<TRequest>()` and `OnMessage<TMessage>()` now reject a request
  whose message has a typed trigger (`On<UpdateRequest>()`, `On<AssignRequest>()`, `On<CreateMultipleRequest>()`,
  `On<Vela.Messages.CreateRequest<T>>()`, ...): registering the step throws, naming the trigger to use (`OnUpdate()`).
  They stay the way to register Custom APIs and messages without a typed trigger. The new analyzer **VELA023** (error)
  reports it at build with a code fix to the typed trigger. The list of typed triggers is generated from the message
  model.
- **A typed trigger bound to one table is rejected on any other (#461).** `OnWinQuote()` and `OnWinOpportunity()`
  both register on `Win`, on quote and opportunity, so on the wrong table a step would run with the wrong typed
  request. The step builder now rejects a typed trigger on a table its message does not run on: `OnQualifyLead()`
  (lead), `OnWinOpportunity()` / `OnLoseOpportunity()` (opportunity), `OnWinQuote()` /
  `OnGenerateQuoteFromOpportunity()` (quote), `OnConvertSalesOrderToInvoice()` (salesorder), the queue triggers
  (queueitem), the email triggers (email), the team triggers (team), the list triggers (list), `OnCloseIncident()`
  (incident) and the contract triggers (contract). Apply already failed on most of these (no message filter for the
  table). Verified live through `vela plan` and `vela apply`.
- **Every SDK request is translated to its sdkmessage, not only the modelled ones (#461).** The request-name table
  now holds the 118 public requests whose name is not an sdkmessage, read from `sdkmessagerequest` (for example
  `CloseQuote` → `Close`, `CancelSalesOrder` → `Cancel`, `SendFax` → `Send`), so `On<CloseQuoteRequest>()`
  registers on `Close` too.
- **Several steps may share a trigger, and the rank is no longer part of a step's identity.** Two steps on the same
  message, stage, mode and entity were rejected; they are now allowed when their filtering attributes, images,
  relationship scope or step key tell them apart, and the executing step (`OwningExtension`, resolved by id for an
  async step, whose name Dataverse leaves null) selects its handler. The automatic step key no longer includes the
  rank, so a rank change updates the step in place; the first step of a trigger keeps its earlier key at the default
  rank, and a keyless step deployed at another rank gets a new key once (the first `vela plan` shows an update).
  Two steps that still cannot be told apart need a `WithStepKey(...)`. VELA008 flags only those.
- **`ISyncAsyncMode<T>` and `IAsyncMode<T>` gain `Async(bool deleteJobOnSuccess)`.** A class implementing them must
  add it.

- **`PluginPipeline` caps the cascade at the Dataverse depth (#450).** The default `WithMaxDepth` cap was 16; it is
  now `PluginBase.DefaultMaxDepth` (8), the depth at which Dataverse aborts a chain as an infinite loop. A test whose
  cascade runs deeper than 8 (a plugin with a raised `maxDepth`, or a classic `IPlugin`) now fails with the pipeline's
  error. Call `WithMaxDepth(16)` to keep the old cap.
- **An unset `HookOrganizationService` hook throws (#439).** `Create`, `Retrieve`, `Execute`, `RetrieveMultiple`,
  `Update`, `Delete`, `Associate` and `Disassociate` used to return `Guid.Empty`, `null` or an empty collection, or
  do nothing, when their `On...` delegate was not set, so the plugin failed later with a `NullReferenceException`
  (or passed without the write it needed). Each now throws a `NotSupportedException` naming the hook to set. This
  includes the default services of `PluginContextBuilder`: set the hook, or inject a store with
  `WithOrganizationService(...)`.
- **The pipeline fires the steps Dataverse fires for owner and state changes (#439, verified live).** An `Update`
  that mixes `ownerid` or `statecode`/`statuscode` with other fields is one execution, no longer two (Microsoft Learn
  describes a split that the platform does not do). An `AssignRequest` fires the `Assign` steps around an `ownerid`
  Update; it used to fire no Update step at all, and with an `Assign` step registered, ran that step without the
  Update. A `SetStateRequest`, and an `Update` that sets `statecode`, fire the `SetStateDynamicEntity` steps (nested
  at depth + 1 for the Update). The Update inside Assign or SetState has no PreValidation stage. Steps on `SetState`
  never fire, as on the platform.
- **A pipeline registration that would never fire fails the setup (#439).** `PluginPipeline.FromAssembly` threw away
  a step or Custom API whose type it could not resolve, and `Register<T>()` / `Register(Type)` added nothing for a
  type that declares no step and no Custom API. Both now throw an `InvalidOperationException` naming what was
  skipped.
- **Removed the 8 typed triggers Dataverse never accepts a custom step on (#461).** `OnReviseQuote()`,
  `OnFulfillSalesOrder()`, `OnConvertQuoteToSalesOrder()`, `OnGenerateSalesOrderFromOpportunity()`,
  `OnGenerateInvoiceFromOpportunity()` and the three `OnGet*ProductsFromOpportunity()` are gone, with their typed
  contexts and `PluginMessages` constants. Their sdkmessage filter has `iscustomprocessingstepallowed = false`, so
  registering a step fails with 0x80044187 (verified live): code using them could never be deployed.
- **`ImpersonationSource` and `WithImpersonation(ImpersonationSource)` are removed.** Nothing ever wrote the enum
  to Dataverse: a step declared `.WithImpersonation(ImpersonationSource.PluginOwner)` ran as the calling user.
  `StepDefinition.ImpersonationSource` is gone too. Use `.WithImpersonation(Guid systemUserId)` to run a step as a
  specific user, or drop the call to run as the calling user (the default).
- **Application Insights goes through the platform's `Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`, and
  `Microsoft.Extensions.Logging` is gone.** The sandbox never serves a `Microsoft.Extensions.Logging.ILogger`, and
  the reflection bridge to the native logger looked up members that don't exist, so `ctx.Log*`, `ctx.LogMetric` and
  `ctx.BeginLogScope` reached only the plug-in trace log and Application Insights received nothing. The context now
  calls the platform logger directly, with no reflection. `ctx.Logger` and the injected `ILogger` are
  `Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`, and `ctx.Log` takes `Microsoft.Xrm.Sdk.PluginTelemetry.LogLevel`
  (same member names, so swap the `using`). `Microsoft.Extensions.Logging.Abstractions` is no longer a dependency,
  so it and `System.Diagnostics.DiagnosticSource` leave the plugin package.
- **`ctx.LogMetric` takes a `long` and sends its properties.** The platform's metric API only takes integers.
  The properties now travel as metric dimensions (invariant strings, on top of the default dimensions), and the
  metric is no longer duplicated as an Information log entry.
- **Vela.Testing: `RecordingLogger` implements `Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`** (the shape the
  platform serves), `LogEntry.Level` is a `PluginTelemetry.LogLevel`, and metrics are recorded in
  `RecordingLogger.Metrics`. `WithLogger(...)` takes a `PluginTelemetry.ILogger`. `PluginPipeline.LogCapture` is
  new and records the whole cascade.

- **`ctx.EnvironmentId` is a `string?`, and now carries the value.** The platform declares
  `IPluginExecutionContext6.EnvironmentId` as a string, but Vela read it as a `Guid?`, so it was always null and the
  execution snapshot always printed `<none>`. It now returns the platform string (null when missing or empty).
  Code that used it as a `Guid` must parse it (`Guid.TryParse(ctx.EnvironmentId, out Guid id)`).
- **An organization service fault from a plugin reaches the caller unchanged.** `PluginBase` wrapped a
  `FaultException<OrganizationServiceFault>` (duplicate key, missing record, missing privilege, exhausted
  throttling...) in an `InvalidPluginExecutionException`, so the caller got the generic `0x80040265` instead of the
  fault's own code. The fault is now traced and rethrown as is: Dataverse returns its `ErrorCode` to the caller, and
  the Web API its matching HTTP status (404, 412...). Wrapping it with the error-code constructor of
  `InvalidPluginExecutionException` would not help: Dataverse still returns `0x80040265` and keeps the code only as a
  `SubErrorCode` detail (verified live). Tests that expected an `InvalidPluginExecutionException` from a failed
  service call now get the `FaultException<OrganizationServiceFault>`.
- **An unexpected exception no longer shows its type or message to the caller.** The message was
  `Unhandled exception in plugin '{FullName}': {Type}: {Message}`, which exposed the plugin's type name and a raw
  message that can hold record values, endpoints or query fragments. It is now
  `An unexpected error occurred. Correlation id: {id}.`; the full exception is still traced (persisted unless the
  Plugin Trace Log setting is `Off`) and stays the inner exception. Override `FormatUnexpectedErrorMessage` to return
  a detailed message. A failure while building the execution context gets a generic message too.
- **A Custom API parameter type with no Dataverse equivalent fails when the step is built.** `long`, `short`,
  `float`, enums, `byte[]`, `List<string>` and other types were registered as a lookalike (Integer, Float or
  String) and then read back as their default value, silently. Building the definition now throws a message naming
  the parameter and the type to use (`int`, `double`, `OptionSetValue`, `string[]`, ...).
- **`TypedRequest.Get<T>` / `TypedResponse.Get<T>` throw on a value of the wrong type** instead of returning
  `default`, and convert a base `Entity` to a declared early-bound type (Dataverse sends `Entity`, so an
  `Account` property used to read as null).
- **Secure configuration is named in code, never valued.** `WithSecureConfig("<secret>")` is replaced by
  `WithSecureConfigFrom("<Name>")`, and `StepDefinition.SecureConfig` by `SecureConfigName`. The secret used to be
  compiled into the plugin assembly (so it lived in git and in the uploaded, decompilable `pluginassembly`). Move the
  value to `vela apply --secure-config <Name>=...` or the `VELA_SECURE_CONFIG_<Name>` environment variable.
- **One organization service per identity.** `ILocalPluginContext` now exposes `Service` (the current user),
  `SystemService` and `AsUser(Guid)`. `UserService`, `CallingUserService`, `AsInitiatingUser()`,
  `AsCallingUser()`, `AsSystem()`, `CreateOrganizationService(Guid?)` and the injected
  `Func<Guid?, IOrganizationService>` factory are removed. Migrate `ctx.UserService` to `ctx.Service`,
  `ctx.AsSystem()` to `ctx.SystemService`, and `ctx.AsInitiatingUser()` to `ctx.AsUser(ctx.InitiatingUserId)`.
- **The test pipeline reuses one plugin instance per step (and per Custom API).** It constructed a fresh instance
  for every execution, which hid plugins that keep state in fields; the platform caches and reuses instances, so such
  a plugin now misbehaves in the test as it would in production.
- **A Custom API request parameter you declare is required unless you say otherwise.** `Add(x => x.Foo)` and
  `Add(x => x.Foo, optional: false)` registered an optional parameter unless the property carried `[Required]`,
  contrary to the documentation, so the handler read a default value when a caller left it out. They are now
  required (as `RequestParameter<T>(...)` already was); pass `optional: true` to keep a parameter optional.
  Parameters inferred from the model (no `Request(...)` call) keep their rule: optional unless `[Required]`. The next
  `apply` recreates each parameter whose optionality changes.
- **`Build()` rejects Custom API shapes Dataverse does not accept**, with a message naming the API and the
  parameter: a function without a response property, a workflow-enabled function, a workflow-enabled API with an
  `Entity`, `EntityCollection` or `string[]` parameter or with an `EntityReference` while not bound to a table, a
  parameter name with characters other than letters, digits and underscores, and (in the lightweight builder too
  now) the same parameter name twice.
- **The Custom API builders share one core.** The metadata methods moved to `ICustomApiMetadata<TSelf>`, which
  `ICustomApiBuilder` and `ICustomApiBuilder<TRequest, TResponse>` extend, and both builders derive from
  `CustomApiBuilderBase<TSelf>`. Calling code compiles unchanged; a class that implements these interfaces itself
  must follow, and `RequestParameter`, `ResponseParameter` and the typed `Add(property, optional, description)` gain
  an optional `logicalEntityName` parameter (recompile against the new version).
- **The unused Custom API manifest stack is removed:** `CustomApiManifest`, `CustomApiManifestWriter`,
  `CustomApiManifestComparer` / `CustomApiManifestDiff` (with `CustomApiDefinitionChange` and
  `CustomApiPropertyChange`) and `CustomApiDefinitionReader`. Nothing in Vela used them, and the comparer did not
  match what `vela plan` compares. Read the definitions with `FrameworkManifestScanner.Scan(assembly)`, and diff
  them against an environment with `vela plan`.
- **`PluginBase<TConfig>` is removed.** It derived from `PluginBase` directly, so it could not be combined with
  `EntityPluginBase<TEntity>` or `CustomApiBase`, and the package scanner never detected a plugin built on it: such a
  plugin was never registered. Read the step's unsecure / secure configuration through `UnsecureConfig` /
  `SecureConfig` on the Vela base class instead.
- **`RetryPolicy` recognises throttling by error code only.** A fault whose message merely contained "throttle",
  "temporar" or "too many requests" was replayed, including a business error that said "temporarily unavailable".
  Only the three service-protection error codes are retried now.
- **The injected `IPluginCacheService` is scoped to the executing organization.** The framework's own entries were
  keyed by organization id, but user keys were stored raw in the process-wide cache, so the same key could serve one
  organization's value to another should a worker be shared. Every key now carries the organization scope, and
  `Clear()` drops only that organization's entries instead of the whole process cache. A `PluginCacheService` you
  create yourself is unchanged.
- **Localized messages fall back to the organization's base language.** A message missing in the user's language
  (or a user with no language setting) went straight to the hard-coded `DefaultLanguageCode` (1033). The chain is now
  user language, then `organization.languagecode` (read only when needed), then `DefaultLanguageCode`.
  `IMessageService.ResolveOrganizationLanguageCode(cacheFor)` exposes the organization language (a new interface member).
- **Retry always takes the plugin context, and skips retrying inside a transaction.** The context-free
  `RetryPolicy.Execute(operation, ...)` overloads and `PluginBase.ExecuteWithRetry(operation)` /
  `ExecuteIdempotentWithRetry(operation)` are removed. Use `RetryPolicy.Execute(ctx, operation, ...)` and
  `ExecuteWithRetry(ctx, operation)` / `ExecuteIdempotentWithRetry(ctx, operation)`: when `ctx.IsInTransaction` is
  true the operation runs once, because a failed call aborts the transaction and a replay cannot succeed.
- **`PluginPipeline` no longer writes onto the entities you pass it.** `Service.Create(entity)` used to set
  `entity.Id`, and a plugin's change to `Target` showed up on your object (and survived a rollback). The steps now
  work on a copy, as on Dataverse where the request is serialized: read the id from the return value of `Create` (or
  `CreateMultipleResponse.Ids`), and the stored state from `pipeline.GetAll(...)`.

### Added

- **13 more typed triggers (#405):** `OnAddItemCampaign()`, `OnRemoveItemCampaign()` (campaign), `OnRecalculate()`
  (goal), `OnLockSalesOrderPricing()` (salesorder), `OnLockInvoicePricing()` (invoice), `OnImmediateBook()`
  (appointment), `OnReschedule()`, `OnSendTemplate()` (template), `OnSendFax()` (fax), `OnAddProductToKit()` (product),
  and the global `OnUnlockSalesOrderPricing()`, `OnUnlockInvoicePricing()`, `OnRemoveProductFromKit()`, each with its
  `I<Message>PreContext` / `I<Message>PostContext`. Verified live through a real `vela apply`: every step registers at
  PreValidation and PostOperation, and fires at PreValidation with the sdkmessage (`AddItem`, `RemoveItem`, `Send`, ...),
  the table above (`none` for the global ones) and its typed `ctx.Request`. PostOperation firing was not exercised live.
  The global messages join `PluginMessages.GlobalMessages`, so their steps register on no table. `On<SendFaxRequest>()`
  and the other request-typed forms of these messages now throw (one way per message). The other candidates of #405
  (`RenewContract`, `QualifyMemberList`, `ExecuteWorkflow`, `SetProcess`, `SendBulkMail`, `ReassignObjectsOwner`,
  `InitializeFrom`, ...) accept no custom step on Dataverse and get no trigger. The test harness gives a table-bound
  message its `Target`'s id as `PrimaryEntityId` when the `Target` is a record of that table (`Recalculate`, verified
  live).
- **`[SdkMessage("...")]` (`Vela.Messages`) (#403).** A request type outside the request-name table states the
  sdkmessage its steps register on; honoured by `On<TRequest>()`, `OnMessage<TMarker>()` and the test harness.
- **Vela.Testing: `PluginContext.ForRequest(request)` and `.WithRequest(request)`.** The fake context takes its
  message (the sdkmessage the platform runs the request on) and its input parameters from a typed request, so a
  Custom API or message test states no message name or parameter key.
- **`Include` takes column selectors (#447):** `q.Include(o => o.Order_Details, l => l.LineTotal)` reads only those
  columns of the related records.
- **Trace volume controls (#449).** The plug-in trace log keeps about 10 KB per execution, and the automatic traces
  could crowd a plugin's own lines out of it. `protected override bool AutoTraceServiceCalls => false;` stops the
  wrapped services (`Service`, `SystemService`, `AsUser`) from writing two lines per Dataverse call; a failed call is
  still traced, on one line with its scope, depth, table or request, and exception type (an indented FetchXML query
  is collapsed onto that line, in every `RetrieveMultiple` trace).
  `TraceSnapshotDetail.None` (`AutoSnapshotDetail`, or an explicit `TraceExecutionSnapshot`) writes no snapshot.
  Both are opt-in, per plugin class (a shared abstract base class applies them to a whole project): the defaults are
  unchanged.
- **`Async(deleteJobOnSuccess: true)`** deletes an asynchronous step's system job once it succeeds (Dataverse
  `asyncautodelete`), carried on `StepDefinition.AsyncAutoDelete`.
- **`ForRelationship("schema_name", ...)` on `OnAssociate()` / `OnDisassociate()`.** Dataverse fires these steps for
  every relationship; the handler now runs only for the named ones (`StepDefinition.Relationships`), and any other
  relationship is skipped with a trace line.
- **`SkipWhenDepthAbove(n)`** skips a step's handler (with a trace line) when the execution depth is above `n`,
  replacing the plugin-level depth guard, which throws, for that step only. `n` must be at least 1. Runtime-only.
- **Typed bulk images.** A typed step context of a `CreateMultiple` / `UpdateMultiple` handler exposes `PreImages` /
  `PostImages`: one projection per target, aligned with `ctx.GetTargets()`, read by the step's own alias.

- **`RetryPolicy.Execute(..., maxElapsed:)` bounds the whole call (#450).** Each wait was capped at
  `RetryPolicy.MaxDelay` (30 s) but their sum was not, so a few attempts could consume most of the ~2-minute plugin
  budget. With `maxElapsed`, a retry whose wait would end past the budget is not made and the last fault propagates.
  The default (null) keeps the previous behaviour.
- **`OnAssign()` steps can capture images (#439).** `WithPreImage(...)` is offered at every stage and
  `WithPostImage(...)` at PostOperation, as Dataverse accepts them on the Assign `Target` (verified live: the
  pre-image holds the old owner, the post-image the new one). They are read with the typed `ctx.PreImage` /
  `ctx.PostImage` on `IAssignPreContext<T>` / `IAssignPostContext<T>`, and a named handler gets generated
  projections of the captured columns, like Update and Merge (#475). A capture on `OnAssign()` no longer reports
  VELA021.
- **VELA024 warns on parallel execution in a plugin (#448).** `Task.Run`, `Task.Factory.StartNew`, `Parallel.*`,
  `AsParallel()`, `ThreadPool.QueueUserWorkItem` and `new Thread(...)` inside a plugin / Custom API type, and
  blocking on a task there (`.Result`, `.Wait()`, `Task.WaitAll` / `WaitAny`, `GetAwaiter().GetResult()`), following
  Microsoft's guidance not to use parallel execution in plug-ins. Code outside a plugin type is not flagged.

- **VELA022 warns on a step registered on `SetState` (#439).** Dataverse accepts the registration but never runs
  it: a `SetStateRequest` and an Update of `statecode` fire `SetStateDynamicEntity` (verified live). Register on
  `OnUpdate().FilterOn(x => x.StateCode)` or `OnMessage(PluginMessages.SetStateDynamicEntity)` instead.

- **`WithDefaultStatus(table, state, status)` on `InMemoryOrganizationService` and `PluginPipeline` (#439).** It
  sets the status a record gets when a write changes `statecode` without `statuscode`, or a SetState passes status
  -1; the store now applies that default (state 0 gets status 1, any other state status 2, when undeclared).
  `InMemoryOrganizationService.Execute` also runs a `SetStateRequest`.

- **Merge steps capture images of the master and of the subordinate record (#462).** `OnMerge()` now offers
  `WithPreImage` / `WithPostImage` (the master, `Target`) and `WithSubordinatePreImage` /
  `WithSubordinatePostImage` (the record merged away, registered against `SubordinateId`), with typed
  `ctx.PreImage` / `ctx.PostImage` / `ctx.SubordinatePreImage` / `ctx.SubordinatePostImage` and generated
  projections. `ImageDefinition.MessagePropertyName` records the request property. The harness delivers both
  images on a `MergeRequest`, and its Merge now sets the subordinate's `masterid`. Verified live: both kinds on one
  step at every stage, and the subordinate's post-image is the deactivated record.
- **PreValidation steps can capture a pre-image (#436).** `WithPreImage(...)` is now offered at PreValidation on
  `OnUpdate()`, `OnDelete()`, `OnUpdateMultiple()`, `OnMessage(...)` and `On<TRequest>()`, as Microsoft Learn
  recommends over an extra Retrieve. A post-image is still not offered before PostOperation, and Create never has
  a pre-image (the runtime backstop and VELA007 still reject it). A top-level PreValidation step runs outside the
  transaction, so its image can be stale by the main operation: use it for validation and keep a strictly
  consistent read at PreOperation. Verified live: the image arrives at PreValidation for Update and Delete, and once
  per `Targets` entry for UpdateMultiple.
- **`PluginBusinessException(message, errorCode)` and `PluginBusinessException(message, innerException)`.** The error
  code is passed to `InvalidPluginExecutionException(OperationStatus.Failed, code, message)`, which an SDK caller
  reads as the fault's `SubErrorCode` detail (the top-level code stays `0x80040265`). The business exception is now
  traced with its stack trace and inner exception, not just its message.
- **VELA021 warns when a typed image projection cannot be generated.** A `WithPreImage(...)` / `WithPostImage(...)`
  that lists columns but cannot be projected (a non-constant alias, a selector that is not a column property, or a
  `RegisterStep()` chain whose message or stage is not constant) used to generate nothing silently, leaving only a
  `CS0246` on the missing `{Handler}_{Pre|Post}Image` type. The generator now says why, on the capture. Selectors that
  VELA001/VELA010 already report are not reported twice.

- **VELA020: a Create/Update step without `FilterOn(...)` is a build error.** These steps already had to declare a
  filtering attribute, but only building the step at runtime enforced it, so a chain without one compiled and then
  failed in `vela plan` or at execution. The rule makes the documented compile-time guarantee real.
- **A Custom API parameter can name its table** (`logicalentityname`): the `logicalEntityName` argument of `Add`,
  `RequestParameter` and `ResponseParameter`, or `[LogicalEntityName("contact")]` (`Vela.Messages`) on a model
  property. An early-bound entity parameter (`Account`) uses its own table. Before, the framework had no way to set
  it, so `apply` removed a table set in Dataverse by recreating the parameter.
- **`PluginBase.DefaultMaxDepth` (8) is the one depth guard default.** The constructors of `PluginBase`,
  `EntityPluginBase<TEntity>`, `CustomApiBase` and `CustomApiBase<TRequest, TResponse>` default their `maxDepth` to it
  instead of repeating the literal.
- **`PluginPipeline.WithStrictSecurity()`, `WithAccessTeamTemplate(...)` and `WithSharedRecord(...)`.** The
  pipeline forwards the in-memory store's configuration by hand, and these three were missing, so a pipeline
  scenario could not opt into strict security, register an access-team template or pre-share a record. A test now
  fails if any store configuration method has no pipeline counterpart.

### Changed

- **The telemetry guide and `LogMetric` docs describe what Vela does and defer to Microsoft for the rest (#465).**
  `LogMetric` writes the `[Metric]` trace-log line and hands the metric to the platform logger; what reaches
  Application Insights is the platform's, as [Microsoft documents it](https://learn.microsoft.com/power-apps/developer/data-platform/application-insights-ilogger).
  The `Method` dimension is documented as present only when Vela dispatches to a handler method. Checked against a
  live environment exporting to Application Insights: entries arrive at their level, formatted, with Vela's default
  and scope dimensions.
- **The depth guard and `RetryPolicy` are documented as Microsoft describes them (#450).** The `maxDepth` guard is a
  last-resort backstop (at the default it only stops a chain Dataverse aborts anyway; Microsoft advises against logic
  that depends on `Depth`), and the plugins guide has a new [Preventing recursion](docs/guides/plugins.md#preventing-recursion)
  section on `FilterOn`, `HasChanged` and shared-variable flags. `RetryPolicy` notes that service-protection limits
  don't apply to data operations from a plugin, so it is meant for calls that leave the sandbox and for code shared
  with a client.
- **Automatic execution snapshots no longer trace record data.** The `start` / `success` / failure snapshots wrote
  every Target, image and parameter value on every execution, and also sent them to `ILogger` / Application
  Insights. They now list only metadata (message, stage, ids, parameter/attribute names), the redundant `success`
  snapshot is dropped, and snapshots go to the plug-in trace log only. Opt back into values per plugin with
  `protected override TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.Full;`. An explicit
  `ctx.TraceExecutionSnapshot(phase)` still traces values (`TraceSnapshotDetail.Metadata` for names only).
- **Dataverse call tracing is one line per event** (`Dataverse.Update start scope=User depth=1 entity=account; id=...`
  then `Dataverse.Update ok 42 ms`) instead of an 11-line block per call, which filled the ~10 KB trace log.
- **Less work per execution.** `ctx.Service`, `ctx.SystemService` and `ctx.NotificationService` are created on first
  use instead of for every execution, and the reflection the context does on every run (context interface types,
  context properties read by name, the telemetry logger and `LogMetric` lookups) is resolved once per process. A typed
  Custom API finds its request type's auto-properties once per type instead of reflecting over every input parameter
  on every execution.
- **The newer context members are read through their SDK interfaces (#448).** `EnvironmentId`, `TenantId`,
  `InitiatingUserAgent`, `IsPortalsClientCall`, `PortalsContactId`, `IsApplicationUser` and the bulk image
  collections were read by reflection on a public property of the concrete context class, so a context that
  implements `IPluginExecutionContext2..7` explicitly returned nothing. They are now read through the interfaces,
  which also lets the compiler check each member's type. Whether the platform's own context class implements them
  explicitly is not verified live; this removes the dependency on it, and the last per-process reflection cache.
- **The generated step API is compiled with nullable annotations enabled (#448).** `StepApi.cs`,
  `StepBuilder.Impl.cs` and `TypedTriggers.cs` carried `?` annotations in a nullable-disabled context (CS8669), so
  the annotations were ignored; the generator now emits `#nullable enable`, matching the hand-written API.
- **VELA011 catches more mutable plugin state (#448).** Besides a writable field, it now warns on a writable
  auto-property, a `readonly` field holding a mutable collection (`List<T>`, `Dictionary<,>`, `HashSet<T>`, the
  `IList<T>` / `IDictionary<,>` / `ICollection<T>` / `ISet<T>` interfaces, the non-generic collections) and a
  primary-constructor parameter captured by a member. Arrays, read-only views, concurrent and immutable collections
  are left alone.
- **VELA013 is scoped to plugin code and reads constants (#448).** It warns only inside a plugin / Custom API type,
  like VELA019, so test projects and tools are no longer flagged, and `new ColumnSet(AllColumns)` over a
  `const bool` no longer slips through.

### Fixed

- **Harness writes that fail leave nothing behind (#451).** An `Update` whose lookup key matches nothing used to
  merge its other columns first, and a multi-record `Associate` linked the records before the failing one. Both now
  leave the store as it was.
- **Harness `Merge` follows the relationships (#451, verified live).** It moves the subordinate's N:N rows to the
  master, and it no longer reparents children over a relationship whose Merge behaviour is `NoCascade`.
  `WithReferentialRelationship` now declares Merge as `Cascade`, like the platform's referential relationships, so
  its children are still reparented.
- **Harness `DeleteFile` clears only the File column the file was uploaded to (#451),** and needs Write on that
  record. It cleared any column holding the same Guid, in every table, with no access check.
- **`RecordingLogger` scopes can be disposed in any order (#451).** Disposing a scope removed the innermost one.
- **Early-bound entities are downcast deeply, and the caller's request is left untouched (#447).** `ctx.Service`
  now also downcasts early-bound entities nested in `RelatedEntities` (deep insert) and in `EntityCollection`
  attributes (activity parties), which failed with the same `SerializationException`. `Execute` sends a copy of the
  request instead of replacing the parameters of the caller's request object.
- **`TypedQuery.Select` / `SelectAll` keep the group-by and aggregate columns added before them (#447).**
- **`Clone()` drops only the real primary key (#447).** Another `*id` Guid column holding the same value as `Id`
  is kept; an early-bound model's key column is read from its `Id` property.
- **FetchXML value redaction covers CDATA values (#447).**
- **The manifest scanner reports the real cause of a discovery failure (#447),** not the reflection wrapper's message.
- **`ctx.Response.id` resolves in a post-create test with an explicit trigger.** `PluginContextBuilder` filled
  `OutputParameters["id"]` (and `Ids` for `CreateMultiple`) from the Target only when it derived the trigger from a
  single-step plugin. A multi-step router, whose test states `OnCreate().Stage(PluginStages.PostOperation)`, saw
  `Guid.Empty`, although Dataverse always sets it. The builder now fills them for any Create/CreateMultiple PostOperation
  context, and keeps a value the test sets with `WithOutputParameter`.

- **The harness runs a table-bound request on its table, with an empty `PrimaryEntityId` (#461, verified live).**
  The queue messages (`AddToQueue`, `RemoveFromQueue`, `PickFromQueue`, `RouteTo`, `ReleaseToQueue`) run on
  `queueitem` whatever their `Target`, the email requests on email, the team and list requests on team and list,
  `CloseIncident` on incident and `WinQuote` on quote, so a step on the wrong table does not fire in a test either.
  The platform leaves `PrimaryEntityId` empty on all of these, at PreValidation and PostOperation, and so does the
  harness now (it used to take the id of the closing activity's lookup or of the `Target`).
- **The in-memory pipeline fills a bulk step's image collections as Dataverse does** (verified live): each target's
  collection also holds the images of the plugin type's other steps on the message and the platform's
  `PreBusinessEntity` / `PostBusinessEntity`, so a test of access by alias is real. It also seeds the
  `sdkmessageprocessingstep` rows an async step's `OwningExtension` points at, so several async steps on one trigger
  dispatch as on Dataverse.

- **`RetryPolicy` jitter is independent across threads (#450).** Each thread's `Random` was seeded from the tick
  count on .NET Framework, so threads that started retrying in the same tick drew the same backoff. The seeds now come
  from one shared generator.
- **The harness refuses to download an Image column that does not store full images (#458, verified live).**
  Dataverse keeps only a thumbnail for such a column (`account.entityimage` is one), so `InitializeFileBlocksDownload`
  fails with `0x80040217` ("No FileAttachment records found ..."); the harness served the stored bytes, so a plugin
  calling `DownloadFile` on it passed its tests and failed in production. When the table's metadata declares the
  column as an Image column with `CanStoreFullImage` not `true`, the download now throws that fault. Without metadata
  for the column the stored bytes are still served. New builder method: `FakeEntityMetadata.Image(name,
  canStoreFullImage)`.
- **`DownloadFile` no longer returns a truncated file (#440).** An empty block before the announced
  `FileSizeInBytes` used to end the loop and return the bytes read so far, with no error. It now throws an
  `InvalidPluginExecutionException` naming the table, record, column, the bytes received and the announced size.
  The same check rejects a download whose total differs from the announced size.
- **FetchXML condition values compare as the column's type (#439).** A `<value>` was kept as text and compared with
  the stored value's string form, so `eq` on a date (`2026-10-09`), a money with another scale (`10.50`) or a braced
  guid matched nothing, and `ne` matched everything. Values (FetchXML, and text values in a `QueryExpression` or
  `QueryByAttribute`) are now read as the column's type in the invariant culture, and a value that is not valid for
  it throws. Ordered operators (`gt`, `lt`, `between`, ...) no longer depend on the machine's culture, and no longer
  throw `InvalidCastException` on a lookup.
- **A store-side rejection of a modelled message fails the operation (#439).** With a step registered on the
  message, a failure such as `Merge` on a table that cannot merge was caught as "message not modelled": the call
  returned an empty response and the PostOperation steps still fired. Only a message the store does not simulate is
  now treated that way (it throws the new `MessageNotModelledException`, a `NotSupportedException`); every other
  failure reaches the caller.
- **`WithStrictSecurity()` protects Delete (#439).** Without `WithSecurity(...)`, Create, Update and RetrieveMultiple
  failed as documented, but Delete went through.
- **Typed triggers register on the real sdkmessage (#461).** 15 triggers registered steps on SDK request names that
  do not exist as sdkmessages, so `vela apply` could not deploy them: `OnSendEmail()` now registers on `Send`,
  `OnSendEmailFromTemplate()` on `SendFromTemplate`, `OnDeliverIncomingEmail()` / `OnDeliverPromoteEmail()` on
  `DeliverIncoming` / `DeliverPromote`, `OnWinOpportunity()` / `OnWinQuote()` on `Win`, `OnLoseOpportunity()` on
  `Lose`, `OnCloseIncident()` on `Close`, `OnCancelContract()` / `OnCloneContract()` on `Cancel` / `Clone`,
  `OnAddMembersTeam()` / `OnRemoveMembersTeam()` on `AddMembers` / `RemoveMembers`, and `OnAddListMembersList()` /
  `OnAddMemberList()` / `OnRemoveMemberList()` on `AddListMembers` / `AddMember` / `RemoveMember`. The handler keeps
  its typed context (one sdkmessage can serve two requests: `Win` on opportunity or quote), and `ctx.MessageName` is
  the sdkmessage, as on the platform. `OnMessage(PluginMessages.SendEmail)` is translated the same way. Verified
  live through `vela apply`: each step registers, fires on its SDK request and reaches its typed handler.
- **The harness runs a request's steps under the sdkmessage.** An executed `SendEmailRequest` runs the `Send` steps
  and plugins see `MessageName == "Send"`, so a test now catches a step registered under a wrong name.
  `PluginContext.For<T>().OnMessage(...)` translates request names the same way. `CloseIncidentRequest` and
  `WinQuoteRequest` resolve their primary entity from the closing activity's lookup (incident, quote), so an
  entity-bound step fires.
- **The harness runs async steps only once the outermost operation commits** (#438). Each operation used to run its
  own async steps as soon as its body returned, so an async step queued by a nested write ran, with its side effects
  and log entries, even when the outer operation then failed. Live, a rolled-back operation creates no async job
  (verified 2026-10-07). Async steps now wait for the outermost operation (or `ExecuteTransaction` batch) to commit, are
  dropped when it rolls back, and each runs in its own rollback scope: a failed async step leaves no writes behind.
- **`IsInTransaction` follows the stage and mode in the harness** (#438). It was always true. It is now false for
  async steps and for a depth-1 PreValidation step, and true for PreOperation, sync PostOperation and a nested
  PreValidation (verified live), so the retry path of `RetryPolicy` can be tested through the pipeline. Retrieve and
  RetrieveMultiple steps report false, as on Dataverse. `CorrelationId` is now one id per top-level operation, shared
  by its cascade and its async steps, instead of one id for the whole pipeline.
- **The harness fills the remaining context fields as Dataverse does** (#438, verified live 2026-10-09). `RequestId`
  is one id per call into the pipeline (an `ExecuteMultiple` batch is one request) and is kept by async steps.
  `OperationId` equals `RequestId` on sync steps and is a separate system job id on async steps. `IsolationMode` is 2
  (sandbox) and `SecondaryEntityName` is "none". The children of an `ExecuteTransaction` share one correlation id and
  run in its transaction, PreValidation included; the children of an `ExecuteMultiple` each get their own.
- **Harness Associate/Disassociate steps see `PrimaryEntityName` "none".** The harness reported the Target's entity
  and id, so a handler branching on `ctx.PrimaryEntityName == "account"` passed in tests and never matched live. On
  Dataverse both are global messages: every step sees `PrimaryEntityName` "none" and an empty `PrimaryEntityId`, at
  every stage and mode (verified live), and only the `Target` input names the record. The harness now matches (#459).
- **Harness Custom APIs see the platform's primary entity.** The harness gave every Custom API an empty
  `PrimaryEntityName` and no `PrimaryEntityId`. On Dataverse (verified live) a global API sees "none" and an empty id,
  an entity-bound API sees the bound table and the `Target`'s id, and a collection-bound API sees the bound table and
  an empty id. The harness now matches.

- **VELA007 flags every image the message can never provide.** It now also reports a pre-image on
  `OnMessage("CreateMultiple")` and any image on `OnMessage("RouteTo")`, from the same image table the runtime and
  the CLI use (#435).

- **The harness runs a Delete's PreValidation steps before the cascade.** They used to fire after the cascaded
  children were already deleted, so a PreValidation step could not see them. On Dataverse they still exist at
  PreValidation and are gone by PreOperation (verified live), which the harness now matches.

- **VELA009, VELA017 and VELA018 analyze handlers bound through `Handle(Delegate)`.** A handler taking a generated
  typed step context (or any context other than the config's `Handle(Action<...>)` one) binds to that overload, and
  the analyzers missed it entirely: an uncaptured image read, a `HasChanged` without a pre-image or a Target write in
  an async step went unreported. VELA009 also covers the static-call form `PluginContextExtensions.HasChanged<T>(ctx, ...)`.
- **A column selector that is not a column of the entity fails instead of registering a wrong column.** A nested
  path (`a => a.PrimaryContactId.Id`, `a => a.ParentAccount.Name`) resolved to the last member's name ("id",
  "name"), and `a => a.Id` on a model without a named `Id` to "id", so `FilterOn` and images silently targeted a
  wrong or nonexistent column. VELA001 now reports both, the runtime throws for both, and the image generator no
  longer projects a nested path. One resolver now backs FilterOn, images and typed queries.
- **`string[]` Custom API parameters register as StringArray.** The builders emitted `System.String[]`, which the
  CLI registered as a plain `String` (with no drift shown). Early-bound entity parameters now register as `Entity`
  instead of `String`. One shared mapping serves every builder.
- **`HasChanged` detects casing edits and multi-select changes.** It compared strings case-insensitively, so
  "acme" to "ACME" was not a change, and compared multi-select option sets through `ToString()` (the type name), so
  any two selections were equal. Change detection now compares strings exactly and multi-selects by their set of
  values (order-free; an empty selection equals null). Harness queries keep Dataverse's case-insensitive matching,
  and a multi-select equality filter now compares the selected values instead of matching every row.
- **The test harness filters Create steps.** A Create step fired whatever columns the record was created with; it now
  fires only when the record sets one of its filtering attributes, like Update (and like Dataverse).
- **Step impersonation and CallerId are honoured.** The default service was built on `InitiatingUserId`, so a
  step registered with `WithImpersonation(...)` (or a CallerId call) still ran with the triggering user's
  privileges. `ctx.Service` and the injected `IOrganizationService` now act as `UserId`, like the platform.
- **`AddPluginRuntime` registers the same services as the built-in resolver.** Both now share one default set, so
  Microsoft DI also gets `IPluginCacheService`, `IEnvironmentVariableReader` and `IMessageService`, and its
  `IOrganizationService` is the current user's service.
- **A generated step context reads an aliased image.** For `WithPostImage("snap", ...)` (or a pre-image alias) the
  generated `ctx.PostImage` / `ctx.PreImage` read the image under the default alias, so it was always null at runtime.
  The context now passes the step's alias.
- **Harness FetchXML joins and conditions return the same rows as the QueryExpression.** The FetchXML parser mapped
  every link-type except `outer` to an inner join (so `not any` returned the opposite rows), never read `entityname`
  or `valueof` on a condition, and dropped link-entities inside a `<filter>`, link-entities under aggregates, link
  orders, attribute aliases, `dategrouping`, `distinct` countcolumns and `returntotalrecordcount`; QueryExpression
  aggregates ignored their link-entities and `OrderByAggregate`. FetchXML now parses into the equivalent
  QueryExpression (aggregates included) and runs through the same engine path: all nine link-types, conditions on
  any linked column (selected or not), column comparisons, joins before aggregating, and date grouping by
  day/month/quarter/year. Anything not modelled (an unknown element or attribute, `natural`, `datasource`,
  `rowaggregate`, week/fiscal date grouping) throws `NotSupportedException` naming it instead of being ignored.
  `matchfirstrowusingcrossapply` now drops unmatched parents (an inner variant), a joined row no longer carries an
  unselected join-key column, and `ToFetchXml()` emits link orders and `order entityname`, and writes a column alias
  as a plain fetch rather than an aggregate.
- **The test pipeline honours a step's registration: enabled flag, configuration and impersonation.**
  `PluginPipeline` fired a `.Disabled()` step, always constructed the plugin with `(null, null)`, ignored
  `WithImpersonation(Guid)`, and wired `ctx.Service` and `ctx.SystemService` to one service subject to the same
  security checks. A disabled step no longer fires; the plugin receives the step's unsecure configuration and the
  value of its secure configuration (supply it with the new `PluginPipeline.WithSecureConfig(name, value)`); an
  impersonated step sees the impersonated `UserId` and its `ctx.Service` acts as that user (privileges, audit
  stamping), while `InitiatingUserId` stays the caller; under `WithSecurity`, `ctx.SystemService` is SYSTEM and is not
  subject to the security model, while `ctx.Service` is.
- **Associate/Disassociate fire regardless of the entity in the test pipeline.** The harness matched these steps on
  the entity, so a step declared on `account` did not fire for an association made from `contact`, unlike the runtime
  and the CLI, which treat both messages as global. The list now lives in one place
  (`PluginMessages.GlobalMessages` / `PluginMessages.IsGlobalMessage`), shared by the runtime, the harness and the CLI.
- **Upsert and alternate keys run through the test pipeline.** An `UpsertRequest` skipped the pipeline (no step
  fired), and an `UpdateRequest`/`DeleteRequest` addressed by `KeyAttributes` fired its steps with `Guid.Empty` (or
  failed). Upsert now resolves the id or alternate key and runs the Create steps (new record) or the Update steps
  (existing record), returning `RecordCreated` and `Target`; Update and Delete by alternate key fire their steps with
  the resolved id.
- **The test pipeline models the rest of Dataverse's transaction scope** (verified live). The initial
  PreValidation stage runs before the transaction, so a write a PreValidation step makes commits on its own even
  if the operation fails later; nested operations join the caller's transaction. A plugin that catches a failed
  **write** can no longer let the operation commit: returning fails it with "ISV code reduced the open transaction
  count" (`0x8009000C`) and a further call gets "There is no active transaction"; a caught failed **read** does not
  end the transaction. A plugin's own operations carry `InputParameters["FromSandbox"] = true` at PreValidation.
  An async step's context has an unnamed `OwningExtension` and the `AsynchronousStepPrimaryName` post image.
  Shared variables set at PreValidation of a Create/Update/Delete reach the later stages only through
  `ParentContext.SharedVariables`, their ParentContext being the operation's stage-30 context.
- **A Retrieve/RetrieveMultiple Post step's typed `ctx.Response` reads the result.** The platform hands the step
  the result as `OutputParameters["BusinessEntity"]` / `["BusinessEntityCollection"]` (verified on a live
  environment), but `RetrieveResponse.Entity` / `RetrieveMultipleResponse.EntityCollection` read `Entity` /
  `EntityCollection`, so `ctx.Response.Entity` was missing. `GetResponse<T>()` now exposes the platform value under
  the SDK key.
- **Reads run through the test pipeline.** `PluginPipeline.Service.Retrieve`/`RetrieveMultiple` read the store
  directly, so `OnRetrieve()`/`OnRetrieveMultiple()` steps never fired, and a PreOperation step that replaced
  `InputParameters["Query"]` (or any input of a modelled message) was ignored. Reads now fire their steps, a Pre
  step's change to `ColumnSet`/`Target`/`Query` changes what is read, and a Post step sees and can change the result
  under `OutputParameters["BusinessEntity"]` / `["BusinessEntityCollection"]`. For every message, a synchronous
  step's changes to the inputs reach the main operation and its changes to the outputs reach the caller.
- **Harness reads return the platform's record shape.** A retrieved or queried record now always includes its
  primary key column (`accountid`, `activityid` for activities, or the metadata's primary id), a lookup carries the
  referenced record's name (`EntityReference.Name` and `FormattedValues`), and a choice carries its label in
  `FormattedValues` when its metadata is registered.
- **Custom APIs run the whole pipeline in the harness.** Only the API's own plugin ran: steps registered on its
  message never fired, and inputs of the wrong type reached the handler. Now PreValidation, PreOperation and
  PostOperation steps fire around the main operation as far as the API's `AllowedCustomProcessingSteps(...)` allows
  (a step it forbids fails the call), and the main operation runs at stage 30 under the
  `CustomApi '<name>' implementation` step. Before any step runs, a missing required input fails with `0x80040203`
  and a wrong-typed or undeclared input with `0x80040315`, with the platform's messages (verified live).
- **The test pipeline's steps work on a deep copy of every request.** The values of a request (entities,
  references and their keys, column sets, queries) were shared with the caller, so a step editing one in place
  changed the caller's objects; an Upsert step's change to `Target` never reached the main operation; and a step
  could not remove an input or output. All of these now behave as on the platform.
- **`ctx.SystemService` over an `InMemoryOrganizationService` is SYSTEM in a context-builder test.**
  `PluginContext...WithOrganizationService(store)` used the store as its calling user for both services, so under
  `WithSecurity` `ctx.SystemService` was denied what SYSTEM may do. It is now the store's SYSTEM view.
- **A nested request's own bypass parameters are honoured by the test pipeline.** Bypass parameters were read only
  from the outermost request, so a plugin's own write with `BypassCustomPluginExecution` (the usual way to avoid
  re-entering itself) re-fired the plugin, and `ExecuteMultiple`/`ExecuteTransaction` children's own parameters were
  ignored. The effective bypass is now the inherited bypass plus the request's own, at any depth.
- **VELA002 flags an expression-bodied registration that never calls `Handle(...)`.** `RegisterPluginSteps() =>
  RegisterStep()...;` (the form most samples use) was missed; void expression bodies are now checked like statements.
- **VELA015 flags a handler of the wrong shape and reads parenthesized chains.** A method group with more or fewer
  than one parameter, or a non-void return, compiled through `Handle(Delegate)` and only failed at registration; it is
  now an error on every trigger path. The message/stage is read through parentheses, and the code fix writes the
  simplified type (`IUpdatePostContext<Account>`) instead of a fully qualified `global::` name.
- **VELA009 flags `HasChanged` on a column the pre-image does not capture.** When the step's `WithPreImage(...)` lists
  its columns, a compared column missing from the list reads its previous value as `null` and always looks changed.
- **VELA016 no longer flags a container class.** Two nested plugins that both name a handler `OnUpdate` were reported
  on the enclosing class; only a class's own steps are compared now.
- **The VELA003 fix keeps the columns of a duplicate `FilterOn(...)`.** It merged nothing and dropped the second call;
  it now moves its columns into the first `FilterOn(...)`.
- **VELA014 asks for `partial` only when something is generated, and covers every enclosing type.** It now shares the
  generator's own decision (so a capture the generator cannot project no longer forces `partial`), and a `struct` or
  `record` enclosing a plugin is reported too (the code fix handles any type kind).
- **The image projection generator handles generic plugins and struct/record containers.** It reopened every
  containing type as `partial class Name`, which broke a generic plugin or a plugin nested in a struct or record; it
  now repeats each type's kind and type parameters. A column property without `[AttributeLogicalName]` is projected
  under its lower-cased name, as the runtime resolves it, instead of suppressing the projection.
- **Vela types are recognised by symbol, not by simple name.** `PluginBase`, `EntityPluginBase<T>`, `CustomApiBase`,
  `IPlugin`, `ILocalPluginContext` and `IFluentStep` are resolved from the compilation, so an unrelated type that
  shares a name is no longer treated as a plugin (VELA011, VELA012, VELA019) or a step builder (generator); VELA011
  and VELA019 skip compilations that reference neither the SDK nor Vela. The fluent-chain walk the analyzers and the
  generator each copied is now one shared, tested walker, so every rule sees parentheses and `!` the same way.
- **A failure while building the execution context or registering services follows the handler failure contract.**
  `ConfigureServices`, an `[assembly: VelaServiceRegistrar]` and the `LocalPluginContext` constructor ran before
  the `try` in `PluginBase.Execute`, so their exception escaped unwrapped and untraced, and the resolver was never
  disposed. Registration now runs inside the `try` (traced, wrapped in an `InvalidPluginExecutionException`, resolver
  disposed); a context construction failure is traced to the platform tracing service and wrapped too. Tracing in the
  failure handlers is best-effort: a tracing service that throws no longer replaces the original failure (or a
  `PluginBusinessException`'s message).
- **`RetryPolicy` honours `Retry-After`.** A service-protection fault's `ErrorDetails["Retry-After"]` was ignored, so the
  jittered backoff retried too early and was rejected again. The policy now waits that long, and rethrows at once when
  the wait exceeds `RetryPolicy.MaxDelay` (30 s).
- **Typed query values reach Dataverse in the shape it compares.** `ToFetchXml()` wrote a `DateTime` without
  milliseconds or zone (a UTC instant was read in the user's time zone) and an enum as its member name
  (`value="Active"`). A UTC `DateTime` is now written as `yyyy-MM-ddTHH:mm:ss.fffZ` (a local one converted to UTC,
  an `Unspecified` one zone-less), a `DateTimeOffset` keeps its offset, and an enum is written as its integer. The
  typed condition helpers (`Where`, `WhereIn`, `WhereLink`, `Condition`, the `QueryExpression` extensions) also turn
  an enum value into its integer, which a `QueryExpression` needs.
- **A second `WhereRelated` no longer replaces the first.** Every call wrote the single
  `Criteria.AnyAllFilterLinkEntity` slot, so only the last related-record filter applied. Each call now adds its own
  child filter, and the filters combine with AND.
- **The labels table source ignores deactivated rows and keys its cache by column mapping.** `DataverseTableMessageSource`
  served templates from inactive rows, and its cross-execution snapshot was keyed by table only, so two sources over the
  same table with different columns shared one snapshot. Rows whose `statecode` is not 0 are skipped, and the cache key
  includes the key, language and value column names.
- **`RetrieveAll` / `ToList` no longer leave the query on its last page.** Paging overwrote the query's `PageInfo`,
  so a reused `TypedQuery` kept the last page number and cookie. The caller's `PageInfo` is restored afterwards.
- **File helpers cap the block size at 4 MB.** `UploadFile` / `DownloadFile` passed a larger `blockSize` straight to
  Dataverse, which rejects blocks above 4 MB; it is now capped (`FileColumnExtensions.MaxBlockSize`). When
  `InitializeFileBlocksDownload` reports `IsChunkingSupported = false`, `DownloadFile` reads the whole file in one
  block, as Dataverse requires. `DownloadFile` also assembles the content in one buffer instead of a `List<byte>`.
- **`CustomApiResponse` uses its API unique name.** The constructor ignored its `apiUniqueName` argument, so a typed
  Custom API response had no `ResponseName`. It now sets `ResponseName`, as `CustomApiRequest` sets `RequestName`.

## [0.24.0] - 2026-10-12 (Vela.Cli)

### Changed (breaking)

- **The generated `On<Message>()` step extensions move to `Messages/<Name>.Steps.cs`, compiled by the plugin
  projects.** They extend the `Vela.Plugins` step builder, but they were written into the contract file in the Shared
  project, which references only `Vela.Messages`, so any `vela models --messages` / `--custom-apis` output with an
  extension failed to build in a scaffolded solution (`CS0234: 'Steps' does not exist in the namespace 'Vela'`, found
  on a live run). `vela init` now excludes `*.Steps.cs` from Shared and links it into the plugin projects, and the
  extension classes are `internal`. An existing solution needs the two `.csproj` lines shown in the models guide.
- **`vela models --messages` generates one class per request, not per sdkmessage (#403).** A shared sdkmessage has one
  request per entity, each with its own fields (verified live: `Close` serves `CloseIncident` and `CloseQuote`,
  `Cancel` serves `CancelContract` and `CancelSalesOrder`, `Send` serves `SendEmail`, `SendFax` and `SendTemplate`).
  The generator read only the first request, so `--messages Close` produced one `CloseRequest` typed with whichever
  entity's fields came first. `--messages` now takes the real request name and generates exactly it
  (`--messages CloseQuote` gives `CloseQuoteRequest` with `QuoteClose` and an `OnCloseQuote()` on `Quote`); an
  sdkmessage name that is not a request (`Close`, `Send`) is rejected with the request names it serves. The bare
  `--messages` generates every request of every registerable message. A request the framework's table does not route
  is marked `[SdkMessage("...")]`. A request the SDK already ships (found by reflection over `Microsoft.Xrm.Sdk` and
  `Microsoft.Crm.Sdk.Proxy`, replacing the hand-kept list) gets no duplicate class, only its entity-constrained
  `On<Message>()` over the SDK type (`OnCloseQuote()` → `On<Microsoft.Crm.Sdk.Messages.CloseQuoteRequest>()`). A
  message with a typed trigger on the step builder (`OnQualifyLead()`, `OnMerge()`, ...) gets nothing: the builder's
  own method registers it (one way per message), and the SDK ships its request. The "already has an SDK request class" check now uses the request name, so the bare `--messages` no longer generates duplicates such as
  `WinRequest` or `SendRequest` next to the SDK's `WinOpportunityRequest` and `SendEmailRequest`.
- **`vela add` registers an unmodelled message through its request type.** `--message <Name>` outside the curated set
  renders `On<NameRequest>()` with an `IMessagePost/PreContext<NameRequest>` handler instead of `OnMessage("Name")`:
  the SDK class, fully qualified, when one ships (`On<Microsoft.Crm.Sdk.Messages.SetStateRequest>()`), else the
  request `vela models --messages` generates (a note says so). `Assign` now renders the curated `OnAssign()`.
  The scaffolded Custom API tests build their context with `PluginContext.ForRequest(new XxxRequest { ... })`.
- **Steps deployed by very old Vela versions (named `df:<plugin type>:<step key>`) are no longer recognised by
  their step key.** The live read no longer parses that name format. Such a step is matched like a keyless step
  (plugin type, message, stage, mode and table) and renamed on the next `apply`; when it cannot be matched that way,
  it is deleted and recreated.
- **`plan`, `apply` and `inspect` each accept only their own options.** They shared one allowlist, so
  `plan --auto-approve` or `inspect --strict` was accepted and ignored; it is now `Unknown option` (exit 2).
  `--detailed-exitcode` is `plan` only; `--auto-approve`/`--yes` and `--batch-size` are `apply` only; `--strict` is
  `plan`/`apply` only. `vela update` rejects an unknown option instead of ignoring it.
- **The legacy direct-assembly registration path is removed.** `apply` used to upload a bare plugin assembly and
  create its plugin types itself when no plugin package was given. Every scanned input is a plugin package, so the
  path was unreachable; the platform materializes the plugin types from the package. A plugin type that does not
  appear after the package publish fails `apply` with `Plugin type not found in Dataverse after the plugin package
  publish`.
- **The scan no longer accepts a non-array `GetStepDefinitions()` result.** A single returned object used to be
  wrapped into a one-element set (a pre-array framework shape); it now fails the scan with a clear message.
- **An `apply` that does not converge exits 5 (#445).** After its writes, `apply` re-reads the environment; when it
  still differs from the packages (the platform ignored or normalized a value), the run used to print a note and exit
  0, so CI went green on an environment the next `apply` would change again. It now exits 5 (drift), with mode
  `apply-not-converged` and the remaining diff in `summary` / `diff`.

### Added

- **`vela init` writes an `AGENTS.md`** at the solution root: instructions for AI coding agents on how to write a step
  (typed trigger, required `FilterOn`, images, `WithStepKey`, `ctx.Service` / `SystemService` / `AsUser`), a typed
  Custom API and a `PluginPipeline` test in this solution, and how to drive `vela models`, `vela add`,
  `vela plan --json` and `vela apply`. Like the README, it is written only when absent, so `--force` keeps your edits.
- **`apply` writes `asyncautodelete`** for a step declared `Async(deleteJobOnSuccess: true)`, and `plan` diffs it.
- **Several steps on one trigger** are created, updated and deleted as separate steps. A changed step that pairs
  with its live step under another key carries `liveStepKey` in its `after` definition, so `apply` updates that step.

- **Image request property in plan and apply (#462).** The step shape compares each image's `messagepropertyname`,
  so `plan` shows an image moved between the Merge master and subordinate as a change and `apply` recreates it.
  The scanner rejects `SubordinateId` on any message other than Merge (the platform fails with 0x8004416B).
- **`vela help <command>` and `vela help --json`.** The help is generated from one command table that is also the
  option allowlist of `plan`, `apply`, `inspect`, `sign`, `logout`, `update`, `solution`, `logs` and `models`, so it
  lists every command (`solution` was missing) and every option a command accepts. `vela help --json` describes the
  commands, options and exit codes for agents and completion scripts.
- **A versioned JSON contract for `plan` / `apply` / `inspect`.** Every document starts with `"schemaVersion": 1`,
  bumped only when an existing field changes meaning or shape. The new
  [Machine-readable output](docs/reference/machine-readable-output.md) page documents the fields and the exit codes.
  Information that is not a warning (an assembly not yet in Dataverse, for example) is listed under a new `info`
  array, alongside `diagnostics` (warnings, which `--strict` fails on) and `unmanaged`.
- **`vela plan --detailed-exitcode`** exits `8` when the plan found changes to apply (including a package republish)
  and `0` when there is nothing to do, so a pipeline can branch on the plan without parsing it.

- **`--secure-config NAME=VALUE|NAME=@file`** (repeatable) on `plan`/`apply`/`inspect`, with the
  `VELA_SECURE_CONFIG_<NAME>` environment variable as a fallback. A package built with an older framework that still
  compiles a `WithSecureConfig` literal no longer has that value deployed: the environment keeps its current value.
- **`vela models --custom-apis` keeps a parameter's table.** A parameter with a `logicalentityname` gets
  `[LogicalEntityName("...")]`, so a Custom API authored from the generated model registers the same table.
- **`vela init --samples`** opts in to the demo code (sample models, services, plugins and Custom APIs, and the
  Integrations and ConsoleApp projects). See "Changed (CLI)" for the new minimal default.
- **`vela init --publisher-prefix <prefix>`** sets the Dataverse publisher prefix of the Custom API unique names,
  separately from the .NET `--prefix`. It is validated (2-8 characters, letter first, alphanumeric) and recorded in
  the generated projects as `VelaPublisherPrefix`, which `vela add customapi` and `vela add test --kind customapi`
  then use when `--prefix` is not passed.
- **`vela sign --pfx-base64-env <VAR>` / `--pfx-password-env <VAR>`** read the certificate and its password from a
  named environment variable, so neither appears on the command line where other users of the host can read it.
  The generated signing target passes the password this way.

### Changed

- **`vela init` scaffolds plugin projects on .NET Framework 4.8.** Microsoft confirmed 4.8 as the supported and
  recommended version for plug-ins (4.8.1 is not supported), so the generated `Plugins` and `Plugins.Tests`
  projects target `net48` and `Shared` targets `net48;net10.0`. The `net471` cap on plugin packages came from a
  platform bug that Dataverse 9.2.26092 fixes. The scanner no longer warns about `net472`/`net48` packages; it
  warns above `net48` instead. When Dataverse rejects a package above `net471` (an environment older than the fix),
  `vela apply` names the target framework and says to drop to `net471`. A package shipping several `net4x` builds
  still prefers the highest one at or below `net471`, so it registers on every environment.
- **`vela init` is minimal by default (breaking).** It now writes the `Shared` and `Plugins` projects and an empty
  `Plugins.Tests` project (unless `--no-tests`), with no sample code, so a new solution deploys nothing until you
  add your own plugin. It builds, tests and plans unedited. The previous rich scaffold is `vela init --samples`;
  `--minimal`/`--no-demos` are kept as aliases of the default. The interactive wizard asks for samples first
  (default: no). Even with `--samples`, the `AccountMessageReference` cheat-sheet (one step per typed message) is
  now an `abstract` class: it compiles but is never registered, so its 16 disabled steps are no longer deployed.
- **`vela init --force` keeps your code.** It used to delete every managed project folder, user plugins included.
  `init` now records the files it writes, with a content hash, in `.vela/scaffold-manifest.json` (commit it). A
  `--force` regeneration overwrites or removes only listed files you have not edited since; your own files, edited
  samples and files from a scaffold without a manifest are kept and reported (`kept`/`removed` in `--json`).
- **`vela add step` allows several steps on one trigger.** Following the step-identity rule (steps told apart by
  `FilterOn`, images, `WithStepKey` or `ForRelationship`), it refuses only an exact duplicate: the same message,
  stage and mode with the same filtering columns. Otherwise it adds the step and suggests `WithStepKey` for clarity.
- **`vela add` rejects an option the kind does not use** (for example `add service --message`) instead of ignoring
  it, and `add step` no longer accepts `--force`, which it never used.

### Fixed

- **An unchanged plugin package is no longer republished on every `apply` (#492).** `plan` and `apply` compared
  the local package with the stored `pluginpackage.content`, which Dataverse empties once it has processed the
  upload (verified live), so every `plan` after an `apply` proposed `update package` (exit 8 with
  `--detailed-exitcode`) and every `apply` re-imported the package. They now compare the stored package file (the
  `package` column, byte-identical to the upload): its size first, then its content, downloaded in 4 MB blocks only
  when the sizes match. The stored `version` is not compared: a content update leaves it at its first value
  (verified live: still 1.0.0 after 1.0.1 was uploaded), so after a version bump every `plan` proposed the update
  again. The file includes the `.nuspec`, so a version bump is still published once. Verified end to end through
  `vela plan` / `vela apply`.
- **`apply` convergence and reporting gaps (#445).**
  - A step declared `.Disabled()` no longer runs enabled while `apply` writes the other components, and the step
    state is written with `Update` instead of the deprecated `SetStateRequest`. An existing step gets its state in the
    same update as its other columns. Dataverse ignores `statecode` on create (verified live: the step reads back
    enabled), so a new disabled step is updated to inactive right after its create, in the same `ExecuteMultiple`
    request, rather than after every other step and Custom API. Verified end to end through `vela apply`.
  - A step whose table is removed (it becomes global) has its message filter cleared. The update left the old
    filter in place, so every later plan showed the same change (clearing it verified live).
  - A batched write with no response item is reported as failed (outcome unknown), not as applied.
  - When a whole `ExecuteMultiple` request fails and the one-by-one replay fails too, the report and the resume
    summary keep the batch-level error (an expired token, a timeout) next to the replay's fault.
  - A removed Custom API is re-checked right before its delete and is deleted only while a plugin type of the
    applied assemblies still implements it, as the detach before a rename already did.
- **`plan` and `apply` read large environments without hitting query limits (#452).** The live read put every
  plugin type, step, secure configuration, Custom API and solution component id in a single `In` condition, one SQL
  parameter per value, so an assembly with thousands of steps could exceed SQL Server's 2100-parameter cap. The read
  now splits each list into chunks of 500. Recognizing the platform-managed step of a Custom API no longer reads
  every `customapi` in the environment once per assembly: only the messages of main-operation steps are looked up,
  and nothing is read when there are none.
- **The scaffolded README and next steps pass `--solution` to `plan` and `apply`.** They showed both commands without
  it, and on the scaffolded package (`Zava.Plugins`, which has no publisher prefix) both fail with exit 4.

- **The `vela init` samples no longer teach risky patterns (#444).** `AccountRelatedContactsPlugin` counted contacts
  with a `RetrieveMultiple` capped at one page and cached the result in the organization-wide plugin cache, so one
  user could read another user's privilege-filtered count. It now runs an aggregate count built with
  `Query.For<Contact>().Count(...).ToFetchXml()` (what the README and the test already claimed), without caching,
  and the [caching guide](docs/guides/caching.md) says what not to cache. The samples trace record ids instead of
  names, emails and greetings, the tests select the columns they assert instead of `new ColumnSet(true)` (VELA013),
  and each Custom API declares its unique name once as `UniqueName` (the estimate is the `PlaceholderEstimate`
  constant). The router test seeds the new contact, so the duplicate check's self-exclusion is tested, and the router
  uses `ctx.Response.id` for it. A new `PluginPipeline` test shows the step registration under test: it fails when
  `FilterOn` names the wrong column. A stale comment about the initiating-user service now describes the current
  user.

- **`vela add` and `vela models` use the real message names (#461).** `vela add --message Send` (or `Win`,
  `Close`, ...) now renders the typed trigger (`OnSendEmail()`, and `OnWinQuote()` or `OnWinOpportunity()` from the
  table). `vela models --messages` takes SDK request names, the names Dataverse executes: `--messages SendEmail`
  generates `SendEmailRequest`. It used to take sdkmessage names and emit, for `Send`, a `SendRequest` with
  `RequestName = "Send"`, which Dataverse refuses ("Request not supported", verified live); `--messages Send` now
  fails and names the requests to generate (`SendEmail`, `SendFax`, `SendTemplate`). Without a value, one class is
  generated per request, bound to the request's table, and the requests the SDK already ships are skipped
  (`SendRequest`, `WinRequest`... duplicates are gone). `plan` / `apply` also reject any image on
  `SendFromTemplate`, which Dataverse does not support (verified live).
- **A scaffolded solution's parallel build no longer packs a plugin twice.** When MSBuild could not match a test
  project's reference to the solution configuration (the path reaches it in a different form, e.g. through a
  symlink), it built the referenced plugin project a second time, without Configuration/Platform (Debug), in
  parallel with the solution's Release build. Both packed into the same `artifacts/<Plugin>.1.0.0.nupkg`, so the
  build failed with "being used by another process", or the Debug package could overwrite the Release one. `vela
  init` now sets `ShouldUnsetParentConfigurationAndPlatform=false` in the test projects, so such a reference
  inherits their configuration and is the same build, run once. Existing solutions can add the property to their
  test projects (#406).
- **An added step no longer takes over another step registered on the same trigger.** `apply` adopted any single
  live step on the trigger for a step whose key it did not find; it now adopts only the step the diff paired.

- **Step descriptions and Custom API display names and descriptions reach Dataverse.** `apply` wrote the step name
  as every step's description, and the unique name (or the parameter name) as every Custom API's display name and
  description and every parameter's description, whatever the code declared. It now writes `WithDescription(...)`
  (followed by the `[vela:stepkey=...]` line, which the step-key lookup now reads from the end of the description),
  `.DisplayName(...)`, `.Description(...)` and the parameter descriptions, falling back to the previous values when
  none is declared. These fields are part of the plan diff, so a change shows up and converges (a second plan is
  empty); Dataverse lets them change in place, so a parameter description is updated without recreating the
  parameter. Existing deployments that declare none show no change. `plan` rejects, before any write, a value
  longer than Dataverse stores (step description 256 characters with its step-key line, Custom API display name 100,
  description 300, request parameter description 300, response property description 100).
- **Bulk step images deploy, with the right image property for every message.** `plan`/`apply` rejected any image
  on a `CreateMultiple`/`UpdateMultiple` step, and `apply` registered every image against `Target` (or `id` for a
  Create post-image), which Dataverse refuses on several messages. Images are now registered against the property
  the message expects (verified live): `Id` for Create, `Ids` for CreateMultiple, `Targets` for UpdateMultiple
  (pre- and post-image, pre-image also at PreValidation), `EmailId` for Send/DeliverIncoming/DeliverPromote,
  `EntityMoniker` for SetState/SetStateDynamicEntity, and `Target` otherwise. `plan` now rejects, with a clear error
  and before any write, an image Dataverse refuses: a post-image before PostOperation (0x8004416E), a pre-image on
  Create/CreateMultiple or a post-image on Delete (0x8004416D), or any image on RouteTo (0x80044189). The rules
  live in one table shared by the fluent API, the harness, VELA007 and the CLI, so none of them accepts what
  another rejects (#435).

- **Every command has its own `--help`.** `vela init --help`, `sign --help`, `logout --help`, `plan`/`apply`/
  `inspect --help` printed the global usage; they now print the command's usage, options, notes and examples.
- **The package scan runs in a separate process.** Reading a package's steps instantiates its plugin types, so the
  package's code ran inside the `vela` process, next to its connection, access token, client secret and
  secure-config values. `plan`/`apply`/`inspect` and `vela models --package` now scan in a child `vela` process that
  holds none of that state and starts with an allowlisted environment (system paths, locale, .NET variables; no
  `DATAVERSE_*`, `VELA_SECURE_CONFIG_*`, `AZURE_*` or CI tokens inherited by accident), no stdin, and its output on
  stderr. It is not a sandbox: package code runs with your OS rights and can still read the parent's command line
  and environment, your files and the network, so only scan packages you trust, and keep secrets off the command
  line (`DATAVERSE_CLIENT_SECRET`, `--client-secret-stdin`, `--interactive`). `VELA_SCAN_IN_PROCESS=1` restores the
  in-process scan.
- **`--solution` now also holds the components that did not change.** A create or update lands in the solution,
  but a step, Custom API, plugin assembly or package deployed earlier (without `--solution`, or into another
  solution) was never written again and so never added: the solution exported without it. `plan --solution` lists
  each such component as an `add-to-solution` operation, and `apply` adds it (`AddSolutionComponent`, without
  required components); `executed.addedToSolution` counts them. Only unchanged components are listed: a changed one
  joins through its solution-scoped update and a removed one is deleted. Adding an unchanged package to the
  solution is not reported as a package republish.
- **`--packages-dir` with several versions of one package scans only the newest.** Both versions were scanned, so
  every step appeared twice (failing with a misleading "Duplicate local step key"), and the published package was
  whichever the disk listed first. The highest version (NuGet ordering) is kept and each skipped file is named on
  stderr.
- **`--strict` no longer fails the first deployment.** "Plugin assembly ... not found in Dataverse" (and "no plugin
  types found") were warnings, so `plan --strict` failed on every empty environment. They are now `info`; rank
  collisions, missing secure-config values and publisher-prefix mismatches are still warnings.
- **`--verbose` no longer drops the JSON.** With `--verbose` (piped or `--json`), `plan`/`apply`/`inspect` printed
  the human summary on stderr and then returned without writing the JSON document. The JSON is now always written;
  the summary stays on stderr. `--out <file>` reports "Plan written: ..." on stderr, so stdout stays empty.
- **`vela init --json` writes only JSON to stdout.** The "Scaffolding against Vela.Plugins ..." notice now goes to
  stderr.
- **`apply` reports a package-only change like `plan`.** The apply preview listed the package publish neither in
  `operations` nor in the JSON of a failed run, and an unprefixed package without `--solution` failed mid-run
  (exit 7) after the deletes; it now fails before anything is written (exit 4), as `plan` does. `plan`, `apply` and
  `inspect` share one orchestration path and one Dataverse connection per run (instead of up to four).
- **`vela models` names the `Id` override's column** (`[AttributeLogicalName("accountid")]`, as the SDK generator
  does), so `a => a.Id` selects the primary key column.
- **A second `apply --solution` no longer re-imports an unchanged plugin package.** Adding the publisher prefix
  repacked the package by extracting and re-zipping it, which stamped every entry with the current time, so the
  bytes never matched the package in Dataverse and every run re-imported it. The repack is now done in memory and is
  deterministic (original entry order and timestamps; only the `.nuspec` changes).
- **Custom API parameters converge when their optionality or table changes.** `isoptional` and
  `logicalentityname` are create-only but were updated in place; a change now recreates that one parameter (like a
  type change), and an unchanged parameter is no longer rewritten on every apply. `System.String[]` from an older
  package is read as StringArray, and a type with no Custom API equivalent fails the plan with a clear message
  instead of registering as `String`.
- **`apply` no longer deletes the registrations of classic plugins in the same assembly.** The live read took every
  plugin type of the assembly while the scan only produced the Vela ones, so the steps and Custom APIs of a classic
  `IPlugin`/`CodeActivity` class (registered with the Plugin Registration Tool, for example) diffed as "removed" and
  were deleted. They are now left untouched and reported under `unmanaged` in `plan`/`apply`/`inspect` output (info,
  not a warning). The registrations of a Vela plugin class you deleted are still removed.
- **`apply` no longer takes over a Custom API owned by another assembly.** A local Custom API whose unique name already
  existed, implemented by another assembly, was silently re-pointed to the local plugin while the plan said "create".
  `apply` now stops with an error naming the owning type and assembly.
- **`vela add plugin` / `add step` no longer generate a plugin that fails at runtime.** For Create/Update (and the bulk
  variants) without `--filtering-attributes`, they emitted a `// TODO` instead of `FilterOn`, so the default
  `vela add plugin X --entity Account` produced a step that failed to build. The option is now required for these
  messages: the command names it and writes nothing, and `vela add --interactive` asks for the columns.
- **`apply` no longer deletes a secure configuration it was not given.** A step declaring none had its live secure
  config unlinked and deleted, wiping a value an admin set in the environment. Without a value, the environment now
  owns it: no diff, never written, cleared or deleted.
- **A secret no longer leaks between plugins sharing a step key.** Secure configuration values were keyed by step key
  alone, so two plugin types using `WithStepKey("main")` received each other's secret (and the plan never converged).
  They are now keyed by plugin type + step key.
- **The secure-config digest in plan output can no longer be brute-forced.** It was a plain SHA-256 of the secret; it
  is now an HMAC keyed per run.
- **Deleting a step also deletes its secure configuration row**, which Dataverse does not cascade.
- **`vela add customapi --function` generates a function that builds.** It had no response property, which the
  framework now rejects (Dataverse does not expose such a function); it gets a placeholder `Result` to rename.
- **Renaming a Custom API's plugin class no longer blocks the package upload.** With the same unique name, the Custom
  API was re-pointed to the new class only after the upload, which had to remove the old plugin type while the Custom
  API still referenced it. `apply` now detaches the Custom API before the upload and points it at the new type after.
- **A removed step is deleted only within the assemblies being applied.** The delete re-read every plugin type of
  the environment for each step and, when the type did not resolve, fell back to an environment-wide match on the
  step name, which could delete a same-named step of another assembly. The plugin types are now read once, from the
  applied assemblies only, and there is no unscoped fallback.
- **The apply report matches what landed.** When a whole `ExecuteMultiple` call failed after the server had run it
  (a timeout, a dropped connection), the one-by-one fallback replayed its creates, which then failed with "duplicate
  key" and were reported as failures; a replayed create whose id already exists now counts as applied. The resume
  report listed everything after the earliest failure as "not applied", including steps that landed in the same
  continue-on-error batch, and showed only one failure: each operation is now listed under its own outcome
  (completed / failed / not applied), and a step is named with its step key. `--verbose` progress lines
  (`[step] create ...`) were printed before the batch ran; they are now printed after it, and a failed one ends with
  `(not applied)`.

- **`vela init --core-version=X` is honoured.** The latest-version lookup checked the raw arguments for
  `--core-version`, so the inline form was overwritten with the latest nuget.org version. `init` also accepts
  `--verbose`, which it rejected as unknown.
- **`vela add plugin --message Assign` emits `OnAssign()`.** It used `OnMessage("Assign")`, which analyzer VELA006
  flags; the list of typed triggers is now pinned to the step API.
- **`vela add step` refuses an exact duplicate step.** With its defaults it could add a second step with the trigger
  and filtering columns `vela add plugin` already registered, which no registration can tell apart. It now names the
  existing step and writes nothing.
- **`vela add customapi` no longer emits an unregistrable unique name.** A project prefix that is not a valid
  publisher prefix (more than 8 characters, for example) was used as is; the command now asks for `--prefix`.
- **Generated tests assert something.** `vela add test` produced tests whose assertions were commented out. A
  plugin test now checks that its handler ran; a Custom API test checks the unique name and that every declared
  output is set (supplying inputs with `WithInputParameter`). A bound Custom API function sets its placeholder
  `Result`.
- **`vela sign` no longer exposes the certificate or its password.** The temporary `.pfx` from `--pfx-base64` was
  written and then restricted, leaving a window where it was world-readable on Unix; it is now created owner-only.
  The PFX password was passed to `dotnet nuget sign` on its command line; `vela sign` now opens the PFX itself and
  hands the tool a passwordless copy in that owner-only temp file, deleted after signing. The generated signing
  target quotes every MSBuild property.
- **`vela models` no longer collides a global choice with a table class.** A global option set named like a
  generated table (even only by case) produced a duplicate type and a file that overwrote the class on a
  case-insensitive file system; the enum is now suffixed.
- **The `vela init --samples` docs match the code**: the related-contacts sample runs a `QueryExpression`, not
  FetchXML, and steps end with `Handle(...)`, not `Handler(...)`. The sample tests request explicit columns
  (VELA013). The offline fallback framework version is now 0.31.0 (it was 0.28.0), pinned to the CHANGELOG by a test.
- **`vela add test` and the step injector handle more source.** The injector skips raw (`"""`) and `@$` verbatim
  strings when matching braces and ignores commented-out registrations.

## [0.31.0] - 2026-10-06 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.31.0`.

### Added

- **One-call typed query execution and scalar aggregates.** On a `TypedQuery`, `FirstOrDefault(service)` returns
  the first match (or `null`) with no `.Build()`, and `CountScalar` / `SumScalar` / `AverageScalar` /
  `MinScalar` / `MaxScalar` run a single aggregate and read the typed result back in one call - no alias string,
  no `AliasedValue` to unwrap. `Count`/`Sum`/`Average` return `int`/`decimal?`; `Min`/`Max` return the selected
  column's own type (a `DateTime?` column hands back `DateTime?`, a money column `decimal?`). Each runs against a
  stripped clone, so the caller's query is never mutated.
- **`OnAssign()` trigger.** A first-class builder trigger for the Assign message (owner change), alongside the
  existing `On*` triggers, with a typed `{Handler}Context`.
- **Typed merged-image accessors.** `ctx.GetPreImageMerged<T>()` / `ctx.GetPostImageMerged<T>()` (and the bulk
  variants) overlay the captured image with the step Target so a handler sees the effective record without
  registering the opposite image.
- **`Clone<T>()` on early-bound entities.** A create-ready detached copy (id reset, audit/row-version/key
  columns dropped, attribute values deep-copied, nested collection records cloned) for the common
  duplicate-a-record pattern, replacing a hand-rolled attribute-by-attribute copy.
- **Uniform typed step context on every message and stage.** The generated `{Handler}Context` is now emitted for
  every named handler - not only image-capturing CRUD steps - so a handler's signature is the same typed context
  throughout, with `ctx.PreImage`/`ctx.PostImage` typed to the captured columns where present.
- **VELA018 and VELA019.** VELA018 warns when an `.Async()` step writes the step Target (the write is lost after
  the synchronous commit). VELA019 flags `System.IO` filesystem access inside a plugin (the sandbox has no file
  system), with a "Respect the sandbox" guide covering the HTTP allow-list and the two-minute budget.
- **Associate / Disassociate register for any entity.** These two relationship messages register with no primary
  entity (Dataverse has no per-entity filter and the Target is either relationship end); the handler filters on
  `ctx.Request.Relationship`.

### Changed

- **`Vela.Plugins` trims its .NET Framework 4.6.2 dependency graph.** It now references only
  `Microsoft.Extensions.DependencyInjection.Abstractions` instead of the full `Microsoft.Extensions.DependencyInjection`
  package (the runtime never builds a service provider), reducing the transitive 8.x footprint a sandboxed plugin
  carries.
- **VELA012 no longer flags `PluginBusinessException`.** Throwing `PluginBusinessException` from a plugin that
  derives from a Vela base type (`PluginBase`/`EntityPluginBase`/`CustomApiBase`) is the blessed business-error
  path and is no longer reported; a raw `IPlugin` (which has no Vela catch frame) is still flagged. Helper/service
  code should throw `InvalidPluginExecutionException` directly.
- **VELA008's message now explains same-trigger steps.** The diagnostic describes why two steps on the same
  message/stage/mode in one class collide and how to consolidate (one step, a union of filtering attributes, and
  per-column `HasChanged`/`Contains` in the handler).

### Fixed

- **Plugin DI no longer silently elevates to the system user.** `AddPluginRuntime` registered both the user and
  system organization services under `IOrganizationService`, and Microsoft DI returns the last registration, so
  every consumer resolving `IOrganizationService` silently got the **system** service. It now registers the user
  service (matching the lightweight resolver); use `ctx.SystemService` or the `Func<Guid?, IOrganizationService>`
  factory for a system/impersonated call.

## [0.23.0] - 2026-10-06 (Vela.Cli)

CLI line, published as `cli-v0.23.0`.

### Added

- **`vela logs` / `vela trace`.** A read-only reader of the Dataverse plug-in trace log (and `--jobs` for failed
  system jobs), with filters for `--top` / `--since` / type / message / entity / correlation / errors. The human
  view truncates; `--json` is complete - so an agent can read plug-in traces without a human.
- **`vela models --intersect`.** Auto-discovers and generates the N:N intersect tables of the scoped tables (one
  level, opt-in, deduplicated), so typed queries can traverse many-to-many relationships.
- **`vela plan` surfaces a code-only rebuild.** When steps and Custom APIs are unchanged but the assembly was
  rebuilt, `plan` now lists a `package` operation (create, or update when the stored version or content differs)
  and sets `hasChanges`, instead of reporting "no changes" for a rebuild that `apply` would deploy.
- **`vela plan --solution` checks the publisher prefix.** It warns for every Custom API being created or updated
  whose unique name lacks the solution's publisher prefix - the mismatch `apply` rejects - so it is caught in the
  preview (and fails the command under `--strict`).
- **`vela init --minimal` (alias `--no-demos`).** Scaffolds a bare `Shared` + `Plugins` solution with no sample
  code and none of the demo-only Integrations/Tests/ConsoleApp projects - a clean, build-ready canvas to fill in
  with `vela models` and `vela add`. The rich default is unchanged.
- **`apply` reports what it executed in JSON.** The `apply-execute` JSON carries an `executed`
  block - `{ created, updated, deleted, packageRepublished, byKind: [...] }` from the pre-apply diff - so a
  script or agent reads the executed counts directly instead of parsing the free-text notes.
- **`vela add service` reports the DI registration line in `--json`.** A new `registration` field gives the
  `services.AddScoped(...)` line to wire up (omitted when there is nothing to register).
- **The scaffold references `System.Net.Http`.** Generated plugin projects reference it (with a TLS 1.2 snippet)
  so a net462 integration plugin using `HttpClient` compiles without a manual reference.

### Changed

- **`apply --solution` warns before the confirmation gate on a cross-solution assembly overwrite.** When the
  target plugin assembly already exists in the org outside the target solution, the heads-up now precedes the
  prompt so an interactive user can decline knowing the risk.
- **`vela plan` lists only the assemblies actually found in the org** under `currentAssemblies`, instead of the
  requested names - so it no longer lists an assembly it also reports as not found.

## [0.30.0] - 2026-10-02 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.30.0`.

### Added

- **VELA017: reading an image the step did not capture.** A post-operation context exposes both `PreImage` and
  `PostImage` (a pre-operation one exposes `PreImage`), so reading `ctx.PreImage`/`ctx.PostImage` when the step
  never called `WithPreImage`/`WithPostImage` compiled but returned null at runtime. VELA017 reports it as an
  **error** when the handler is bound by exactly one `RegisterStep()` chain that lacks the capture, including
  null-conditional reads (`ctx?.PreImage`). Split chains and partial-class splits stay silent (under-report,
  never a false positive), like VELA009.
- **Explicit cast from a typed image projection to its entity.** `(Account)ctx.PreImage` widens a projection to
  the full early-bound entity for shared services that take one. The projection now keeps its source image, so
  the cast is lossless (Id, logical name and every attribute), equivalent to `GetPreImage<Account>()` /
  `GetPostImage<Account>()`, and an absent image casts to null. The cast is explicit on purpose: uncaptured
  columns read null again once you leave the projection.
- **The trace log names the dispatched handler.** Each dispatch writes
  `Entering handler 'OnUpdated' for message 'Update'.`, so a failure in a multi-step plugin points at its
  handler. Lambda/local-function handlers (compiler-generated names) are not traced. Opt out with
  `protected override bool AutoTraceHandlerEntry => false;`. Visibility still follows the org's Plugin Trace Log
  setting.
- **`WhereIn`/`WhereNotIn` and `ConditionIn`/`ConditionNotIn` take an `IEnumerable<TValue>`.** On `TypedQuery`,
  `TypedLink` and `TypedFilterBuilder`, each element becomes its own condition value, so a `Guid[]` no longer has
  to be widened to `object[]` (it used to bind as one nested value through `Where(column, In, params object[])`).
- **Testing: `Build(plugin)` / `Build<TPlugin>()`** infer the message/stage/mode from the plugin's single step,
  like `Execute(plugin)` / `Execute<TPlugin>()`. The plugin-less `Build()` is unchanged.

### Changed

- **Testing: the harness rejects a Custom API call that omits a required input parameter.** It throws
  `InvalidPluginExecutionException` before the handler runs, like the platform. Previously the call was
  accepted, so a test could pass on an invalid request; such tests now fail and need the parameter supplied.

### Fixed

- **Testing: `Execute<TPlugin>()` works with generated Vela plugins.** It required a parameterless constructor
  (`new()`), which the `(string unsecureConfig, string secureConfig)` primary constructor does not provide. It
  now uses a parameterless constructor if present, otherwise the `(string, string)` one with empty
  configuration, otherwise a clear error pointing at `Execute(plugin)`.
- **`TypedQuery`'s XML docs attach to `TypedQuery` again** (a helper type sat between the comment and the
  declaration).

## [0.22.0] - 2026-10-02 (Vela.Cli)

CLI line, published as `cli-v0.22.0`.

### Changed

- **`vela add plugin` no longer guesses a filtering column.** Without `--filtering-attributes`, a Create/Update
  plugin used to auto-select a column from the early-bound model (and failed when none could be inferred). It
  now leaves a `// TODO: add .FilterOn(e => e.Column)` comment, matching `vela add step`, so the file compiles
  and the author picks the real filter. An explicit `--filtering-attributes` is unchanged.

### Fixed

- **Plugin packages targeting above .NET Framework 4.7.1 now produce a warning.** Dataverse rejects them at
  registration, even though Microsoft's docs recommend `net48` for plug-ins. When a package ships several
  `net4x` builds, the scanner now prefers the highest one at or below `net471` (previously `net472`/`net48`
  won).
- **`vela add test` seeds `WithTargets(...)` for a `CreateMultiple`/`UpdateMultiple` plugin.** The generated
  test always used `WithTarget(...)`, leaving `ctx.Request.Targets` null so the test threw. `add test` now takes
  `--message`; `RetrieveMultiple` (query-based, no `Targets`) keeps the single-record form, as does the
  `vela add step` handler access hint.
- **Generated files are written with LF line endings.** Templates follow the checkout's EOL (CRLF on Windows)
  while injected code uses `\n`, so files such as those touched by `vela add step` could mix endings. All
  generated-file writes (`vela add`, `vela models`, `vela init`, and the `web-resources.entries.ts` updates) now
  normalize to LF.
- **Scaffolded Custom API request/response XML docs give the right parameter key.** They said properties key
  the bag with `<uniquename>.{prop}`; the real key is the plain property name (no dot, no prefix).

## [0.29.11] - 2026-09-29 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.11`.

### Added

- **Message localization service (i18n) for plugins (#312).** A new `IMessageService` resolves messages by key in the current (initiating) user's UI language (`usersettings.uilanguageid`), falls back to a configurable default language, and formats templates with `string.Format`. Pluggable sources ship in the box: an in-code `DictionaryMessageSource` and a `DataverseTableMessageSource` (configurable labels table, read once through the elevated system service and cached like environment variables). Reach it from a handler via `ctx.GetMessage("KEY", args)` / `ctx.TryGetMessage(...)`, throw a localized business error with `throw ctx.BusinessError("KEY", args)`, and register sources in a plugin's `ConfigureServices` override. Registered by default in the execution resolver.

## [0.21.4] - 2026-09-29 (Vela.Cli)

CLI line, published as `cli-v0.21.4`.

### Added

- **`vela models --from-solution <zip>` generates C# early-bound models offline (#313).** Reads table metadata from the `customizations.xml` inside an exported (unmanaged) solution zip and generates the same early-bound classes and option-set enums as the online path, with no Dataverse connection - useful for migrations/onboarding or as a fallback when a connection is unavailable. Scope defaults to the zip's custom tables (narrow with `--entities`). Degrades gracefully on what an export does not carry: an unmapped column type is skipped, a lookup keeps no typed targets (the export stores numeric type codes, not logical names), a global option set absent from the file falls back to a raw option-set value, and relationships are not emitted in this first version. Offline output is C# only.

## [0.29.10] - 2026-09-29 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.10`.

### Added

- **`IOrganizationService.RetrieveAll(TypedQuery<T>)` and `TypedQuery<T>.ToList(service)` auto-paging execution helpers (#314).** They run a typed query to completion, following `MoreRecords`/`PagingCookie` page by page, and return a typed `List<T>` - removing the hand-rolled paging loop. A query with a `Top` is honored with a single retrieve (Top and paging are mutually exclusive in Dataverse). Page size defaults to 5000 (the Dataverse maximum) and is configurable.

## [0.21.3] - 2026-09-29 (Vela.Cli)

CLI line, published as `cli-v0.21.3`.

### Added

- **`vela solution show <uniquename>`** - a read-only command that connects to Dataverse and prints a solution's publisher customization prefix (#321). Use it to align a scaffold's `--prefix` with the target solution up front (`vela init MySolution --prefix "$(vela solution show mysolution -i)"`) instead of discovering a prefix mismatch only at `apply`. Supports `--json` (`{"solution":"...","publisherPrefix":"..."}`) and the standard connection flags.

## [0.21.2] - 2026-09-29 (Vela.Cli)

CLI line, published as `cli-v0.21.2`.

### Fixed

- **A scaffolded solution now builds even inside a repository that uses Central Package Management (#315).** `vela init` pins package versions inline, which is invalid once a parent `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`) is inherited from an enclosing repo, producing NU1008 on every project. The scaffold now emits a solution-root `Directory.Packages.props` that turns CPM off; being the nearest such file, it shadows any inherited parent and keeps the inline versions valid.

## [0.29.9] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.9`. Test-harness fidelity audit (#303): the Merge message.

### Added

- **`MergeRequest` is now supported by the in-memory harness** (previously threw "not supported"). Merge moves the subordinate's non-conflicting data onto the target without overwriting the target's own values, applies `UpdateContent` to the target (ignored for `incident`, per the platform), reparents the subordinate's 1:N children to the target, then deactivates the subordinate - the whole operation is atomic. Restricted to the tables Dataverse supports (account, contact, lead, incident); other tables fault.

## [0.29.8] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.8`. Test-harness fidelity audit (#303): standard ownership/customer metadata types.

### Fixed

- **`FakeEntityMetadata.From<T>()` maps the standard `ownerid` and `customerid` columns to their true attribute types** - `Owner` and `Customer` respectively - instead of a plain `Lookup`, so metadata built from a generated early-bound type reflects Dataverse's ownership/customer semantics. Custom (prefixed) lookups are unchanged.

## [0.29.7] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.7`. Test-harness fidelity audit (#303): N:N cleanup on delete and deterministic date operators.

### Fixed

- **Deleting a record removes its N:N intersect rows.** A many-to-many intersect row was left dangling, pointing at a record that no longer existed, after that record was deleted. Delete (and cascade delete) now removes the intersect rows of every declared or metadata-declared N:N relationship on either side of the deleted record.
- **Relative date operators are deterministic.** `Today`/`Yesterday`/`Tomorrow`/`ThisMonth`/`ThisYear`/`LastXDays`/`NextXDays`/`OlderThanXDays` used the host's local `DateTime.Today`, making a query's result depend on when and where the suite ran. They now resolve "today" from the service's injected clock (`WithClock(...)`, UTC by default), so date-based query tests are reproducible.

## [0.29.6] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.6`. Test-harness fidelity audit (#303): aggregate over no rows.

### Fixed

- **A `sum` aggregate over no (non-null) values now returns null, not 0**, in the in-memory query engine - matching Dataverse (and how `avg`/`min`/`max` already behave), so an empty sum is distinguishable from a real 0.

## [0.29.5] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.5`. Test-harness fidelity audit (#303): transaction atomicity.

### Fixed

- **Bulk writes and deep inserts are now all-or-nothing in the harness.** `CreateMultiple`/`UpdateMultiple` and a single `Create` carrying `RelatedEntities` (a deep insert) committed rows one by one, so a fault partway through left partial rows - contradicting Dataverse's "the whole batch is one transaction". They now run inside a store snapshot and roll the whole write back on any fault.
- **An owner/state Update runs as one atomic operation.** An Update carrying owner (`ownerid`) or state (`statecode`/`statuscode`) fields alongside standard fields fires two step passes; a failure in the second (owner/state) pass previously left the first (standard) pass committed. Both passes now share one snapshot scope, so a second-pass failure rolls the first pass back.

## [0.29.4] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.4`. Test-harness fidelity audit (#303): security opt-in guard, 1:N association, and alternate-key lookups.

### Added

- **`InMemoryOrganizationService.WithStrictSecurity()`** opts into strict security: a security-relevant operation run without a configured security model throws instead of silently granting full access. This closes the default-off footgun where an access-control test that forgot `WithSecurity(...)` passes green because everything is allowed.

### Fixed

- **`Associate`/`Disassociate` now support 1:N relationships**, not only N:N. A 1:N schema name sets (Associate) or clears (Disassociate) the child's lookup - from either side of the relationship - instead of throwing "not a declared many-to-many".
- **`Associate` on an already-associated N:N pair now faults** (like Dataverse) instead of silently no-op'ing, so a plugin that catches that duplicate-association fault can be tested.
- **A lookup supplied as an alternate-key `EntityReference`** (KeyAttributes set, Id empty) is now resolved to a concrete id at write time on Create/Update, so the persisted lookup points at a real record and later joins/reads find it. A key that matches no record faults, like relating to a missing record.

## [0.29.3] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.3`. Test-harness fidelity audit (#303): deep-insert plugin pipeline.

### Fixed

- **Deep-insert children (and grandchildren) now re-enter the plugin pipeline in the in-memory harness.** A `Create` whose target carried `RelatedEntities` persisted the descendants directly through the store, so their own Create pre/post steps and images never fired - a plugin broken on a deep-inserted child would still pass in the harness. The pipeline now persists each record through a non-recursing store primitive and drives the child recursion itself, so every descendant fires its Create steps, mirroring how cascade delete already routes each record through the pipeline. Persistence is unchanged for direct (non-pipeline) `Create` callers, and each record is still persisted exactly once (no double-write).

## [0.29.2] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.2`. Test-harness fidelity audit (#303): file-download access control.

### Fixed

- **A File-column download in the in-memory harness now enforces the same access control as `Retrieve`.** `InitializeFileBlocksDownload` streamed a File column's content with no record-level Read demand and no field-level-security check, while `Retrieve`/`RetrieveMultiple` masked the same column - so an access-control test could pass in the harness while the identical read would be blocked on a real org (a false green). The download now applies a Read demand on the record and, for a field-secured column, requires field read.

## [0.21.1] - 2026-09-28 (Vela.Cli)

CLI line, published as `cli-v0.21.1`. plan/apply fidelity audit (#305): casing convergence.

### Fixed

- **A step or Custom API is no longer reported as "changed" when only the casing of its message name or unique name differs.** Dataverse compares a step message, a Custom API unique name and the derived display name case-insensitively, so a mixed-case code declaration diffed as a perpetual "changed" against the live read and `apply`'s update silently no-op'd it. The diff now folds only those top-level fields to lower case for comparison, while `apply` still writes the authored casing; case-significant configuration and nested parameter unique names (the dotted-name self-heal) still compare exactly.

## [0.29.1] - 2026-09-28 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.1`. Test-harness fidelity audit (#303): query and alternate-key fixes.

### Fixed

- **The FetchXML parser now maps every operator the query engine already evaluates.** `between` / `not-between`, the date operators (`on`, `on-or-before`, `on-or-after`, `today`, `yesterday`, `tomorrow`, `this-month`, `this-year`, `last-x-days`, `next-x-days`, `olderthan-x-days`) and the user operators (`eq-userid`, `ne-userid`) previously threw "not supported" from the parser even though the engine supported them, so a `ToFetchXml()` round-trip through the harness broke. A parity test guards the two sides against future drift.
- **Alternate-key resolution is now case-insensitive, matching Dataverse and the harness's own uniqueness check.** Resolution compared strings case-sensitively while uniqueness did not, so upserting `"abc"` against a stored `"ABC"` faulted as a missing key instead of updating the record. Resolution now uses the shared value comparer.

## [0.21.0] - 2026-09-28 (Vela.Cli)

CLI line, published as `cli-v0.21.0`. plan/apply fidelity & safety audit (#305): scanner hardening,
web-resource publish safety, and apply/report accuracy.

### Added

- **`--batch-size` apply option** to set the ExecuteMultiple chunk size (1..1000, default 100), for tuning against org throughput or working around ExecuteMultiple limits.

### Changed

- **The package scanner runs each package type's construction and definition method under a timeout** (30s), so an infinite loop or blocking call in a scanned assembly aborts the scan with a clear message instead of hanging the CLI (including `vela models --package`). The scanner's assembly load context is now collectible and unloaded after the scan.
- **Web-resource writes publish what was already committed even if a later upsert fails.** A mid-loop failure previously left earlier created/updated web resources unpublished (invisible/stale); the committed writes are now published before the failure is surfaced.

### Fixed

- **The live registration read normalizes step rank** the same way the local/diff side does, so an externally set rank of 0 or negative no longer diffs as a spurious "changed".
- **The apply resume report's per-group counters include "ignored" operations.** `RecordIgnored` now bumps the per-group tally (previously only the aggregate), so the half-applied summary is accurate.
## [0.29.0] - 2026-09-27 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.29.0`. Runtime robustness audit (#306): retry idempotency, a bounded
cache, a Custom API concurrency fix, and dependency-injection lifetime hygiene.

### Changed

- **`RetryPolicy` (and `PluginBase.ExecuteWithRetry`) no longer retry timeouts by default.** A write that times out client-side may already have committed on the server, so a blind retry could create a duplicate. Retries now apply only to safe-to-replay throttling / service-protection faults (by error code or message). Timeouts (`TimeoutException`, `WebException`, or a fault whose message mentions "timeout") are retried only when the caller opts in for an idempotent operation via the new `retryOnTimeout` parameter or `ExecuteIdempotentWithRetry`. Backoff is now exponential with full jitter (was linear); `maxAttempts < 1` and a negative delay are now rejected instead of silently skipping the operation.

### Added

- **`PluginBase.ExecuteIdempotentWithRetry<T>(...)`** and a `retryOnTimeout` overload on `RetryPolicy.Execute` for operations that are safe to replay on a timeout.
- **`PluginCacheService` now accepts a maximum entry count** (`new PluginCacheService(maxEntries)`, default `PluginCacheService.DefaultMaxEntries`) exposed via the `MaxEntries` property.

### Fixed

- **The typed `CustomApiBase<TRequest, TResponse>` definition build is now thread-safe.** Its `GetCustomApiDefinition` override bypassed the base class's lock and memoization, so concurrent first executions on the sandbox-reused instance could null the shared builder mid-build (a spurious "can only call inside RegisterCustomApi") or cross-wire two builders. The lock and one-time memoization now live in a single sealed `GetCustomApiDefinition`, and subclasses customize the build through a protected `BuildDefinitionCore()` that runs under that lock.
- **The global plugin cache is bounded.** Expiry was lazy (an entry dropped only when its own key was fetched again), so the static process-wide cache accumulated distinct short-lived keys for the worker's lifetime. When an insert pushes the count over the cap, a sweep first drops already-materialized expired entries, then evicts the oldest-inserted entries until the count is back at the cap.
- **The dependency resolver disposes the transient `IDisposable` services it created** at the end of each execution. Scoped and platform-owned services (context, organization service, tracing) are left untouched.
- **Re-registering a service with a different lifetime no longer leaks the previous one.** Re-registering a scoped type as transient now drops the cached scoped instance and stops caching, instead of continuing to hand out the stale instance.
- **The opt-in cross-execution environment-variable cache key is scoped by organization id**, so a value cannot bleed across organizations should a worker be shared.
- **`IOrganizationService.DeleteFile(target, fileAttributeName)` fails loudly when the column does not read back as a file id** (wrong column name, or not a File column) instead of silently doing nothing; a genuinely empty column is still a no-op.

### Performance

- **The per-execution step-context construction reflection is cached per handler parameter type** (the derive check and the `(ILocalPluginContext)` constructor lookup), leaving only the constructor invocation on the hot dispatch path.

## [0.20.0] - 2026-09-27 (Vela.Cli)

CLI line, published as `cli-v0.20.0`. Model-generation fidelity audit (#308): deterministic TypeScript table
output plus option-set and lookup correctness fixes.

### Changed

- **TypeScript table columns are emitted in deterministic logical-name order (primary key first).** They previously followed raw SDK metadata order, which is undocumented and unstable across environments and edits, so the C# generator sorted while the TS path did not - regeneration reshuffled members and the shared `option-sets/*.ts` reference order, producing spurious diffs. TS tables now match the C# generator's determinism guarantee. Regenerating existing TypeScript models produces a one-time reordering diff.

### Fixed

- **Two distinct global option sets whose names map to the same identifier no longer collide.** When two different global sets sanitized (TS) or Pascalized (C#) to the same enum name, only the first was kept and attributes referencing the second were silently typed to the wrong enum. The global registry now dedups by set identity (MetadataId, else name) and disambiguates a true name collision, so neither set is dropped nor mis-typed.
- **A C# local (nested) choice enum name is uniquified against the table's other members.** The nested `{prop}OptionSet` enum shared the table's declaration space with its properties and methods, so a sibling column named for example `StatusOptionSet` collided with the generated enum (CS0102). The enum name now goes through the same used-name set as the members.
- **Owner/Customer lookups returned with an empty `Targets` list keep their polymorphic logical-name union in TypeScript.** Some metadata payloads omit the (implicit) target pair; the generated record dropped the `_x_value@...lookuplogicalname` union in that case. Owner now defaults to `'systemuser' | 'team'` and Customer to `'account' | 'contact'`.

## [0.28.2] - 2026-09-27 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.28.2`. Second pass of the analyzer/generator audit (#309).

### Fixed

- **Code fixes for VELA004 (remove `static`) and VELA015 (use the step context type) now edit the file that owns the handler.** When the handler method was declared in a different file from the `.Handle(...)` call (a partial class, or a handler defined elsewhere), the fix operated on the wrong document and threw "node not in tree". Both are now Solution-based and edit the handler's own document.
- **A generated image projection on a value-type column is nullable.** A non-nullable value-type column (for example an authored `int` model property) was projected as a non-nullable field, so an absent image column read its default (`0`) - indistinguishable from a real value. Value-type projected members are now `T?` (early-bound models are already nullable, so this only changes an authored non-nullable value-type selector).
- **On a projection-name collision, the first shape keeps the plain name.** When two steps produced the same projection name with different captured columns, every shape got a hash suffix, making a lambda-handler projection unnameable. The first shape (ordered deterministically) now keeps the plain, guessable name; only later colliding shapes are hash-suffixed.

### Notes

- VELA015 typed `On<TRequest>()` / permissive `OnMessage(...)` coverage (M1) remains a documented known limitation, not an extension.

## [0.28.1] - 2026-09-27 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.28.1`. Analyzer accuracy and performance (first pass of the analyzer audit, #309).

### Fixed

- **VELA001 sees through `as` and double casts.** A base-`Entity` member selected via `x.LogicalName as object` or a nested/double cast was not flagged; the analyzer now unwraps `as`/cast conversions the same way the source generator does, so a mistaken base-member selector is caught regardless of the conversion syntax.
- **VELA005 no longer flags a nested path as a duplicate column.** It compared columns by the leaf member name, so `a => a.Name` and `a => a.Parent.Name` (a member of a related entity) were wrongly reported as duplicates. It now only treats a member access directly on the lambda parameter as a column.
- **VELA012 code fix qualifies the exception type with `global::`.** The fix inserted `Microsoft.Xrm.Sdk.InvalidPluginExecutionException`; it now uses the `global::`-qualified name so it cannot bind to a shadowing `Microsoft` namespace in user code.

### Performance

- **VELA001 runs its cheap syntactic gate before the semantic-model lookup**, skipping the conversion query on lambdas that are not column selectors.
- **VELA009 caches its per-type Handle-chain scan**, so a type with many `HasChanged` calls no longer re-scans all its step chains once per call (was quadratic per class).

### Notes

- VELA008 (duplicate-trigger) implicit-stage and VELA015 (handler-context) typed `On<TRequest>()` / permissive `OnMessage(...)` coverage are documented as known limitations in code; extending them is a follow-up (still open on #309).

## [0.19.6] - 2026-09-27 (Vela.Cli)

CLI line, published as `cli-v0.19.6`. Connection-string input validation and config-dir hardening (closes the CLI security audit, #307).

### Fixed

- **Client-secret connection inputs are validated before use.** `--url` must be an absolute `https` URL, and no value (`--url`/`--tenant-id`/`--client-id`/`--client-secret`) may contain `;`, which would inject or corrupt the `;`-delimited Dataverse connection string. (`=` is allowed, so base64 secret padding still works.) A bad input fails with a clear message that never echoes the secret.
- **`~/.vela` is created with owner-only (0700) permissions on every write.** `UserConfig.Write` (called after each command by the update-check) now goes through the same `EnsureConfigDir` hardening as the token cache, so the config directory can never be left world-traversable before a secret-bearing write.

### Docs

- The plugins guide now states explicitly that secrets belong in `WithSecureConfig` (redacted in output), never in `WithUnsecureConfig` (stored unencrypted and printed verbatim in `plan`/`inspect`).

## [0.19.5] - 2026-09-27 (Vela.Cli)

CLI line, published as `cli-v0.19.5`. Tightens plugin-type detection in the package scanner.

### Fixed

- **The package scanner no longer mistakes a type whose base name merely starts with the marker for a Vela plugin.** Plugin / Custom API discovery matched any type whose base type name *started with* `EntityPluginBase` / `CustomApiBase`, so a user's own `EntityPluginBaseHelper` (or similar) was treated as a plugin and instantiated during the scan. Detection now matches the base type's simple name exactly (ignoring generic arity, so `EntityPluginBase<T>` / `CustomApiBase<TReq,TResp>` still match).

## [0.19.4] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.19.4`. Detects a rank collision between a filter-less global step and entity-bound steps.

### Fixed

- **`plan`/`apply` now detect an execution-order collision between a filter-less "global" step and an entity-bound step on the same message.** A step registered with no message filter fires for every entity on its message, so it collides with entity-bound steps at the same stage/mode/rank. The environment read scoped itself to the touched entities (excluding filter-less steps), and the collision detector grouped by exact coordinate (so an entity-less step and an `account` step never met), so the clash was missed. The environment read now also fetches filter-less steps, and the detector projects each global step onto the touched entity coordinates it covers, so the collision is surfaced as a warning.

## [0.19.3] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.19.3`. Stops a mixed-case step declaration from churning as perpetually "changed".

### Fixed

- **A step whose entity or filtering/image attributes are declared in mixed case no longer diffs as "changed" on every `plan`/`apply`.** Dataverse stores entity and attribute logical names in lower case, so the live read is always lower case; a code declaration such as `"Account"` or `"Name"` diverged from it and never converged. The local normalization now canonicalizes the step's entity logical name, filtering attributes and image attributes to lower case (and the derived step name uses the lower-cased entity), matching the live read. Message-name casing and Custom API `uniqueName` casing are unchanged (their canonical form is not simply lower case).

## [0.19.2] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.19.2`. Stops a keyless step from churning as perpetually "changed".

### Fixed

- **A step registered without an explicit step key no longer diffs as "changed" on every `plan`/`apply`.** A keyless step's display name is `{plugin} - {lifecycle}: {message} of {entity}` and carries no key marker, so the live-read step-key resolver parsed the trailing `{message} of {entity}` as if it were an explicit step key. The code side has no key, so the two never matched and the step was reported (and re-written) on every run. The resolver now recognises the computed keyless name and resolves it to no key, so a keyless step converges. Steps with an explicit key (marker or legacy name) are unaffected.

## [0.19.1] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.19.1`. Generated-model correctness and a signing-secret warning from the follow-up audit.

### Fixed

- **Generated `[Range]` no longer throws or loses precision on `Money`/`decimal` columns.** `vela models --data-annotations` emitted `[Range(min, max)]` with `double` literals for every numeric column. On a `Money` property that throws `InvalidCastException` at validation time (RangeAttribute cannot convert `Microsoft.Xrm.Sdk.Money`), and on a `decimal` property it validates through `double` and loses precision. `Money` columns now emit no `[Range]`, `decimal` columns use the type-correct `[Range(typeof(decimal), "min", "max")]` overload, and `int`/`double` are unchanged.
- **Bound Custom API request parameter named `Target` no longer produces `CS0102`.** A bound Custom API auto-emits a `Target` property for the bound record; a request parameter also named `Target` duplicated that member and would not compile. The duplicate parameter is now skipped (it already maps to the same `Parameters["Target"]` slot) with an explanatory comment.

### Security

- **`vela sign --pfx-password` now warns that the password is passed on the command line.** `dotnet nuget sign` accepts the PFX password only as an argv argument, so on a shared host it is visible to other users via `ps`/proc for the child process lifetime. The command now warns and steers to `--fingerprint` (an installed certificate) or `--pfx-credential` (Windows Credential Manager), mirroring the `--client-secret` warning.

## [0.19.0] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.19.0`. Makes a mid-run `apply` failure observable to scripts and always surfaces a half-applied state.

### Added

- **Machine-readable result on a failed `apply`.** A mid-run failure (Dataverse has no cross-entity transaction, so some operations may have landed) now emits a JSON result with mode `apply-failed` for `--json`/`--out` callers, carrying the attempted plan summary and the failure message, so a scripted caller can detect a partial apply instead of only seeing stderr text.
- **Distinct exit code `7` (`ExitPartialApply`) for a mid-run `apply` failure**, so CI can tell "the org may be half-updated, re-run apply to reconcile" apart from a bad-args/strict failure (`4`) or a connection problem (`6`).

### Changed

- **A mid-run `apply` failure now exits `7` instead of `4`.** A script that specifically matched exit code `4` on an apply failure must now also accept `7` (see above). Pre-apply failures (bad args, connection, strict) are unchanged.

### Fixed

- **The per-owner half-applied report is always shown on a failed `apply`, not only under `--verbose`.** When a batched write faulted, the summary of which rows landed and which did not (and which were skipped) was gated on `--verbose`, so a default-verbosity apply failure hid the partial state. It now renders whenever anything failed or was skipped; a fully-successful apply stays silent.

## [0.18.0] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.18.0`. Step supported-deployment fidelity and a `models` robustness fix.

### Added

- **`plan`/`apply` now honour `.WithSupportedDeployment(...)`.** The step's `supporteddeployment` was hardcoded to `0` (ServerOnly) on write and never read, so the modifier was a silent no-op and drift went undetected. The read path now selects `supporteddeployment`, the diff shape carries it, and the upsert writes the declared value (0 ServerOnly, 1 OfflineOnly, 2 Both). A step that does not set it stays at 0, so nothing churns.

### Fixed

- **`vela models` no longer discards every model when one entity name is bad.** Entity metadata was read with a single `Select(...).ToArray()`, so one unreadable/typo'd `--entities` name (or an inaccessible table) threw and aborted the whole generation. Each entity is now read independently and a failure is skipped with a warning, matching the TypeScript table path and the message path.

## [0.28.0] - 2026-09-26 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.28.0`. Adds specific-user step impersonation.

### Added

- **`WithImpersonation(Guid systemUserId)` on the step builder.** A step can now run in the context of a specific Dataverse systemuser (the `sdkmessageprocessingstep.impersonatinguserid`), so the plugin's `IOrganizationService` acts as that user rather than the calling user. The value is carried on `StepDefinition.ImpersonatingUserId` and surfaced end to end by the CLI (see cli 0.17.0). Omitting it keeps the calling-user default; passing `Guid.Empty` is rejected at registration. The existing `WithImpersonation(ImpersonationSource)` enum overload is unchanged.

## [0.27.2] - 2026-09-26 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.27.2`. Analyzer accuracy fix from the follow-up audit.

### Fixed

- **VELA015 now validates the context's entity, not just its shape.** A handler bound with `.Handle(...)` whose parameter was the right message-context interface but for a *different* entity (for example `IUpdatePostContext<Contact>` on a plugin for `Account`) compiled through the permissive `Handle(Delegate)` overload and then failed the image cast at runtime. VELA015 now compares the context's generic entity argument against the plugin's entity and reports the mismatch at compile time.

## [0.27.1] - 2026-09-25 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.27.1`. Fixes two edge cases in the 0.27.0 typed-image/context generation found in a follow-up audit.

### Fixed

- **Generated typed step context no longer references a renamed projection on a handler-name collision.** When two steps in one plugin bound the same named handler with different captured columns, the projection types were hash-disambiguated but the generated context still referenced the original (now non-existent) name, producing `CS0246`. The context now references the emitted (hash-suffixed) projection name. (This shape is also a VELA016 error, so it was usually masked, but the generator runs regardless.)
- **VELA014 now flags a non-`partial` enclosing class of a nested plugin.** The generated projection nests inside the plugin class and every type that encloses it, all emitted as `partial class`; VELA014 (and its code fix) previously checked only the plugin class, so a non-`partial` outer class still failed the build with an obscure `CS0260`. VELA014 now reports (and the fix adds `partial` to) every class in the containment chain that lacks it.
- **Clear error when a step handler's declared context cannot be built.** If a handler parameter derives from a Vela step context but is abstract or lacks a public `(ILocalPluginContext)` constructor, the pipeline now throws an explicit `InvalidPluginExecutionException` naming the type, instead of silently building an incompatible base context and failing later with an opaque cast error.
- **`Handle(...)` validates the handler shape at registration.** A handler bound through the permissive delegate overload must be a void method taking exactly one context parameter; a wrong-arity or non-void delegate now fails at registration with a clear message instead of an `IndexOutOfRange`/`TargetParameterCount` crash mid-dispatch.
- **Circular service dependency detection.** `SimpleServiceResolver` now detects a factory that resolves its own type (directly or via a cycle) and throws a clear `InvalidPluginExecutionException` (with the resolution chain) instead of a `StackOverflowException` that cannot be caught and would tear down the worker.

## [0.17.0] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.17.0`. Wires step impersonation through plan/apply.

### Added

- **`plan`/`apply` now read, diff and write step impersonation (`impersonatinguserid`).** Previously `.WithImpersonation(...)` was dropped end to end: the CLI never read the column, so a hand-set impersonation read as "in sync" and a code-declared one was never written. The step read path now selects `impersonatinguserid`, the diff shape carries it (normalized to a canonical Guid string, empty = none), and the upsert writes it as a `systemuser` reference (clearing it on update when removed). Requires framework 0.28.0 for `WithImpersonation(Guid)`. The scaffold `--core-version` default moves to 0.28.0.

## [0.16.4] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.16.4`. Scaffold and generated-model warning fixes from the follow-up audit.

### Fixed

- **Generated models no longer warn `CS8669` in the consuming project.** `vela models` emits nullable reference annotations (a typed N:1 nav property, a `string?`/`EntityReference?`) into auto-generated files, which the compiler requires be inside an explicit `#nullable` context. Each generated model file now opens with `#nullable enable` (after the header, before the namespace), so the models compile warning-free under both net462 and net10 consuming projects.
- **Guard against a `vela init` scaffold regressing to `CS0260`.** Added a scaffold-template test asserting that every demo plugin which captures a typed image (a `WithPreImage`/`WithPostImage` column-selector) is declared `partial`, so the source generator's `partial` half (VELA014-016) always compiles. The demo plugins were already `partial`, so a freshly scaffolded solution builds clean on the latest framework; the test keeps it that way.
- **Scaffolded plugin project no longer emits `CS7035`.** The generated plugin `.csproj` uses a deliberate `1.0.*` wildcard assembly version (Dataverse caches plugin assemblies by version, so the wildcard auto-increments to bust the cache), which makes the compiler warn `CS7035` ("version string not in the recommended `major.minor.build.revision` format"). The project now suppresses `CS7035`, so a freshly scaffolded or `vela add`-ed plugin builds warning-free without losing the cache-busting behaviour.

## [0.16.3] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.16.3`. Two `apply` data-safety fixes from the follow-up audit.

### Fixed

- **`apply` no longer creates a duplicate step when its step key drifts.** When a step's stored key changed (for example an `executionOrder` change reseeds the framework auto key, or a pre-marker deployment carries no key), the live-step lookup found no key match and returned nothing, so `apply` created a second step and orphaned the original even though the diff correctly classified the change as an update. The lookup now falls back to the step's trigger identity (message, entity, stage, mode) when that match is unambiguous, updating the existing step in place. An ambiguous coordinate still returns no match, so `apply` never updates the wrong step.
- **`apply`'s resume report no longer over-reports success on a capture-phase abort.** Step and Custom API writes are captured and then flushed together at the end of each phase. If a synchronous validation error (an unresolved message, an immutable-field change, a missing filter) was thrown while capturing, the resume boundary had already advanced past earlier owners in the same batch, so the report listed them as completed even though nothing had been flushed. The boundary now advances only after a successful flush, so a mid-capture failure reports the batch as un-applied instead of misleading the operator in the dangerous direction.

## [0.16.2] - 2026-09-26 (Vela.Cli)

CLI line, published as `cli-v0.16.2`. Model-generation and secret-handling fixes from the follow-up audit.

### Fixed

- **`models` no longer emits a non-compiling step extension for a bound message whose entity is not generated.** A bound Custom API (or an SDK message applicable to specific entities) referenced the early-bound entity type in its generated `On<Message>()` extension, even when that entity was not part of the same generation batch (for example `vela models --custom-apis` alone, or an API bound to `contact` while only `account` was generated), producing `CS0246`. The generator now degrades to the unbound generic form (`On<Message><TEntity>() where TEntity : Entity`) for a Custom API and drops the out-of-batch entity overloads for an SDK message, so the output always compiles.
- **Client secret no longer leaks to stderr / CI logs on a connection failure.** A connection string embeds `ClientSecret=...`, and the SDK (or `client.LastError`) can echo it verbatim in an exception, which was printed as-is by the error path and, under `--verbose`, in the full exception dump. Failure output now scrubs `ClientSecret`/`Password`/`Secret` values to `***` as a defense-in-depth layer.

## [0.16.1] - 2026-09-25 (Vela.Cli)

CLI line, published as `cli-v0.16.1`. Data-safety fix from a follow-up audit.

### Fixed

- **`plan`/`apply` no longer risks deleting live registrations from a partial package.** When a plugin type failed to load during a package scan (a dependency missing from the package, or built for a different target framework), the scanner silently dropped it, so its existing steps/Custom APIs were diffed as `Removed` and `apply` would delete them. The scan now aborts with a clear error when any type fails to load, so nothing is deleted from a misbuilt package.

## [0.27.0] - 2026-09-25 (Vela.Messages + Vela.Plugins + Vela.Testing)

Framework line, published as `framework-v0.27.0`. Typed image ergonomics: short nested projection names and generated
typed step contexts.

### Changed

- **Typed image projections are now nested in the plugin's `partial` class with a short name (breaking).** The
  generated projection type is emitted as a nested type inside the plugin class and named
  `{Handler}_{Pre|Post}Image`, instead of `{PluginClass}_{Handler}_{Pre|Post}Image` in a separate
  `<Namespace>.Generated.Images` namespace exposed via a `global using`. Read it unqualified, for example
  `ctx.PostImageAs<OnUpdated_PostImage>()`. The lambda-handler fallback likewise drops the plugin prefix
  (`{Message}{Stage}_{Pre|Post}Image`), and the `Generated.Images` namespace and its `global using` are removed.
  **Migration:** drop the `{PluginClass}_` prefix at each `PreImageAs`/`PostImageAs` read site and declare the
  plugin class `partial` (see VELA014).

### Added

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

## [0.16.0] - 2026-09-25 (Vela.Cli)

CLI line, published as `cli-v0.16.0`. Scaffold updates for the typed-image ergonomics.

### Changed

- Scaffolded plugins that capture typed images are now generated `partial`, read images through the generated typed
  step context (`void OnAccountCreated(OnAccountCreatedContext ctx)` / `ctx.PostImage`), and lightweight Custom APIs
  bind their handler via a method group (`RegisterCustomApi("...", Handle)`) instead of `nameof(Handle)`.
- Scaffolds target framework **0.27.0** by default (offline-fallback `DefaultCoreVersion`); `vela init` still resolves
  the latest published framework from nuget.org.

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
  (`zava_AccountEstimateValue` -> `OnAccountEstimateValue`).
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
  SDK request stays reachable via `ctx.GetRequest<CreateRequest>()`. First step of the typed-requests design.
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
  `SendEmailFromTemplate`, whose `ctx.Request.Target` is the base SDK `Entity`). Part of the typed-requests design.
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
  [Custom API guide](guides/custom-apis.md) now show. Part of the typed-requests design.

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
