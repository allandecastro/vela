# Analyzers and generators

Vela ships a small set of Roslyn **analyzers** and one **source generator** inside the `Vela.Plugins` package
(they live in the bundled `Vela.Analyzers` assembly). There is **no separate analyzer package or project to
reference** - they come with `Vela.Plugins` automatically and run in the editor and at build with no extra
configuration. Rules that catch a build-breaking mistake in the fluent step builder are reported as **errors** so
they fail fast instead of surfacing as a silent runtime surprise; advisory and best-practice rules (VELA011 reliability, VELA012 usage, VELA013 performance, VELA018 and VELA019 reliability) are **warnings** and never break a build. Together they
let the compiler enforce the framework's rules, so both a human developer and an AI author get told what is valid up
front. Several rules also ship an IDE **code fix** (see below).

| Rule | Category | Severity | What it catches |
| --- | --- | --- | --- |
| [VELA001](#vela001) | Vela.Usage | Error | A column selector targets a base `Entity` member, not a real Dataverse column |
| [VELA002](#vela002) | Vela.Usage | Error | A `RegisterStep()` chain never ends with `.Handle(...)` |
| [VELA003](#vela003) | Vela.Usage | Error | A single-value step modifier is set more than once |
| [VELA004](#vela004) | Vela.Usage | Error | A step handler is a `static` method instead of an instance method |
| [VELA005](#vela005) | Vela.Usage | Error | The same column is listed twice in one `FilterOn` / `WithPreImage` / `WithPostImage` |
| [VELA007](#vela007) | Vela.Usage | Error | A pre-image on Create, or a post-image on Delete (the message can never provide it) |
| [VELA008](#vela008) | Vela.Usage | Error | Two steps in one plugin share the same trigger (message + stage + mode) |
| [VELA009](#vela009) | Vela.Usage | Error | `HasChanged`/`HasAnyChanged` in a handler whose step registers no pre-image |
| [VELA010](#vela010) | Vela.Usage | Error | A generated relationship navigation property used in a column selector |
| [VELA011](#vela011) | Vela.Reliability | Warning | A writable field (mutable state) on a plugin type |
| [VELA012](#vela012) | Vela.Usage | Warning | Throwing an exception other than `InvalidPluginExecutionException` from a plugin |
| [VELA013](#vela013) | Vela.Performance | Warning | `new ColumnSet(true)` retrieves every column |
| [VELA014](#vela014) | Vela.Usage | Error | A plugin with a generated context or typed image must be declared `partial` |
| [VELA015](#vela015) | Vela.Usage | Error | A step handler's context does not match the step's message/stage |
| [VELA016](#vela016) | Vela.Usage | Error | A typed-context handler is bound on more than one image-capturing step |
| [VELA017](#vela017) | Vela.Usage | Error | A handler reads `ctx.PreImage`/`ctx.PostImage` its step did not capture |
| [VELA018](#vela018) | Vela.Reliability | Warning | An asynchronous step's handler writes to the Target (the change is lost) |
| [VELA019](#vela019) | Vela.Reliability | Warning | A plugin accesses the filesystem (`System.IO.File` / `FileStream` / ...), which the sandbox denies |
| [VELA020](#vela020) | Vela.Usage | Error | A Create/Update (or bulk) step reaches `Handle(...)` without `FilterOn(...)` |
| [VELA021](#vela021) | Vela.Usage | Warning | An image capture lists columns but no typed projection can be generated for it |
| [VELA022](#vela022) | Vela.Reliability | Warning | A step on the `SetState` message never fires |

Several rules ship an IDE **code fix** (the editor lightbulb applies the correction): VELA003, VELA004, VELA005,
VELA007, VELA012, VELA014 and VELA015. The code fixes are in `Vela.CodeFixes`, bundled in `Vela.Plugins`
alongside the analyzers.

Plus the [typed image projection generator](#typed-image-projection-generator) that ships in the same assembly.

## VELA001

**A column selector must target a real column, not a base `Entity` member.** The column selectors used by
`FilterOn(...)`, the image selectors in `WithPreImage(...)` / `WithPostImage(...)`, and the typed query / change
helpers accept an early-bound column, e.g. `a => a.Name`. Selecting a member of the base `Microsoft.Xrm.Sdk.Entity`
type instead (such as `a => a.LogicalName`, or an `Id` override with no `[AttributeLogicalName]`) does not name a
Dataverse column and is flagged. So is a **nested path** such as `a => a.PrimaryContactId.Id` or
`a => a.ParentAccount.Name`: it reads another object's member, not a column of this entity (select the lookup
column itself, `a => a.PrimaryContactId`). The runtime rejects both too, instead of registering a wrong column.

**Fix:** select a generated column of your entity (`a => a.Name`), not an `Entity` member.

## VELA002

**A `RegisterStep()` chain must end with `.Handle(...)`.** The redesigned builder puts `Handle` last (it returns
`void`), so a step that stops earlier never binds a handler and would have nothing to run. Any discarded
`RegisterStep()....;` statement, or expression body of a void member (`RegisterPluginSteps() => RegisterStep()...;`),
whose value is still a fluent builder is flagged.

**Fix:** end the chain with `.Handle(handler)`. (Chains captured in a local to be completed later are not flagged.)

## VELA003

**Set each single-value modifier at most once per step.** `FilterOn(...)` and the `With...` setters
(`WithExecutionOrder`, `WithDescription`, `WithStepKey`, `WithImpersonation`, `WithSupportedDeployment`,
`WithUnsecureConfig`, `WithSecureConfigFrom`) overwrite the earlier value when called twice, silently dropping it, so a
second call is flagged. `Disabled()` is exempt (it is an idempotent flag toggle). The runtime builder throws on the
second call as well, so a chain split across locals is still caught at registration.

**Fix:** call the modifier once (combine the arguments if needed, for example a single `FilterOn(a => a.A, a => a.B)`).
The code fix removes a duplicate `With...` call and, for a duplicate `FilterOn(...)`, moves its columns into the
first `FilterOn(...)` so no filtering attribute is lost.

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

**Retired.** VELA006 flagged `OnMessage("create")` for a message Vela models. The string trigger it targeted no
longer exists: every message is named by a type (a curated `OnCreate()`, a request `On<XxxRequest>()`, or a marker
`OnMessage<TMarker>()`), so there is nothing left to flag. The id is not reused.

## VELA007

**Do not register an image the message can never provide.** A **pre-image on a Create** step (there is no prior row
state before a create) and a **post-image on a Delete** step (the row no longer exists afterwards) are impossible,
and the runtime rejects them. The typed triggers already prevent this through the builder's type-state, so this
targets the type-stated `On<CreateRequest>()` / `OnMessage<TMarker>()` paths where `WithPreImage(...)` /
`WithPostImage(...)` are exposed unconditionally. The message is read from the type: a `[SdkMessage("...")]` or
`[CustomMessage("...")]` on it, or an SDK request type (`Microsoft.Xrm.Sdk.Messages.CreateRequest` is `Create`). A
message Vela does not model, or a type whose message is only set in its constructor, is left alone.

**Fix:** use the image the message supports - a post-image for Create, a pre-image for Delete - or drop the image.

## VELA008

**Register at most one step per trigger in a plugin.** Dataverse allows a single step per trigger - message + stage
+ mode + entity - and the runtime throws at registration when a plugin defines two. Two `RegisterStep()` chains in
the same `RegisterPluginSteps()` with the same message, stage, and mode are flagged (the entity is fixed per
plugin). Only complete chains whose message and stage are statically determinable are compared.

**Different filtering attributes do not make the steps distinct.** The trigger is message + stage + mode + entity;
filtering attributes are not part of it. At runtime the framework resolves a *single* step for the executing
message, and it never re-evaluates filtering attributes itself - Dataverse applies the step's registered filter
upstream, before the plugin runs. So two steps that differ only by `FilterOn(...)` are indistinguishable once the
plugin executes (Dataverse does not tell the plugin which step row fired), their auto step-keys collide, and the
runtime throws `registers more than one step for the same trigger`. Splitting the logic across two steps is not a
way to react to different column sets.

**Fix:** keep one step with the **union** of the filtering attributes, then branch inside the handler on what
actually changed:

```csharp
RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.CreditLimit, a => a.Name)   // union of both triggers' columns
    .WithPreImage(a => a.CreditLimit, a => a.Name)
    .Handle(ctx =>
    {
        if (ctx.HasChanged<Account>(a => a.CreditLimit)) { /* credit path */ }
        if (ctx.HasChanged<Account>(a => a.Name)) { /* rename path */ }
    });
```

If the two really are unrelated, move one to a different stage/mode, or put it in a separate plugin class.

## VELA009

**`HasChanged` / `HasAnyChanged` need a registered pre-image.** They diff the `Target` against the step's pre-image
and throw at runtime when none is registered. This is flagged only in the unambiguous case: the handler is an
instance method bound to exactly one `RegisterStep()` chain in the same class, that chain has no `WithPreImage(...)`,
and the call uses the default pre-image alias. Lambda handlers, handlers shared across steps, and custom aliases are
left alone to avoid false positives. When that chain's `WithPreImage(...)` lists its columns as selectors, a
compared column missing from the list is flagged as well: its previous value reads as `null`, so it looks changed
whenever the Target carries it.

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
(immutable configuration) are fine. Plugin types are recognised by symbol (`Microsoft.Xrm.Sdk.IPlugin`,
`Vela.Plugin.PluginBase`, `EntityPluginBase<T>`, `Vela.CustomApi.CustomApiBase`), never by a look-alike name.

**Fix:** keep per-execution state in locals or on the execution context; make constants `const` / `readonly`.

## VELA012

**Throw `InvalidPluginExecutionException`.** Only `InvalidPluginExecutionException` surfaces its message to the
caller (and the UI); any other exception thrown from a plugin is reported as a generic, unhelpful error. Rethrows
(`throw;` / `throw ex;`) are left alone. A code fix changes the thrown type, keeping the arguments.

`PluginBusinessException` is **not** flagged inside a plugin that derives from a Vela base (`PluginBase` /
`EntityPluginBase` / `CustomApiBase`): that base catches it and surfaces its message to the caller as a clean
`InvalidPluginExecutionException` (with a dedicated `business-exception` trace snapshot). It cannot derive from
`InvalidPluginExecutionException` because the SDK seals that type, so the analyzer recognizes it by name. Use it
when you want business semantics (and a localizable message); use `InvalidPluginExecutionException` directly for a
plain user-facing failure. Both are clean. In a type that implements `IPlugin` **directly** there is no Vela base
frame to perform that mapping, so `PluginBusinessException` is still flagged there - throw
`InvalidPluginExecutionException` from a raw `IPlugin`.

This rule checks throws inside **plugin / Custom API types** (those implementing `IPlugin` or deriving from the
Vela bases). A throw in a plain helper or injected service class is not analyzed; when that code must raise a
user-facing failure, throw `InvalidPluginExecutionException` there - it surfaces cleanly to the caller whatever
the entry point is. `PluginBusinessException` surfaces cleanly too, but only when the plugin that ran the code
derives from a Vela base (that base's catch does the mapping), so reserve it for that case.

**Fix:** `throw new InvalidPluginExecutionException("...")` (or `throw new PluginBusinessException("...")`) for
user-facing failures.

## VELA013

**Retrieving all columns.** `new ColumnSet(true)` fetches every column, moving far more data than a plugin needs.
Request only the columns you use (the explicit `SelectAll()` on a typed query states the intent and is left alone).

**Fix:** pass the specific columns to `ColumnSet`, or use the typed `Select(...)`.

## VELA014

**Plugin must be `partial`.** The source generator emits types **nested inside the plugin's own class** - a
`{Handler}Context` for every named handler on a modelled message (any message, any stage), and a
projection for every explicit image capture (`WithPreImage(a => a.X, ...)` / `WithPostImage(...)`). A nested type needs a second
`partial` declaration of the class (and of every type that encloses it, whether a class, struct or record), so the
rule reports a missing `partial` up front instead of leaving the build to fail with an obscure `CS0260`. The rule asks
the generator itself whether it emits a nested type, so a capture the generator cannot project (see
[VELA021](#vela021)) does not require `partial`.

**Fix:** add the `partial` modifier to the plugin class (the code fix does this). `vela init` / `vela add` scaffold
plugins `partial` already. A plugin whose only handlers are lambdas (no generated context) and that captures no
explicit image generates nothing and does not require `partial`.

## VELA015

**Step handler context must match the step.** A handler bound with `.Handle(...)` must take the step's message
context (for example `IUpdatePostContext<T>`), its generated typed step context (a subclass of it), or the plain
`ILocalPluginContext`. The `Handle(Delegate)` overload (which lets a method group bind the generated context via its
natural type) is permissive at compile time, so this restores the guarantee the specific
`Handle(Action<IXxxContext>)` overload gave: a context for a different message or stage (or an unrelated type) is
reported. Scoped to the guided `Create` / `Update` / `Delete` path. On every path, a handler that does not take
exactly one parameter or does not return `void` is reported too: `Handle(Delegate)` compiles it, and the step builder
rejects it at registration.

**Fix:** give the handler the step's context (or its generated `{Handler}Context`); the code fix corrects the type
(written as short as the file's `using`s allow).

## VELA016

**Typed-context handler must bind a single step.** The generated typed step context is named after the handler
(`{Handler}Context`), so binding one handler on two image-capturing steps would need two contexts under the same
name. Use a distinct handler per image-capturing step (or read images via `ctx.PostImageAs<T>()` on the plain
context if you must share a handler). Only a class's own steps are compared: two nested plugins in one container
class may each have a handler with the same name.

## VELA017

**Image read but not captured by the step.** A post-operation context exposes both `PreImage` and `PostImage`
(a pre context exposes `PreImage`), so reading an image the step did not capture compiles but returns `null` at
runtime - a silent footgun. The rule fires only in the unambiguous case: the handler is an instance method bound
by exactly one `RegisterStep()` chain in the same class, and that chain has no matching `WithPreImage(...)` /
`WithPostImage(...)`. Add the capture to the step, or read the image it does capture.

## VELA018

**Write to the Target in an asynchronous step.** An asynchronous step runs after the main operation has committed,
in its own execution, so mutating the Target (`ctx.Request.Target` / `ctx.GetTarget<T>()`) there has no effect: the
change is neither written back to the database nor seen by any later step. Call `ctx.Service.Update(...)` with
the change, or move the logic to a synchronous step. Like VELA017 it fires only in the unambiguous case: the handler
is an instance method bound by exactly one `RegisterStep()` chain and that chain is `.Async()`; a handler shared
across steps, or a write through a local copy of the Target, is left alone. See
[which stage persists a Target write](../guides/plugins.md#writing-the-target-which-stage-persists).

## VELA019

**Filesystem access in a plugin.** The Dataverse sandbox denies file I/O, so `System.IO.File` / `Directory` calls and
`new FileStream` / `FileInfo` / `DirectoryInfo` / `DriveInfo` / `FileSystemWatcher` throw at runtime inside a plugin
or Custom API. This is a type-identity check, so it is precise: in-memory types (`MemoryStream`, `StringReader` /
`StringWriter`, `BinaryReader` / `BinaryWriter`) and the pure-string `System.IO.Path` helpers (`Path.Combine`,
`Path.GetExtension`, ...) are **not** flagged, and reading an embedded resource with
`Assembly.GetManifestResourceStream(...)` (a `Stream`, not a filesystem type) is fine. Like the other
plugin-authoring rules it is scoped to plugin / Custom API types, so filesystem use in an unrelated helper class is
not analyzed.

**Fix:** keep data in memory, read embedded resources via `Assembly.GetManifestResourceStream`, or move file work
out of the sandbox. See [Respect the sandbox](../guides/plugins.md#respect-the-sandbox).

## VELA020

A `Create`, `Update`, `CreateMultiple` or `UpdateMultiple` step must declare at least one filtering attribute, or
building the step fails at runtime (the step would otherwise fire on every create/update). VELA020 reports a
`RegisterStep()` chain that reaches `.Handle(...)` without `.FilterOn(...)`, so the mistake fails in the editor and
at build instead. Add `.FilterOn(e => e.Column)` before `.Handle(...)`. It also reports `On<UpdateRequest>()` (and
the other three messages, through a request or marker type, as VELA007 reads it) reaching `.Handle(...)`: the
type-stated triggers offer no `FilterOn`, so use the typed `OnCreate()` / `OnUpdate()` / ... trigger instead. A chain split across a local variable is not analyzed (it may
have been filtered before the split).

## VELA021

**Typed image projection not generated.** Reported by the generator (a warning, on the `WithPreImage` /
`WithPostImage` call) when a capture lists columns but no projection can be emitted for it: the image alias is not a
compile-time constant, a selector does not read a column property of the entity (`a => a.GetHashCode()`), or the
trigger of a chain that starts at `RegisterStep()` cannot be read from the chain. Without it the only symptom
is a `CS0246` on the missing `{Handler}_{Pre|Post}Image` type. Nested paths, `Entity` members and relationships are
already errors of VELA001 / VELA010 and are not reported again; a chain split across locals is not reported.

**Fix:** use a constant alias and column selectors, or read the full image with `ctx.GetPreImage<T>()` /
`ctx.GetPostImage<T>()`.

## VELA022

**A step on `SetState` never fires.** Reported on `On<T>()` / `OnMessage<T>()` when `T` pins the message with
`[SdkMessage("SetState")]`. `On<SetStateRequest>()` and a `[CustomMessage("SetState")]` marker are not reported: the
builder registers them on `SetStateDynamicEntity`. Dataverse accepts a `SetState` registration, but never runs the step: a `SetStateRequest` fires the `SetStateDynamicEntity`
steps, and an Update of `statecode` fires the Update steps plus nested `SetStateDynamicEntity` steps (verified live
on every stage). The step's logic would silently never execute.

**Fix:** register on `OnUpdate().FilterOn(x => x.StateCode)` (fires for both a SetState and an Update of the state),
or drop the `[SdkMessage("SetState")]` and use `On<SetStateRequest>()`, which registers on `SetStateDynamicEntity`
(inputs `EntityMoniker`, `State`, `Status`).

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
- **Naming and nesting.** A projection is emitted as a **nested type inside the plugin's own `partial` class**,
  named `{Handler}_{Pre|Post}Image` (plus the alias when one is given). Because it is scoped to the plugin class it
  needs no plugin-name prefix and no `using`: the handler reads it unqualified
  (`ctx.PostImageAs<OnUpdated_PostImage>()`). This is why a plugin that captures a typed image must be declared
  `partial` ([VELA014](#vela014)). A lambda handler (no name) falls back to `{Message}{Stage}_{Pre|Post}Image`,
  where the message comes from the trigger: the guided `OnCreate()`..., the typed `On<TRequest>()`, or the
  permissive `OnMessage<TMarker>()` (the request or marker type name, minus a `Request` suffix).
- **Opt-in and additive.** A capture with no explicit columns (`WithPreImage()`) keeps the full early-bound
  entity and generates nothing; if the column list is not a literal set of selectors, the generator skips it, you
  keep the untyped image, and [VELA021](#vela021) says why. A column property without `[AttributeLogicalName]` is
  projected under its lower-cased name, as the runtime resolves it.
- **Any containing type.** The generated part repeats each containing type's kind and type parameters, so a generic
  plugin or a plugin nested in a `struct` / `record` works (each must be `partial`).
- **Explicit widening to the entity.** Each projection also carries an **explicit** cast to its entity
  (`(Account)ctx.PreImage`) for the case where a service needs the whole image. It is lossless (the projection
  retains the source image, so `Id`, logical name and every attribute survive) and equivalent to
  `ctx.GetPreImage<Account>()` / `ctx.GetPostImage<Account>()` (both go through `Entity.ToEntity`); an absent image
  casts to `null`. It is deliberately not an implicit conversion: leaving the captured-columns view is a visible
  step, so the compile-safety does not disappear silently. See
  [Need the whole entity](../guides/plugins.md#need-the-whole-entity-for-a-service).
- **Typed step context.** For a named handler on a Create/Update/Delete step that captures an image, the generator
  also emits a `{Handler}Context` (a subclass of the step's message context) exposing `ctx.PreImage` / `ctx.PostImage`
  typed to the captured columns, so the handler can read them as members instead of `ctx.PreImageAs<T>()`. See
  [Typed step context](../guides/plugins.md#typed-step-context-read-images-as-ctxpreimage--ctxpostimage) and the
  VELA015 / VELA016 guard rails.
