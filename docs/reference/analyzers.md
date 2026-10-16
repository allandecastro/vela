# Analyzers and generators

Vela ships 23 Roslyn **analyzers** (VELA001 to VELA024; VELA006 was removed), one **source generator** and a set of IDE **code fixes**
inside the `Vela.Plugins` package (assemblies `Vela.Analyzers` and `Vela.CodeFixes`). There is nothing else to
reference: they run in the editor and at build with no configuration. A mistake that would fail at registration
or silently do nothing is an **error**; advisory rules (VELA011, VELA012, VELA013, VELA018, VELA019,
VELA021, VELA022 and VELA024) are **warnings**.

| Rule | Category | Severity | What it catches |
| --- | --- | --- | --- |
| [VELA001](#vela001) | Vela.Usage | Error | A column selector targets a base `Entity` member, not a real Dataverse column |
| [VELA002](#vela002) | Vela.Usage | Error | A `RegisterStep()` chain never ends with `.Handle(...)` |
| [VELA003](#vela003) | Vela.Usage | Error | A single-value step modifier is set more than once |
| [VELA004](#vela004) | Vela.Usage | Error | A step handler is a `static` method instead of an instance method |
| [VELA005](#vela005) | Vela.Usage | Error | The same column is listed twice in one `FilterOn` / `WithPreImage` / `WithPostImage` |
| [VELA007](#vela007) | Vela.Usage | Error | An image the message can never provide (pre-image on Create/CreateMultiple, post-image on Delete, any image on RouteTo) |
| [VELA008](#vela008) | Vela.Usage | Error | Two steps in one plugin share a trigger and nothing tells them apart (same filters and images, no step key) |
| [VELA009](#vela009) | Vela.Usage | Error | `HasChanged`/`HasAnyChanged` in a handler whose step registers no pre-image |
| [VELA010](#vela010) | Vela.Usage | Error | A generated relationship navigation property used in a column selector |
| [VELA011](#vela011) | Vela.Reliability | Warning | Mutable state on a plugin type: a writable field or auto-property, a `readonly` mutable collection, a captured primary-constructor parameter |
| [VELA012](#vela012) | Vela.Usage | Warning | Throwing an exception other than `InvalidPluginExecutionException` from a plugin |
| [VELA013](#vela013) | Vela.Performance | Warning | `new ColumnSet(true)` in a plugin type retrieves every column |
| [VELA014](#vela014) | Vela.Usage | Error | A plugin with a generated context or typed image must be declared `partial` |
| [VELA015](#vela015) | Vela.Usage | Error | A step handler's context does not match the step's message/stage |
| [VELA016](#vela016) | Vela.Usage | Error | A typed-context handler is bound on more than one image-capturing step |
| [VELA017](#vela017) | Vela.Usage | Error | A handler reads `ctx.PreImage`/`ctx.PostImage` its step did not capture |
| [VELA018](#vela018) | Vela.Reliability | Warning | An asynchronous step's handler writes to the Target (the change is lost) |
| [VELA019](#vela019) | Vela.Reliability | Warning | A plugin accesses the filesystem (`System.IO.File` / `FileStream` / ...), which the sandbox denies |
| [VELA020](#vela020) | Vela.Usage | Error | A Create/Update (or bulk) step reaches `Handle(...)` without `FilterOn(...)` |
| [VELA021](#vela021) | Vela.Usage | Warning | An image capture lists columns but no typed projection can be generated for it |
| [VELA022](#vela022) | Vela.Reliability | Warning | A step on the `SetState` message never fires |
| [VELA023](#vela023) | Vela.Usage | Error | A message with a typed trigger is registered through it, not `On<UpdateRequest>()` |
| [VELA024](#vela024) | Vela.Reliability | Warning | A plugin runs work in parallel (`Task.Run`, `Parallel.*`, `new Thread`, ...) or blocks on a task (`.Result`, `.Wait()`) |

Code fixes (editor lightbulb): VELA003, VELA004, VELA005, VELA007, VELA012, VELA014, VELA015 and VELA023. The
[typed image projection generator](#typed-image-projection-generator) ships in the same assembly as the analyzers.

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

**Retired.** VELA006 flagged `OnMessage("create")` for a message with a typed trigger. The string trigger no longer
exists: every message is named by a type, and [VELA023](#vela023) covers a request or marker type whose message has a
typed trigger. The id is not reused.

## VELA007

**Do not register an image the message can never provide.** A **pre-image on Create or CreateMultiple** (there is
no prior row state), a **post-image on Delete** (the row is gone) and **any image on RouteTo** (Dataverse supports
none) are rejected by Dataverse, the runtime and `vela plan`. The rule reads the message from the chain (a typed
trigger, or the type of `On<TRequest>()` / `OnMessage<TMarker>()`: a `[SdkMessage]` / `[CustomMessage]` on it or an
SDK request type) and uses the same image table as the runtime and the CLI. A message it
cannot read statically is left alone; the stage rules (no post-image before PostOperation) are enforced by the
builder's type-state and at runtime.

**Fix:** use the image the message supports (a post-image for Create, a pre-image for Delete), or drop the image.
The code fix removes the call.

## VELA008

**Two steps on one trigger must be told apart.** Several steps in a plugin may share a trigger - message + stage +
mode (the entity is fixed per plugin) - when their **filtering attributes**, their **images**, their relationship
scope (`ForRelationship(...)`) or an explicit **`WithStepKey(...)`** differ: each is deployed as its own Dataverse
step, and the executing step selects its handler. Two chains in the same `RegisterPluginSteps()` that share the
trigger, list the same `FilterOn(...)` columns, the same images (alias and columns, in any order) and the same
`ForRelationship(...)` scope, and carry no `WithStepKey(...)`, resolve to the same step key, and the runtime rejects
them at registration; those are flagged. So is a `WithStepKey(...)` used twice in the plugin (case-insensitive),
whatever the triggers: step keys must be unique within a plugin. The rank (`WithExecutionOrder`) does not tell steps
apart. A type-stated trigger is compared under the sdkmessage it registers on: `On<SendEmailRequest>()` and
`On<SendFaxRequest>()` both register on `Send`.

Only complete chains whose message, stage, filters, images, relationships and step key are statically readable are
compared: a chain whose `FilterOn` or image takes something other than a literal column selector (`a => a.Name`), or
whose `ForRelationship(...)` or `WithStepKey(...)` takes something other than a constant string, is skipped rather
than guessed.

**Fix:** give one of the steps a `WithStepKey("...")`, or keep one step and branch inside the handler on what
actually changed:

```csharp
RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.Name)
    .WithExecutionOrder(10)
    .WithStepKey("normalize-name")
    .Handle(NormalizeName);

RegisterStep().OnUpdate().PostOperation().Sync()
    .FilterOn(a => a.Name)
    .WithExecutionOrder(20)
    .WithStepKey("validate-name")
    .Handle(ValidateName);
```

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

**Mutable plugin state.** The Dataverse sandbox caches and reuses plugin instances across executions and calls them
concurrently, so state stored on a plugin / Custom API type leaks data between unrelated operations and races. Four
shapes are flagged:

- a writable field (instance or `static`);
- a writable auto-property (`{ get; set; }`, also with a `private set`): its backing field is a writable field;
- a `readonly` field holding a mutable collection (`List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, the other
  `System.Collections.Generic` collections, `IList<T>` / `ICollection<T>` / `IDictionary<TKey, TValue>` / `ISet<T>`,
  the non-generic `ArrayList` / `Hashtable` / `IList` / `IDictionary`, `Collection<T>` / `ObservableCollection<T>` /
  `KeyedCollection<TKey, TItem>`, and `NameValueCollection` / `StringCollection` / `OrderedDictionary`): `readonly`
  freezes the reference, not the contents;
- a primary-constructor parameter that a member reads or writes: the compiler stores it in a writable field.

`const`, `readonly` and `static readonly` fields of immutable values, get-only and `init` auto-properties, arrays (the
usual fixed column lists), read-only views (`IReadOnlyList<T>`, `ReadOnlyCollection<T>`), the concurrent collections
(thread-safe, the deliberate cache) and a primary-constructor parameter used only in an initializer or a base
constructor call are fine. Plugin types are recognised by symbol (`Microsoft.Xrm.Sdk.IPlugin`,
`Vela.Plugin.PluginBase`, `EntityPluginBase<T>`, `Vela.CustomApi.CustomApiBase`), never by a look-alike name.

**Fix:** keep per-execution state in locals or on the execution context; make constants `const` / `readonly` and
expose collections as read-only.

## VELA012

**Throw `InvalidPluginExecutionException`.** Only `InvalidPluginExecutionException` surfaces its message to the
caller (and the UI). Any other exception thrown from a plugin on a Vela base reaches the caller as the generic
`An unexpected error occurred. Correlation id: ...` (see [Errors](../guides/plugins.md#errors)). Rethrows
(`throw;` / `throw ex;`) are left alone. A code fix changes the thrown type, keeping the arguments.

`PluginBusinessException` is **not** flagged inside a plugin that derives from a Vela base (`PluginBase` /
`EntityPluginBase` / `CustomApiBase`): that base catches it and surfaces its message to the caller as a clean
`InvalidPluginExecutionException` (with a dedicated `business-exception` trace snapshot). It cannot derive from
`InvalidPluginExecutionException` because the SDK seals that type, so the analyzer recognizes it by its full name
(`Vela.Plugin.PluginBusinessException`). Use it
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
The argument is read as a constant, so `new ColumnSet(AllColumns)` over a `const bool` and `AllColumns = true` in an
object initializer are caught too. Like VELA019, the rule applies inside a plugin / Custom API type only: a test
class or a tool in the same solution is not flagged.

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
across steps is left alone. See
[which stage persists a Target write](../guides/plugins.md#writing-the-target-which-stage-persists).

A write is any assignment, compound assignment or `++`/`--` on a property or indexer of the Target, or on an entry
of its `Attributes` collection. The rule follows the Target through:

- **A local that only ever holds the Target**: `var t = ctx.Request.Target;`, `Account t; t = ctx.GetTarget<Account>();`,
  a type pattern (`if (ctx.Request.Target is Account t)`), a chain of such locals (`var b = t;`), a reference cast
  or `as`. A write through the local inside a lambda of the handler counts too.
- **One helper call**: `Mutate(ctx.Request.Target)` or `Mutate(t)`, where `Mutate` is a non-virtual method of the
  handler's own type, declared in the same file, that writes into that parameter (directly or through such a local).
  The warning is on the call.

It stays silent, to avoid false positives, when it cannot be sure the written object is the Target:

```csharp
private void OnUpdate(ILocalPluginContext ctx)
{
    var t = ctx.Request.Target;
    t = new Account();                                       // given another value: never tracked
    t.Name = "x";                                            // not reported

    var copy = new Account { Id = ctx.Request.Target.Id };  // a copy, not the Target
    copy.Name = "x";                                         // not reported
}
```

The rule does not follow:

- a local that is ever given anything other than the Target (a copy, `ToEntity<T>()`, any method result, `null`,
  `??`), compound-assigned (`??=`), deconstructed into, passed by `ref`/`out`, or declared by `out var`, `foreach` or
  a `switch` pattern. The analysis does not track the order of statements, so it keeps only locals that can never
  hold anything else;
- a helper that is virtual, abstract or an override, declared in another type or another file (a `partial` part),
  an extension method, a local function, or a lambda stored in a variable; a `params`, `ref`, `in` or `out`
  parameter;
- a helper that calls another helper (one level only), or one that reads the Target itself from a context it is
  given (`Apply(ctx)`);
- method calls that change the Target without an assignment (`t.Attributes.Add(...)`, `t.Attributes.Remove(...)`).

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
at build instead. Add `.FilterOn(e => e.Column)` before `.Handle(...)`. It also reports a request or marker type naming one of the four
messages (`OnMessage<UpdateMarker>()`) reaching `.Handle(...)`: the type-stated paths offer no `FilterOn`, so use the
typed `OnCreate()` / `OnUpdate()` / ... trigger instead. A chain split across a local variable is not analyzed (it may
have been filtered before the split).

## VELA021

**Typed image projection not generated.** Reported by the generator (a warning, on the `WithPreImage` /
`WithPostImage` call) when a capture lists columns but no projection can be emitted for it: the image alias is not a
compile-time constant, a selector does not read a column property of the entity (`a => a.GetHashCode()`), or the
trigger of a chain that starts at `RegisterStep()` is not constant (`OnMessage(variable)`). Without it the only symptom
is a `CS0246` on the missing `{Handler}_{Pre|Post}Image` type. Nested paths, `Entity` members and relationships are
already errors of VELA001 / VELA010 and are not reported again; a chain split across locals is not reported.

**Fix:** use a constant alias and column selectors, or read the full image with `ctx.GetPreImage<T>()` /
`ctx.GetPostImage<T>()`.

## VELA022

**A step on `SetState` never fires.** Reported on `On<T>()` / `OnMessage<T>()` when `T` pins the message with
`[SdkMessage("SetState")]`; `On<SetStateRequest>()` and a `[CustomMessage("SetState")]` marker register on
`SetStateDynamicEntity` and are not reported. Dataverse accepts a `SetState` registration, but never runs the step: a `SetStateRequest` fires the `SetStateDynamicEntity`
steps, and an Update of `statecode` fires the Update steps plus nested `SetStateDynamicEntity` steps (verified live
on every stage). The step's logic would silently never execute.

**Fix:** register on `OnUpdate().FilterOn(x => x.StateCode)` (fires for both a SetState and an Update of the state),
or drop the `[SdkMessage("SetState")]` and use `On<SetStateRequest>()`, which registers on `SetStateDynamicEntity`
(inputs `EntityMoniker`, `State`, `Status`).

## VELA023

**A message with a typed trigger is registered through it, not `On<TRequest>()`.** `On<TRequest>()` and
`OnAsyncOnly<TRequest>()` are for messages without a typed trigger (Custom APIs, messages Vela does not model). A
request whose message has one (`On<UpdateRequest>()`, `On<AssignRequest>()`, `On<CreateMultipleRequest>()`, ...) is an
**error**, and the runtime rejects the same registration, so the rule holds without the analyzers.

**Fix:** use the typed trigger; the code fix rewrites `On<UpdateRequest>()` to `OnUpdate()`. Then give the handler the
trigger's context (`IUpdatePostContext<Account>` instead of `IMessagePostContext<UpdateRequest>`).

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
- **Typed step context.** For every named handler (`.Handle(OnX)`) on a modelled, entity-bound message, the
  generator also emits a `{Handler}Context` (a subclass of the step's message context). When the step captures an
  image, the context exposes `ctx.PreImage` / `ctx.PostImage` typed to the captured columns (plus
  `ctx.SubordinatePreImage` / `ctx.SubordinatePostImage` on Merge, and per-target `ctx.PreImages` / `ctx.PostImages`
  on CreateMultiple / UpdateMultiple), so the handler reads them as members instead of `ctx.PreImageAs<T>()`. An
  imageless handler gets a bare subclass, so adding a capture later does not change its signature. `OnMessage(...)`
  and Custom API handlers get none. See
  [Typed step context](../guides/plugins.md#typed-step-context-read-images-as-ctxpreimage--ctxpostimage) and the
  VELA015 / VELA016 guard rails.

## VELA024

**Parallel execution in a plugin.** Microsoft's guidance is
[not to use parallel execution in plug-ins](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/do-not-use-parallel-execution-in-plug-ins):
the execution context and the organization services are not thread-safe, work on another thread runs outside the
step's transaction and can outlive the step, and blocking on a task risks deadlocks and timeouts. Reported inside a
plugin / Custom API type on:

- work started on another thread: `Task.Run`, `Task.Factory.StartNew`, `Parallel.For` / `ForEach` / `Invoke`, PLINQ
  `AsParallel()`, `ThreadPool.QueueUserWorkItem`, `new Thread(...)`;
- blocking on a task: `.Result`, `.Wait()`, `Task.WaitAll` / `WaitAny`, `GetAwaiter().GetResult()` (also after
  `ConfigureAwait(...)`, on a `Task` or a `ValueTask`).

Types are matched by namespace (`System.Threading`, `System.Threading.Tasks`, `System.Linq`), so a look-alike
`Task` of another library is not flagged. Like VELA019, code outside a plugin type (tests, tools, a nested helper
type) is left alone.

**Fix:** run the work sequentially on the calling thread (batch Dataverse calls with `ExecuteMultipleRequest` if
needed), or move it out of the sandbox: an asynchronous step, a Power Automate flow or an Azure function.
