# Analyzers and generators

Vela ships a small set of Roslyn **analyzers** and one **source generator** inside the `Vela.Plugins` package
(they live in the bundled `Vela.Analyzers` assembly). There is **no separate analyzer package or project to
reference** - they come with `Vela.Plugins` automatically and run in the editor and at build with no extra
configuration. The rules that catch a genuine mistake are reported as **errors** so they fail fast instead of
surfacing as a silent runtime surprise; advisory rules (currently only VELA006) are **warnings** and never break a
build. Together they let the compiler enforce the fluent step builder's rules, so both a human developer and an AI
author get told what is valid up front.

| Rule | Category | Severity | What it catches |
| --- | --- | --- | --- |
| [VELA001](#vela001) | Vela.Usage | Error | A column selector targets a base `Entity` member, not a real Dataverse column |
| [VELA002](#vela002) | Vela.Usage | Error | A `RegisterStep()` chain never ends with `.Handle(...)` |
| [VELA003](#vela003) | Vela.Usage | Error | A single-value step modifier is set more than once |
| [VELA004](#vela004) | Vela.Usage | Error | A step handler is a `static` method instead of an instance method |
| [VELA005](#vela005) | Vela.Usage | Error | The same column is listed twice in one `FilterOn` / `WithPreImage` / `WithPostImage` |
| [VELA006](#vela006) | Vela.Usage | Warning | Prefer the typed trigger (`OnCreate()`) over `OnMessage("create")` for a modelled message |
| [VELA007](#vela007) | Vela.Usage | Error | A pre-image on Create, or a post-image on Delete (the message can never provide it) |
| [VELA008](#vela008) | Vela.Usage | Error | Two steps in one plugin share the same trigger (message + stage + mode) |
| [VELA009](#vela009) | Vela.Usage | Error | `HasChanged`/`HasAnyChanged` in a handler whose step registers no pre-image |
| [VELA010](#vela010) | Vela.Usage | Error | A generated relationship navigation property used in a column selector |
| [VELA011](#vela011) | Vela.Reliability | Warning | A writable field (mutable state) on a plugin type |
| [VELA012](#vela012) | Vela.Usage | Warning | Throwing an exception other than `InvalidPluginExecutionException` from a plugin |
| [VELA013](#vela013) | Vela.Performance | Warning | `new ColumnSet(true)` retrieves every column |

Several rules ship an IDE **code fix** (the editor lightbulb applies the correction): VELA003, VELA004, VELA005,
VELA006, VELA007 and VELA012. The code fixes are in `Vela.CodeFixes`, bundled in `Vela.Plugins` alongside the
analyzers.

Plus the [typed image projection generator](#typed-image-projection-generator) that ships in the same assembly.

## VELA001

**A column selector must target a real column, not a base `Entity` member.** The column selectors used by
`FilterOn(...)`, the image selectors in `WithPreImage(...)` / `WithPostImage(...)`, and the typed query / change
helpers accept an early-bound column, e.g. `a => a.Name`. Selecting a member of the base `Microsoft.Xrm.Sdk.Entity`
type instead (such as `a => a.Id` or `a => a.LogicalName`) does not name a Dataverse column and is flagged.

**Fix:** select a generated column of your entity (`a => a.Name`), not an `Entity` member.

## VELA002

**A `RegisterStep()` chain must end with `.Handle(...)`.** The redesigned builder puts `Handle` last (it returns
`void`), so a step that stops earlier never binds a handler and would have nothing to run. Any discarded
`RegisterStep()....;` statement whose value is still a fluent builder is flagged.

**Fix:** end the chain with `.Handle(handler)`. (Chains captured in a local to be completed later are not flagged.)

## VELA003

**Set each single-value modifier at most once per step.** `FilterOn(...)` and the `With...` setters
(`WithExecutionOrder`, `WithDescription`, `WithStepKey`, `WithImpersonation`, `WithSupportedDeployment`,
`WithUnsecureConfig`, `WithSecureConfig`) overwrite the earlier value when called twice, silently dropping it, so a
second call is flagged. `Disabled()` is exempt (it is an idempotent flag toggle). The runtime builder throws on the
second call as well, so a chain split across locals is still caught at registration.

**Fix:** call the modifier once (combine the arguments if needed, for example a single `FilterOn(a => a.A, a => a.B)`).

## VELA004

**A step handler must be an instance method, not a `static` method.** The handler passed to `.Handle(...)` runs
against the plugin instance, so it can reach its state: services resolved from the lightweight DI, the step's
unsecure/secure config, and any other instance member. A `static` method group cannot, and does not fit the
framework's instance-bound handler dispatch, so `.Handle(SomeStaticMethod)` is flagged. Instance method groups
(`.Handle(OnCreate)`) and lambdas (`.Handle(ctx => ...)`) are fine.

**Fix:** remove `static` from the handler method, or use a lambda.

## VELA005

**Do not list the same column twice in one selector list.** `FilterOn(...)`, `WithPreImage(...)`, and
`WithPostImage(...)` take a list of column selectors; repeating a column (for example
`FilterOn(a => a.Name, a => a.Name)`) has no effect and is almost always a copy/paste slip. Only lambda column
selectors are compared, so a leading image alias string is ignored, and the same column in two different calls
(one in `FilterOn`, one in `WithPreImage`) is fine.

**Fix:** list each column once.

## VELA006

**Prefer the typed step trigger over the permissive `OnMessage(string)` for a message Vela models.** For a modelled
message there is a typed trigger - `OnCreate()`, `OnUpdate()`, `OnDelete()`, the `*Multiple` variants,
`OnRetrieve()` / `OnRetrieveMultiple()`, `OnAssociate()` / `OnDisassociate()`, `OnMerge()` - that gives a typed
handler context and compile-time entity applicability, so `OnMessage("create")` is flagged. Unlike the other
rules this is a **warning**, not an error: `OnMessage("...")` stays valid and is the right tool for a message Vela
does not model (only a literal message string with a modelled name is flagged).

**Fix:** switch to the typed trigger, for example `OnCreate()` instead of `OnMessage("create")`.

## VELA007

**Do not register an image the message can never provide.** A **pre-image on a Create** step (there is no prior row
state before a create) and a **post-image on a Delete** step (the row no longer exists afterwards) are impossible,
and the runtime rejects them. The typed triggers already prevent this through the builder's type-state, so this
targets the permissive `OnMessage("create"|"delete")` path where `WithPreImage(...)` / `WithPostImage(...)` are
exposed unconditionally. The message is read from the chain; a message Vela does not model is left alone.

**Fix:** use the image the message supports - a post-image for Create, a pre-image for Delete - or drop the image.

## VELA008

**Register at most one step per trigger in a plugin.** Dataverse allows a single step per trigger - message + stage
+ mode + entity - and the runtime throws at registration when a plugin defines two. Two `RegisterStep()` chains in
the same `RegisterPluginSteps()` with the same message, stage, and mode are flagged (the entity is fixed per
plugin). Only complete chains whose message and stage are statically determinable are compared.

**Fix:** merge the two steps into one (combine their filtering attributes), or move one to a different stage/mode.

## VELA009

**`HasChanged` / `HasAnyChanged` need a registered pre-image.** They diff the `Target` against the step's pre-image
and throw at runtime when none is registered. This is flagged only in the unambiguous case: the handler is an
instance method bound to exactly one `RegisterStep()` chain in the same class, that chain has no `WithPreImage(...)`,
and the call uses the default pre-image alias. Lambda handlers, handlers shared across steps, and custom aliases are
left alone to avoid false positives.

**Fix:** add `WithPreImage(...)` (the columns you compare) to the step, or remove the change check.

## VELA010

**A relationship is not a column.** The generated early-bound classes expose relationship navigation properties
(a single typed parent for N:1, a typed collection for 1:N) carrying `[RelationshipSchemaName(...)]`. Passing one to
a column selector (`FilterOn`, `Select`, `Where`, `OrderBy`, `WithPreImage`, `WithPostImage`) is a mistake: a
relationship has no storable column, and at runtime the resolver rejects it (without this rule it would silently
build a bogus column from the property name). Only Vela's own selectors are checked, so ordinary LINQ
`Select`/`Where` is never flagged.

**Fix:** select the lookup foreign-key column instead (for example `a.PrimaryContactId`, not `a.PrimaryContact`),
or load related records with a deep insert / relationship-aware read rather than a column selector.

## VELA011

**Mutable plugin state.** A writable field on a plugin / Custom API type is shared state: the Dataverse sandbox
caches and reuses plugin instances across executions and calls them concurrently, so a mutable field leaks data
between unrelated operations and races. Only writable fields are flagged; `const`, `readonly` and `static readonly`
(immutable configuration) are fine.

**Fix:** keep per-execution state in locals or on the execution context; make constants `const` / `readonly`.

## VELA012

**Throw `InvalidPluginExecutionException`.** Only `InvalidPluginExecutionException` surfaces its message to the
caller (and the UI); any other exception thrown from a plugin is reported as a generic, unhelpful error. Rethrows
(`throw;` / `throw ex;`) are left alone. A code fix changes the thrown type, keeping the arguments.

**Fix:** `throw new InvalidPluginExecutionException("...")` for user-facing failures.

## VELA013

**Retrieving all columns.** `new ColumnSet(true)` fetches every column, moving far more data than a plugin needs.
Request only the columns you use (the explicit `SelectAll()` on a typed query states the intent and is left alone).

**Fix:** pass the specific columns to `ColumnSet`, or use the typed `Select(...)`.

## Typed image projection generator

The same bundled `Vela.Analyzers` assembly also contains an incremental **source generator**
(`ImageProjectionGenerator`) - so, like the analyzers, it ships with `Vela.Plugins` and needs no separate package
or project. For every step that captures explicit columns in `WithPreImage(a => a.X, ...)` /
`WithPostImage(...)`, it emits a per-step projection type holding **only** those columns, which you read with
`ctx.PreImageAs<T>()` / `ctx.PostImageAs<T>()`. Reading a column the step did not capture is then a compile
error instead of a silent `null`. See [Typed images](../guides/plugins.md#typed-images-compile-safe-projections)
in the plugin guide for the full usage.

Key points:

- **Compile-time, regenerated every build.** The projections are produced in memory from the capture lambdas
  against your current early-bound model (they are not files in your source tree; `F12` opens them). If a
  captured column is removed or renamed in the early-bound model (for example after re-running `vela models`),
  the `WithPreImage`/`WithPostImage` selector and the handler that reads the projection stop compiling - the
  drift surfaces as a build error, never a stale value.
- **Naming.** A projection is named `{PluginClass}_{Handler}_{Pre|Post}Image` (plus the alias when one is given),
  and a `global using` for `<YourNamespace>.Generated.Images` is emitted on C# 10+ so the type is offered by
  completion without an explicit `using`. A lambda handler (no name) falls back to
  `{PluginClass}_{Message}{Stage}_{Pre|Post}Image`, where the message comes from the trigger: the guided
  `OnCreate()`..., the typed `On<TRequest>()`, or the permissive `OnMessage("...")` / `OnMessage<TMarker>()`
  (a literal message is capitalized into the segment; a non-literal message is not statically known, so the
  step is skipped).
- **Opt-in and additive.** A capture with no explicit columns (`WithPreImage()`) keeps the full early-bound
  entity and generates nothing; if the column list is not a literal set of selectors, the generator skips it and
  you keep the untyped image.
