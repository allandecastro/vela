# Analyzers and generators

This page lists every Roslyn rule Vela reports, the source generator that emits typed step contexts and image
projections, and the IDE code fixes.

All of them ship inside the `Vela.Plugins` package, in the `Vela.Analyzers` and `Vela.CodeFixes` assemblies. There
is nothing else to reference and nothing to configure: they run in the editor and at build.

- **27 rules**, VELA001 to VELA028. VELA006 is retired and its id is not reused.
- **Errors** flag a registration that would fail at registration or would silently do nothing.
- **Warnings** flag advice: code that compiles and registers, but misbehaves in the Dataverse sandbox or wastes
  resources.
- **Code fixes** (the editor lightbulb) exist for VELA003, VELA004, VELA005, VELA007, VELA012, VELA014, VELA015,
  VELA023, VELA026 and VELA027.

Each rule section below follows the same order: what the rule flags, why it matters, and how to fix it.

## Rules at a glance

| Rule | Title | Category | Severity | Code fix |
| --- | --- | --- | --- | --- |
| [VELA001](#vela001) | Column selector targets a member that is not a Dataverse column | Usage | Error | |
| [VELA002](#vela002) | Step registration is missing Handle(...) | Usage | Error | |
| [VELA003](#vela003) | Duplicate step modifier | Usage | Error | Yes |
| [VELA004](#vela004) | Step handler must be an instance method | Usage | Error | Yes |
| [VELA005](#vela005) | Duplicate column in a selector list | Usage | Error | Yes |
| [VELA006](#vela006) | Retired | | | |
| [VELA007](#vela007) | Image not valid for the step's message | Usage | Error | Yes |
| [VELA008](#vela008) | Duplicate step trigger / duplicate step key in a plugin | Usage | Error | |
| [VELA009](#vela009) | HasChanged without a registered pre-image | Usage | Error | |
| [VELA010](#vela010) | Relationship used as a column | Usage | Error | |
| [VELA011](#vela011) | Mutable plugin state | Reliability | Warning | |
| [VELA012](#vela012) | Throw InvalidPluginExecutionException | Usage | Warning | Yes |
| [VELA013](#vela013) | Retrieving all columns | Performance | Warning | |
| [VELA014](#vela014) | Plugin must be partial | Usage | Error | Yes |
| [VELA015](#vela015) | Step handler context must match the step | Usage | Error | Yes |
| [VELA016](#vela016) | Typed-context handler must bind a single step | Usage | Error | |
| [VELA017](#vela017) | Image read but not captured by the step | Usage | Error | |
| [VELA018](#vela018) | Write to the Target in an asynchronous step | Reliability | Warning | |
| [VELA019](#vela019) | Filesystem access in a plugin | Reliability | Warning | |
| [VELA020](#vela020) | Create/Update step is missing FilterOn(...) | Usage | Error | |
| [VELA021](#vela021) | Typed image projection not generated | Usage | Warning | |
| [VELA022](#vela022) | A step on SetState never fires | Reliability | Warning | |
| [VELA023](#vela023) | A message with a typed trigger is registered through it | Usage | Error | Yes |
| [VELA024](#vela024) | Parallel execution in a plugin | Reliability | Warning | |
| [VELA025](#vela025) | Outbound call to an IP address | Reliability | Warning | |
| [VELA026](#vela026) | HttpClient without a timeout | Reliability | Warning | Yes |
| [VELA027](#vela027) | Empty catch in a plugin | Reliability | Warning | Yes |
| [VELA028](#vela028) | Label longer than Dataverse stores | Usage | Error | |

The category column drops the `Vela.` prefix: the full categories are `Vela.Usage`, `Vela.Reliability` and
`Vela.Performance`.

## VELA001

**Column selector targets a member that is not a Dataverse column.** Error.

**Flags.** A Vela column selector (an `Expression<Func<TEntity, object>>`, such as `a => a.Name`) that does not read
a column of the entity. This covers every Vela selector: `FilterOn(...)`, `WithPreImage(...)` /
`WithPostImage(...)`, the typed query, and the typed reads `Retrieve<T>` / `RetrieveByKey<T>`. Two shapes are
flagged:

- a member declared on the base `Microsoft.Xrm.Sdk.Entity` type, such as `a => a.LogicalName` or `a => a.Attributes`;
- a nested path, such as `a => a.PrimaryContactId.Id` or `a => a.ParentAccount.Name`, which reads a member of
  another object.

The `Id` override that `vela models` generates carries `[AttributeLogicalName]` and names the primary key column, so
`a => a.Id` is accepted on a generated model. An `Id` override without that attribute is still flagged.

**Why.** Neither shape names a column of this entity. The runtime rejects both too, instead of registering a wrong
column.

**Fix.** Select a generated column property of the entity: `a => a.Name`, or the lookup column itself
(`a => a.PrimaryContactId`) rather than a member of it.

## VELA002

**Step registration is missing Handle(...).** Error.

**Flags.** A discarded `RegisterStep()....;` statement, or the expression body of a `void` member
(`RegisterPluginSteps() => RegisterStep()...;`), whose value is still a fluent step builder. A chain stored in a
local to be completed later is not flagged.

**Why.** `Handle(...)` is the last call of the builder (it returns `void`). A chain that stops before it never binds
a handler, so the step has nothing to run.

**Fix.** End the chain with `.Handle(handler)`.

## VELA003

**Duplicate step modifier.** Error. Code fix.

**Flags.** A second call, on the same step, to `FilterOn(...)`, `WithExecutionOrder`, `WithDescription`,
`WithStepKey`, `WithImpersonation`, `WithSupportedDeployment`, `WithUnsecureConfig` or `WithSecureConfigFrom`.
`Disabled()` is exempt: it is an idempotent flag.

**Why.** Each of these sets a single value. A second call overwrites the first and silently drops it. The runtime
builder also throws on the second call, so a chain split across locals still fails at registration.

**Fix.** Call each modifier once, combining the arguments if needed: `FilterOn(a => a.A, a => a.B)`. The code fix
removes a duplicate `With...` call. For a duplicate `FilterOn(...)`, it moves the columns into the first
`FilterOn(...)` so no filtering attribute is lost.

## VELA004

**Step handler must be an instance method.** Error. Code fix.

**Flags.** A `static` method group passed to `.Handle(...)`. Instance method groups (`.Handle(OnCreate)`) and
lambdas (`.Handle(ctx => ...)`) are fine.

**Why.** The handler runs against the plugin instance, so it can reach the services resolved from the built-in
dependency injection, the step's unsecure and secure configuration, and any other instance member. A static method
cannot, and it does not fit the framework's instance-bound handler dispatch.

**Fix.** Remove `static` from the handler (the code fix does this), or use a lambda.

## VELA005

**Duplicate column in a selector list.** Error. Code fix.

**Flags.** The same column listed twice in one call that takes a list of column selectors: `FilterOn(...)`,
`WithPreImage(...)`, `WithPostImage(...)`, `WithSubordinatePreImage(...)`, `WithSubordinatePostImage(...)`, the
typed query's `Select(...)` (`Query.For<T>().Select(...)`), and the typed reads `Retrieve<T>(id, ...)` and
`RetrieveByKey<T>(key, ...)`. Only lambda selectors are compared, so a leading image alias string is ignored. The
same column in two different calls (one in `FilterOn`, one in `WithPreImage`) is fine.

```csharp
.FilterOn(a => a.Name, a => a.Name)                                     // VELA005
service.Retrieve<Account>(id, a => a.Name, a => a.AccountNumber, a => a.Name) // VELA005
```

**Why.** A repeated column has no effect. It is almost always a copy and paste slip.

**Fix.** List each column once. The code fix removes the duplicate.

## VELA006

**Retired.** VELA006 flagged `OnMessage("create")` for a message with a typed trigger. The string trigger no longer
exists: every message is named by a type, and [VELA023](#vela023) covers a request or marker type whose message has a
typed trigger. The id is not reused.

## VELA007

**Image not valid for the step's message.** Error. Code fix.

**Flags.** An image the message can never provide:

- a pre-image on `Create` or `CreateMultiple` (there is no prior row state);
- a post-image on `Delete` (the row is gone);
- any image on `RouteTo` (the message supports none).

The rule reads the message from the chain: a typed trigger, or the type passed to `On<TRequest>()` /
`OnMessage<TMarker>()` (an SDK request type, or a type carrying `[SdkMessage]` / `[CustomMessage]`). It uses the
same image table as the runtime and the CLI. A message it cannot read statically is left alone. The stage rules (no
post-image before PostOperation) are enforced by the builder's types and at runtime instead.

**Why.** The runtime and `vela plan` reject these registrations. The rule moves the failure to the build.

**Fix.** Use the image the message supports (a post-image on Create, a pre-image on Delete), or drop the image. The
code fix removes the call.

## VELA008

**Duplicate step trigger in a plugin / Duplicate step key in a plugin.** Error.

**Flags.** Two cases:

- Two `RegisterStep()` chains in the same plugin that share a trigger (message, stage and mode; the entity is fixed
  per plugin), list the same `FilterOn(...)` columns, the same images (alias and columns, in any order) and the same
  `ForRelationship(...)` scope, and carry no `WithStepKey(...)`.
- The same `WithStepKey(...)` value used twice in one plugin (compared case-insensitively), whatever the triggers.

The execution order (`WithExecutionOrder`) does not tell steps apart. A type-stated trigger is compared under the
sdkmessage it registers on: `On<SendEmailRequest>()` and `On<SendFaxRequest>()` both register on `Send`.

Only complete chains whose message, stage, filters, images, relationship and step key are statically readable are
compared. A chain whose `FilterOn` or image takes something other than a literal column selector, or whose
`ForRelationship(...)` or `WithStepKey(...)` takes something other than a constant string, is skipped.

**Why.** Several steps may share a trigger when their filtering attributes, images, relationship scope or step key
differ: each is deployed as its own Dataverse step, and the executing step selects its handler. Chains that differ
in none of these resolve to the same step key, and the runtime rejects them at registration. Step keys must be
unique within a plugin.

**Fix.** Give one of the steps a `WithStepKey("...")`, or keep one step and branch inside the handler on what
changed:

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

**HasChanged without a registered pre-image.** Error.

**Flags.** A call to `HasChanged` or `HasAnyChanged`, with the default pre-image alias, in a handler whose step
registers no pre-image. The rule fires only when the handler is an instance method bound to exactly one
`RegisterStep()` chain in the same class. Lambda handlers, handlers shared across steps and custom aliases are left
alone to avoid false positives.

When that chain's `WithPreImage(...)` lists its columns as selectors, a compared column missing from the list is
flagged as well.

**Why.** Both methods compare the Target with the step's pre-image, and throw at runtime when none is registered. A
column missing from the pre-image reads as `null`, so it looks changed whenever the Target carries it.

**Fix.** Add `WithPreImage(...)` with the columns you compare, or remove the change check.

## VELA010

**Relationship used as a column.** Error.

**Flags.** A relationship navigation property of a generated model (a typed parent for N:1, a typed collection for
1:N, marked `[RelationshipSchemaName(...)]`) passed to a Vela column selector: `FilterOn`, `Select`, `Where`,
`OrderBy`, `WithPreImage`, `WithPostImage`, `Retrieve<T>` or `RetrieveByKey<T>`. Only Vela's own selectors are
checked, so ordinary LINQ `Select` / `Where` is never flagged.

**Why.** A relationship has no storable column. The runtime resolver rejects it; without this rule the mistake would
surface only at run time.

**Fix.** Select the lookup column instead (`a.PrimaryContactId`, not `a.PrimaryContact`), or load related records
with a relationship-aware read such as `RetrieveWithRelated` (see
[Relationship navigation properties](../guides/models.md#relationship-navigation-properties)).

## VELA011

**Mutable plugin state.** Warning.

**Flags.** State that can change on a plugin or Custom API type:

- a writable field, instance or `static`;
- a writable auto-property (`{ get; set; }`, also with a `private set`), whose backing field is writable;
- a `readonly` field holding a mutable collection: `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>` and the other
  `System.Collections.Generic` collections, `IList<T>` / `ICollection<T>` / `IDictionary<TKey, TValue>` / `ISet<T>`,
  the non-generic `ArrayList` / `Hashtable` / `IList` / `IDictionary`, `Collection<T>` / `ObservableCollection<T>` /
  `KeyedCollection<TKey, TItem>`, and `NameValueCollection` / `StringCollection` / `OrderedDictionary`
  (`readonly` freezes the reference, not the contents);
- a primary-constructor parameter that a member reads or writes (the compiler stores it in a writable field).

Not flagged: `const`, `readonly` and `static readonly` fields of immutable values, get-only and `init`
auto-properties, arrays (the usual fixed column lists), read-only views (`IReadOnlyList<T>`,
`ReadOnlyCollection<T>`), the concurrent collections (thread-safe, the deliberate cache), and a primary-constructor
parameter used only in an initializer or a base constructor call.

Plugin types are recognised by symbol (`Microsoft.Xrm.Sdk.IPlugin`, `Vela.Plugin.PluginBase`,
`EntityPluginBase<T>`, `Vela.CustomApi.CustomApiBase`), never by a look-alike name.

**Why.** Dataverse caches plugin instances and reuses them across executions, concurrently. State stored on the
instance leaks between unrelated operations and races (see
[Develop IPlugin implementations as stateless](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/develop-iplugin-implementations-stateless)
on Microsoft Learn).

**Fix.** Keep per-execution state in locals or on the execution context. Make constants `const` or `readonly`, and
expose collections as read-only.

## VELA012

**Throw InvalidPluginExecutionException.** Warning. Code fix.

**Flags.** A `throw` of any exception other than `InvalidPluginExecutionException` inside a plugin or Custom API type
(a type implementing `IPlugin` or deriving from a Vela base). Rethrows (`throw;`, `throw ex;`) are left alone. A
throw in a plain helper or an injected service class is not analyzed.

`PluginBusinessException` is not flagged in a plugin that derives from a Vela base (`PluginBase`,
`EntityPluginBase<T>`, `CustomApiBase`): the base catches it and surfaces its message as an
`InvalidPluginExecutionException`, with a `business-exception` trace snapshot. The analyzer recognizes it by its
full name, `Vela.Plugin.PluginBusinessException` (it cannot derive from `InvalidPluginExecutionException`, which the
SDK seals). In a type that implements `IPlugin` directly there is no Vela base to do that mapping, so
`PluginBusinessException` is still flagged there.

**Why.** Only `InvalidPluginExecutionException` surfaces its message to the caller and the UI. Any other exception
thrown from a plugin on a Vela base reaches the caller as the generic
`An unexpected error occurred. Correlation id: ...` (see [Errors](../guides/plugins.md#errors)).

**Fix.** Throw `InvalidPluginExecutionException` for a user-facing failure. Use `PluginBusinessException` for
business semantics or a localizable message, in a plugin that derives from a Vela base. In a helper class that may
run under any entry point, throw `InvalidPluginExecutionException`. The code fix changes the thrown type and keeps
the arguments.

## VELA013

**Retrieving all columns.** Warning.

**Flags.** `new ColumnSet(true)` in a plugin or Custom API type. The argument is read as a constant, so
`new ColumnSet(AllColumns)` over a `const bool` and `AllColumns = true` in an object initializer are caught too. The
explicit `SelectAll()` of the typed query states the intent and is not flagged. A test class or a tool in the same
solution is not flagged.

**Why.** Every column is fetched, which moves far more data than a plugin needs.

**Fix.** Pass the columns you use to `ColumnSet`, or use the typed reads: `Retrieve<T>(id, a => a.Name, ...)` or
`Query.For<T>().Select(...)`.

## VELA014

**Plugin must be partial.** Error. Code fix.

**Flags.** A plugin class, or a type that encloses it, that is not `partial` while the source generator emits a
type nested inside it. The generator emits a `{Handler}Context` for every named handler on a modelled message, and
a projection for every explicit image capture (`WithPreImage(a => a.X, ...)`, `WithPostImage(...)`). The rule asks
the generator itself whether it emits a nested type, so a capture the generator cannot project (see
[VELA021](#vela021)) does not require `partial`. A plugin whose handlers are all lambdas and that captures no
explicit image generates nothing and does not need `partial`.

**Why.** A nested generated type needs a second `partial` declaration of the class and of every enclosing class,
struct or record. Without this rule the build fails later with an obscure `CS0260`.

**Fix.** Add `partial` to the plugin class and its enclosing types (the code fix does this). `vela init` and
`vela add` scaffold plugins as `partial` already.

## VELA015

**Step handler context must match the step.** Error. Code fix.

**Flags.** Two cases:

- On the guided `Create`, `Update` and `Delete` path, a handler whose context is for a different message or stage,
  or an unrelated type. The handler must take the step's message context (for example `IUpdatePostContext<T>`), its
  generated typed step context (a subclass of it), or the plain `ILocalPluginContext`.
- On every path, a handler that does not take exactly one parameter or does not return `void`.

**Why.** The `Handle(Delegate)` overload, which lets a method group bind the generated context, compiles any method
group. The step builder rejects these handlers at registration; the rule reports them at build.

**Fix.** Give the handler the step's context or its generated `{Handler}Context`. The code fix corrects the type,
written as short as the file's `using` directives allow.

## VELA016

**Typed-context handler must bind a single step.** Error.

**Flags.** One handler bound on more than one image-capturing step of the same class. Two nested plugins in one
container class may each have a handler with the same name.

**Why.** The generated typed step context is named after the handler (`{Handler}Context`), so two steps would need
two contexts with the same name.

**Fix.** Give each image-capturing step its own handler. To share a handler anyway, read the images through the
plain context with `ctx.PostImageAs<T>()`.

## VELA017

**Image read but not captured by the step.** Error.

**Flags.** A handler that reads `ctx.PreImage`, `ctx.PostImage`, `ctx.SubordinatePreImage` or
`ctx.SubordinatePostImage` when its step has no matching `WithPreImage(...)`, `WithPostImage(...)`,
`WithSubordinatePreImage(...)` or `WithSubordinatePostImage(...)`. The rule fires only when the handler is an
instance method bound by exactly one `RegisterStep()` chain in the same class.

**Why.** A post-operation context exposes both `PreImage` and `PostImage`, and a Merge context also the subordinate
images. Reading an image the step did not capture compiles, but returns `null` at runtime.

**Fix.** Add the capture to the step, or read the image it does capture.

## VELA018

**Write to the Target in an asynchronous step.** Warning.

**Flags.** A write to the Target (`ctx.Request.Target`, `ctx.GetTarget<T>()`) in the handler of an `.Async()` step.
The rule fires only when the handler is an instance method bound by exactly one `RegisterStep()` chain; a handler
shared across steps is left alone.

A write is an assignment, a compound assignment, or `++` / `--` on a property or indexer of the Target, or on an
entry of its `Attributes` collection. The rule follows the Target through:

- **a local that only ever holds the Target**: `var t = ctx.Request.Target;`,
  `Account t; t = ctx.GetTarget<Account>();`, a type pattern (`if (ctx.Request.Target is Account t)`), a chain of
  such locals (`var b = t;`), a reference cast or `as`. A write through the local inside a lambda of the handler
  counts too.
- **one helper call**: `Mutate(ctx.Request.Target)` or `Mutate(t)`, where `Mutate` is a non-virtual method of the
  handler's own type, declared in the same file, that writes into that parameter (directly or through such a
  local). The warning is on the call.

To avoid false positives, it stays silent when it cannot be sure the written object is the Target:

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
  `??`, an implicit user-defined conversion), that is compound-assigned (`??=`) or incremented itself (`t++`),
  deconstructed into, passed by `ref` / `out`, or declared by `out var`, `foreach` or a `switch` pattern. The
  analysis does not track statement order, so it keeps only locals that can never hold anything else;
- a helper that is virtual, abstract or an override, declared in another type or another file (a `partial` part),
  an extension method, a local function, or a lambda stored in a variable; a `params`, `ref`, `in` or `out`
  parameter;
- a helper that calls another helper (one level only), or one that reads the Target itself from a context it is
  given (`Apply(ctx)`);
- method calls that change the Target without an assignment (`t.Attributes.Add(...)`, `t.Attributes.Remove(...)`).

**Why.** An asynchronous step runs after the main operation has committed, in its own execution. A change to the
Target there is neither written to the database nor seen by any later step. See
[which stage persists a Target write](../guides/plugins.md#writing-the-target-which-stage-persists).

**Fix.** Call `ctx.Service.Update(...)` with the change, or move the logic to a synchronous step.

## VELA019

**Filesystem access in a plugin.** Warning.

**Flags.** In a plugin or Custom API type: calls to `System.IO.File` and `Directory`, and `new FileStream`,
`FileInfo`, `DirectoryInfo`, `DriveInfo` or `FileSystemWatcher`. The check matches type identity. Not flagged:
in-memory types (`MemoryStream`, `StringReader` / `StringWriter`, `BinaryReader` / `BinaryWriter`), the string
helpers of `System.IO.Path` (`Path.Combine`, `Path.GetExtension`, ...), and
`Assembly.GetManifestResourceStream(...)`. Filesystem use in an unrelated helper class is not analyzed.

**Why.** The Dataverse sandbox denies file I/O, so these calls throw at runtime.

**Fix.** Keep data in memory, read embedded resources with `Assembly.GetManifestResourceStream`, or move the file
work out of the sandbox. See [Respect the sandbox](../guides/plugins.md#respect-the-sandbox).

## VELA020

**Create/Update step is missing FilterOn(...).** Error.

**Flags.** A `RegisterStep()` chain on `Create`, `Update`, `CreateMultiple` or `UpdateMultiple` that reaches
`.Handle(...)` without `.FilterOn(...)`. It also flags a request or marker type that names one of these four
messages (`OnMessage<UpdateMarker>()`) reaching `.Handle(...)`: the type-stated paths offer no `FilterOn`. A chain
split across a local variable is not analyzed, because it may have been filtered before the split.

**Why.** Vela requires at least one filtering attribute on these messages, so the step does not fire on every
create or update. Building the step fails at runtime without one; the rule reports it at build.

**Fix.** Add `.FilterOn(e => e.Column)` before `.Handle(...)`. For a type-stated message, use the typed trigger
(`OnCreate()`, `OnUpdate()`, ...) instead.

## VELA021

**Typed image projection not generated.** Warning, reported by the source generator on the `WithPreImage` or
`WithPostImage` call.

**Flags.** A capture that lists columns, but for which no projection can be emitted:

- a column argument is not an inline selector lambda;
- the image alias is not a compile-time constant;
- a selector does not read a column property of the entity (`a => a.GetHashCode()`);
- the message or stage of a chain that starts at `RegisterStep()` cannot be read from the chain.

Nested paths, `Entity` members and relationships are already VELA001 and VELA010 errors and are not reported
again. A chain split across locals is not reported.

**Why.** Without this warning, the only symptom is a `CS0246` on the missing `{Handler}_{Pre|Post}Image` type.

**Fix.** Use a constant alias and column selectors, or read the full image with `ctx.GetPreImage<T>()` /
`ctx.GetPostImage<T>()`.

## VELA022

**A step on SetState never fires.** Warning.

**Flags.** `On<T>()` or `OnMessage<T>()` where `T` pins the message with `[SdkMessage("SetState")]`.
`On<SetStateRequest>()` and a `[CustomMessage("SetState")]` marker register on `SetStateDynamicEntity` and are not
flagged.

**Why.** Dataverse accepts a step on `SetState` but never runs it: a `SetStateRequest` fires the
`SetStateDynamicEntity` steps, and an Update of `statecode` fires the Update steps. The step's logic would silently
never run.

**Fix.** Register on `OnUpdate().FilterOn(x => x.StateCode)`, which covers a state change made either way.
`SetStateRequest` is
[deprecated](https://learn.microsoft.com/dotnet/api/microsoft.crm.sdk.messages.setstaterequest) in favour of that
Update. Alternatively, drop `[SdkMessage("SetState")]` and use `On<SetStateRequest>()`, which registers on
`SetStateDynamicEntity` (inputs `EntityMoniker`, `State`, `Status`).

## VELA023

**A message with a typed trigger is registered through it.** Error. Code fix.

**Flags.** `On<TRequest>()`, `OnAsyncOnly<TRequest>()` or `OnMessage<TRequest>()` with an SDK request whose message
has a typed trigger: `On<UpdateRequest>()`, `On<AssignRequest>()`, `On<CreateMultipleRequest>()`, and so on.

**Why.** Each message is registered one way. `On<TRequest>()`, `OnAsyncOnly<TRequest>()` and `OnMessage<TMarker>()`
are for messages without a typed trigger (Custom APIs, messages Vela does not model). The runtime rejects the same
registration, so the rule holds even without the analyzers.

**Fix.** Use the typed trigger. The code fix rewrites `On<UpdateRequest>()` to `OnUpdate()`. Then give the handler
the trigger's context (`IUpdatePostContext<Account>` instead of `IMessagePostContext<UpdateRequest>`).

## VELA024

**Parallel execution in a plugin.** Warning.

**Flags.** In a plugin or Custom API type:

- work started on another thread: `Task.Run`, `Task.Factory.StartNew`, `Parallel.For` / `ForEach` / `Invoke`, PLINQ
  `AsParallel()`, `ThreadPool.QueueUserWorkItem`, `new Thread(...)`;
- blocking on a task: `.Result`, `.Wait()`, `Task.WaitAll` / `WaitAny`, `GetAwaiter().GetResult()` (also after
  `ConfigureAwait(...)`, on a `Task` or a `ValueTask`).

Types are matched by namespace (`System.Threading`, `System.Threading.Tasks`, `System.Linq`), so a look-alike `Task`
from another library is not flagged. Code outside a plugin type (tests, tools, a nested helper type) is left alone.

**Why.** Microsoft advises
[not to use parallel execution in plugins](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/do-not-use-parallel-execution-in-plug-ins).

**Fix.** Run the work sequentially on the calling thread (batch Dataverse calls with `ExecuteMultipleRequest` if
needed), or move it out of the sandbox: an asynchronous step, a Power Automate flow or an Azure function.

## VELA025

**Outbound call to an IP address.** Warning.

**Flags.** In plugin code (see [Scope of VELA025 and VELA026](#scope-of-vela025-and-vela026)), a string whose host is
an IPv4 or IPv6 literal, when it is given to:

- `new Uri(...)`, which covers `client.BaseAddress = new Uri("https://10.0.0.1/")` and
  `request.RequestUri = new Uri(...)`;
- a string parameter of an `HttpClient` or `HttpRequestMessage` method or constructor
  (`client.GetStringAsync("http://192.168.1.10/x")`, `new HttpRequestMessage(HttpMethod.Get, "...")`), or of
  `WebRequest` / `HttpWebRequest` (`WebRequest.Create("...")`).

The string must be known at build time: a literal, a `const`, an interpolated string, or a `+` concatenation whose
constant start already holds the whole host (`$"https://10.0.0.1/items/{id}"`, `"https://10.0.0.1:" + port`). A
local is read through its declaration (`string url = "http://10.0.0.1/api"; client.GetAsync(url);`), also along a
chain of locals, when nothing else in the method writes it: no assignment (in a branch or a lambda too), compound
assignment, increment, deconstruction, `ref` / `out` / `in` argument or `ref` alias. The host is parsed with
`System.Uri`, and only `http` / `https` URIs whose host is an IP literal (`10.0.0.1`, `[2001:db8::1]`) are reported.

Not reported: a named host (`localhost` included), a relative or non-HTTP string, a string never given to one of the
members above, a URI built from a field, a parameter, configuration or a local written after its declaration, and a
host cut by an interpolation (`$"https://10.0.0.{n}/"`).

**Why.** A plugin in the sandbox cannot call an IP address. See the web access restrictions in
[Access external web services](https://learn.microsoft.com/power-apps/developer/data-platform/access-web-services).

**Fix.** Give the service a DNS name and call that name.

### Scope of VELA025 and VELA026

Both rules check plugin code: a plugin or Custom API type and, in an assembly that declares one, every other type
too (the services and helpers it resolves run in the sandbox with it). In a test project (one that references
`Vela.Testing`), only the plugin types are checked. A shared library that declares no plugin type is not checked.

## VELA026

**HttpClient without a timeout.** Warning. Code fix.

**Flags.** A `new HttpClient(...)` (or a target-typed `new()`) in plugin code (see
[Scope of VELA025 and VELA026](#scope-of-vela025-and-vela026)) whose `Timeout` is never set in the member that creates
it: the method, constructor or accessor (lambdas and local functions in it included), or the field or property
initializer. The client is fine when:

- its object initializer sets it: `new HttpClient { Timeout = TimeSpan.FromSeconds(15) }`;
- the local, field or property that holds it gets `.Timeout = ...` in the same member, in any order (also inside a
  lambda of that member).

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    services.AddScoped(_ => new HttpClient());                                          // VELA026
    services.AddScoped(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });     // fine
}
```

A factory lambda that returns the client is checked like any other creation. A field created in one method whose
`Timeout` is set in another method is reported: the check stays in the creating member. The check does not follow a
client that leaves that member: a client returned from a method or a local function, passed as an argument
(`new MyApiClient(new HttpClient())`) or copied into another variable is not reported, whether or not the receiving
code sets `Timeout`. Only `System.Net.Http.HttpClient` itself is checked, not a derived type.

**Why.** The default `HttpClient` timeout is 100 seconds, close to the time a plugin has to complete. Microsoft
recommends
[setting a timeout on external calls from a plugin](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins).

**Fix.** Set `Timeout`. The code fix adds `Timeout = TimeSpan.FromSeconds(30)` to the object initializer, creating
the initializer if needed. 30 seconds is a starting point: set the value the service needs.

## VELA027

**Empty catch in a plugin.** Warning. Code fix.

**Flags.** Inside a plugin or Custom API type (lambdas and local functions included), a `catch` clause whose block
holds no statement and no comment: `catch { }`, `catch (Exception) { }`,
`catch (FaultException<OrganizationServiceFault>) { }`, with or without a `when` filter.

```csharp
try { service.Delete("account", id); } catch (FaultException<OrganizationServiceFault>) { }         // VELA027
try { service.Delete("account", id); } catch (FaultException<OrganizationServiceFault>) { throw; }  // fine
```

Not reported: a block with any statement (`throw;`, a trace call, `return`, ...), and a block that holds a comment or
a preprocessor directive, which Vela takes as a deliberate, documented swallow:

```csharp
catch (InvalidOperationException)
{
    // the cache entry may already be gone: nothing to do
}
```

Code outside a plugin type (tests, tools, a nested helper type) is left alone.

**Why.** The exception is swallowed: nothing is traced and the failure disappears. When the swallowed exception is a
failed organization-service write inside the pipeline transaction, Dataverse fails the operation anyway (see
[Transaction errors](https://learn.microsoft.com/troubleshoot/power-platform/dataverse/plug-in-execution/dataverse-plug-ins-errors#transaction-errors)
on Microsoft Learn). The test harness reproduces that failure for a caught failed write (see
[Simulate the pipeline](../guides/testing.md#simulate-the-pipeline-cascade)).

**Fix.** Rethrow, trace, or comment why the exception is ignored. The code fix puts `throw;` in the block, which
rethrows the original exception with its stack trace, and drops the unused exception variable
(`catch (Exception ex) { }` becomes `catch (Exception) { throw; }`) unless the clause has a `when` filter. To surface
a message to the caller instead, throw an `InvalidPluginExecutionException` (see [VELA012](#vela012)).

## VELA028

**Label longer than Dataverse stores.** Error.

**Flags.** A compile-time constant label (a literal, a `const` field, a concatenation of constants or a constant
interpolated string) longer than `vela plan` accepts:

| Label | Set by | At most |
| --- | --- | --- |
| Custom API display name | `.DisplayName(...)` | 100 |
| Custom API description | `.Description(...)` | 300 |
| Request parameter description | `RequestParameter(..., description: ...)`, `Request(p => p.Add(x => x.Prop, optional, description))`, `[Description(...)]` on a property of the request type | 300 |
| Response property description | `ResponseParameter(..., description: ...)`, `Response(p => p.Add(x => x.Prop, optional, description))`, `[Description(...)]` on a property of the response type | 100 |
| Step description | `.WithDescription(...)` | 256, step-key marker included |

The builder methods are covered on both Custom API builders (`CustomApiBuilder<TRequest, TResponse>` and
`CustomApiLightBuilder`). A `[Description]` counts as a request parameter description on a type deriving from
`OrganizationRequest`, and as a response property description on a type deriving from `OrganizationResponse`.

A step description is stored with the step-key marker that Vela appends on its own line (`[vela:stepkey=<key>]`).
The trimmed text therefore gets 256 characters minus that marker: 225 with the automatic key, fewer with a longer
`.WithStepKey(...)`. The rule reads the key from the same `RegisterStep()` chain.

```csharp
RegisterCustomApi("zava_RecalculateOrders", Recalculate)
    .DisplayName("Recalculate the order totals, the taxes and the shipping costs of every line of all the selected orders") // VELA028: 103 characters
    .Description("Recalculates the order totals.");                                                                           // fine
```

Not reported: a value known only at run time (a parameter, a resource string, a non-constant field), a step whose
key is not a constant, and a `RegisterStep()` chain split across a local variable (its key may be set after the
split). `vela plan` still checks every label, constant or not.

**Why.** `vela plan` rejects a label longer than the Dataverse column stores. The rule moves the failure from plan
to the build. Column lengths:
[Custom API tables](https://learn.microsoft.com/power-apps/developer/data-platform/custom-api-tables) and
[SdkMessageProcessingStep](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/sdkmessageprocessingstep).

**Fix.** Shorten the label.

## Typed image projection generator

The `Vela.Analyzers` assembly also contains an incremental source generator, `ImageProjectionGenerator`. Like the
analyzers, it ships with `Vela.Plugins` and needs no separate package or project.

For every step that captures explicit columns in `WithPreImage(a => a.X, ...)` or `WithPostImage(...)`, it emits a
per-step projection type that holds only those columns. You read it with `ctx.PreImageAs<T>()` /
`ctx.PostImageAs<T>()`, or as a member of the typed step context (below). Reading a column the step did not capture
is then a compile error instead of a silent `null`. See
[Typed images](../guides/plugins.md#typed-images-compile-safe-projections) in the plugin guide for the full usage.

- **Regenerated at every build.** The projections are produced in memory from the capture lambdas against your
  current early-bound model. They are not files in your source tree; `F12` opens them. If a captured column is
  removed or renamed in the model (for example after re-running `vela models`), the selector and the handler that
  reads the projection stop compiling. The drift surfaces as a build error, never as a stale value.
- **Naming and nesting.** A projection is a nested type inside the plugin's own `partial` class, named
  `{Handler}_{Pre|Post}Image` (plus the alias when one is given). Because it is scoped to the plugin class, it needs
  no plugin-name prefix and no `using`: the handler reads it unqualified (`ctx.PostImageAs<OnUpdated_PostImage>()`).
  This is why such a plugin must be `partial` ([VELA014](#vela014)). A lambda handler (no name) falls back to
  `{Message}{Stage}_{Pre|Post}Image`. The message comes from the trigger: the guided `OnCreate()`, ..., the typed
  `On<TRequest>()`, or `OnMessage<TMarker>()` (the request or marker type name, minus a `Request` suffix).
- **Opt-in and additive.** A capture with no explicit columns (`WithPreImage()`) keeps the full early-bound entity
  and generates nothing. If the column list is not a literal set of selectors, the generator skips it, you keep the
  untyped image, and [VELA021](#vela021) says why. A column property without `[AttributeLogicalName]` is projected
  under its lower-cased name, as the runtime resolves it.
- **Any containing type.** The generated part repeats each containing type's kind and type parameters, so a generic
  plugin, or a plugin nested in a `struct` or `record`, works (each must be `partial`).
- **Explicit widening to the entity.** Each projection carries an explicit cast to its entity
  (`(Account)ctx.PreImage`) for when a service needs the whole image. The cast is lossless (the projection keeps the
  source image, so `Id`, the logical name and every attribute survive) and equivalent to
  `ctx.GetPreImage<Account>()` / `ctx.GetPostImage<Account>()` (both go through `Entity.ToEntity`). An absent image
  casts to `null`. It is deliberately not implicit, so leaving the captured-columns view is a visible step. See
  [Need the whole entity](../guides/plugins.md#need-the-whole-entity-for-a-service).
- **Typed step context.** For every named handler (`.Handle(OnX)`) on a modelled, entity-bound message, the generator
  also emits a `{Handler}Context`, a subclass of the step's message context. When the step captures an image, the
  context exposes `ctx.PreImage` / `ctx.PostImage` typed to the captured columns (plus `ctx.SubordinatePreImage` /
  `ctx.SubordinatePostImage` on Merge, and per-target `ctx.PreImages` / `ctx.PostImages` on CreateMultiple and
  UpdateMultiple). An imageless handler gets a bare subclass, so adding a capture later does not change its
  signature. `OnMessage<TMarker>()` and Custom API handlers get none. See
  [Typed step context](../guides/plugins.md#typed-step-context-read-images-as-ctxpreimage--ctxpostimage), and
  [VELA015](#vela015) and [VELA016](#vela016) for the guard rails.
