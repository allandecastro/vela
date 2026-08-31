# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## CLI

- **P3 - ribbon deploy end to end.** The classic `RibbonDiffXmlGenerator` (+ `RibbonSchema` validation) exists as
  a utility, but nothing wires the command bar automatically: registering a command on a button is left to the
  developer (Power Apps command designer, or a hand-authored `RibbonDiffXml`). Automating it would need a source
  of truth for the exported command functions (a build manifest from `vela-client`, or a scan) plus a Dataverse
  apply (modern `appaction` records / solution import). Deferred: low value versus letting the dev own the
  wiring. Design: `vela-client` `budapest/docs/plans/ribbon-command-bar.md` (PR-R4).
- **P3 - scaffold resolves Vela from nuget.org.** Now that `Vela.Plugins` and `Vela.Testing` are published,
  `vela init` still emits a `nuget.config` with a `local-packages/` feed (a workaround from before the packages
  shipped). Update the scaffold templates so a generated project resolves the Vela packages from nuget.org
  directly, and drop or simplify the local feed. Refresh the scaffold docs (scaffold.md, initialize-a-solution.md)
  to match.

## Runtime (Vela.Plugins)

- **P3 - simplify `PluginCacheService` locking.** The striped lock pool (`LockStripeCount = 64` + `object[]` +
  hash-and-mask in `GetKeyLock`) is over-engineered: the lock only guards against a same-key stampede (two threads
  running the same factory on a miss); reads are already lock-free via the `ConcurrentDictionary`. Replace the
  fixed array with a `ConcurrentDictionary<string, object>` keyed lock (`GetOrAdd(key, _ => new object())`) - a
  true per-key lock, no hash collisions, and it drops the stripe-count constant. A single global lock is the even
  simpler alternative, at the cost of serializing all cache-miss factory calls process-wide (a real coupling for
  slow Dataverse-backed factories, since the cache is a shared static across concurrent sandbox executions). Do
  **not** add an entry cap: the entry count is the caller's concern, bounded by TTL, and eviction would add a
  policy nobody asked for. Framework-line change (separate release from the CLI).
- **P2 - close the compile-time gap on image/stage validity.** `StepBuilder` forces `FilteringAttributes(...)`
  at **compile time** via the type-state chain (`IFilteringStep*Builder`), but image/stage validity is only a
  **runtime** guard (`EnsureImageAllowed`/`IsImageAllowed`): `.PostImage()` on a PreValidation step compiles fine
  and only throws when the chain runs (surfaced hard at `vela plan`/`apply` via `PackageScanner`, and by the test
  harness, so it is not unchecked - just late). Either extend the type-state to a message x stage matrix so an
  invalid image can't be written, or at minimum keep the runtime guard but improve it: (1) the error when
  `.PostImage()`/`.PreImage()` is called **before** `.Stage()` reads the default stage `(PluginStages)0` and reads
  as "stage '0'" - re-validate images in `Build()` against the final stage and emit a message that names the real
  stage; (2) make the message spell out which stage/message *does* support the requested image. Guards already
  exist and are tested (`RoutingTests.StepBuilder_Disallows*`); this is about catching it as early as filtering.
- **P2 - guard the empty `target.Id` footgun in pre-stage Create.** On PreValidation/PreOperation **Create**,
  the record is not yet committed, so `target.Id` is `Guid.Empty`; nothing warns a developer who reads/uses it
  (the scaffolded `AccountPostCreatePlugin` uses `target.Id` and is PostOperation for exactly this reason - moving
  it to a pre-stage silently passes an empty id). Consider a runtime trace/warning (or doc note on
  `EntityPluginBase.Execute`) when `target.Id` is empty on a Create pre-stage. Note the deliberate design: for
  reference-based messages (Delete/Associate/SetState) `ResolveTarget` materializes a typed `TEntity` holding only
  the id, so `Execute`/handlers are always typed and never null - that part is intended, not a bug.
