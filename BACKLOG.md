# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## CLI

- **P3 - parallel `dotnet build <sln>` can race on the plugin package output.** With two plugin projects
  (`.Plugins` + `.Integrations`) both running `GeneratePackageOnBuild` into the shared `artifacts/`
  (`PackageOutputPath`), a parallel solution build occasionally fails with `The process cannot access the file
  '...<Plugin>.1.0.0.nupkg' because it is being used by another process` (seen in the `VELA_BUILD_SMOKE` test;
  worked around there with `-m:1`). This hits the documented `dotnet build <sln> -c Release` command, so it is a
  real if intermittent user-facing flake. Investigate a proper fix (e.g. per-project pack output, serialize the
  pack target, or GeneratePackageOnBuild only where needed) rather than telling users to pass `-m:1`.
- **P2 - reject unknown options on the remaining commands.** `plan` / `apply` / `inspect` now fail fast on an
  unknown/misspelled option (`TryValidateKnownOptions` in the shared parser). `init`, `add`, `models` and `sign`
  each have their own ad-hoc parser that still silently ignores unrecognized `--flags`, so a typo there is
  swallowed the same way. Extend the same allowlist guard to those commands (one known-option set per command,
  next to its parser), and add tests. Watch the positional-heavy commands (`add <kind> <name>`, `init <name>`)
  so real positionals are not misread as unknown options.
- **P1 - scaffold packages the plugin with `dotnet build`, not `dotnet pack`.** The `.csproj` uses
  `Microsoft.PowerApps.MSBuild.Plugin` with `GeneratePackageOnBuild=true`; that SDK bundles the dependent
  assemblies from the *publish* output, and publish only runs on **build** (`PublishOnBuild` +
  `_PowerAppsPlugin_PublishAfterBuild AfterTargets="Build"`). A cold `dotnet pack -c Release -o artifacts` (the
  currently documented step) does not publish first, so it emits an assembly-less package (`NU5128`, empty
  `lib/net462`) and `vela plan` then fails with "No assemblies found in package". Verified: clean
  `dotnet build -c Release` produces the correct 20-assembly package; clean `dotnet pack` produces 0. Fix: set
  `<PackageOutputPath>$(MSBuildProjectDirectory)\..\..\artifacts</PackageOutputPath>` on the Plugins/Integrations
  csproj (a plain `dotnet build -c Release` then drops the correct package into `artifacts/`), and replace every
  documented `dotnet pack ... -o artifacts` with `dotnet build -c Release` (README next-steps, scaffold/register
  docs, and the signing docs' `dotnet pack -p:...` -> `dotnet build -p:...`; the signing target is
  `AfterTargets="Pack"`, which still fires under `GeneratePackageOnBuild`). Update golden/scaffold tests + CHANGELOG.
- **P2 - persistent auth cache for interactive login.** `vela plan/apply/inspect --interactive` re-prompts the
  Dataverse URL and opens a browser login on every invocation: MSAL only caches the token in-memory (lost between
  CLI runs) and the URL is not remembered. Add a persisted MSAL token cache (e.g. `Microsoft.Identity.Client`
  token-cache serialization under the user profile) and remember the last URL, so a repeat run reuses the token
  silently. Mind secret hygiene (no tokens in world-readable files) and an opt-out/clear path.
- **P2 - Windows temp-dir cleanup after assembly introspection.** `PackageScanner` does
  `Assembly.LoadFrom(...)` to read step definitions; on Windows this locks the DLL for the process lifetime, so
  the best-effort temp cleanup fails with "Access to the path 'Vela.Plugins.dll' is denied" and leaks a
  `vela-tools\<guid>` directory each run. Load the assembly into a collectible `AssemblyLoadContext` and unload it
  before deleting (or load via bytes) so the files can be removed. Note: the scan still runs user code, so this is
  about cleanup/noise, not the security posture (see `PackageScanner` comment).
- **P3 - surface a stale-package hint.** When `vela plan` reads step registrations that do not match the source
  (e.g. a step edited to `OnDelete`/`PostOperation` but the `.nupkg` still carries the old `OnCreate`/`PreValidation`
  because it was not rebuilt), the error names the *old* message/stage and reads as a mystery. This is a natural
  consequence of a stale artifact (related to the `dotnet build` fix above), but the CLI could help: e.g. warn when
  a package file is older than its source assembly's project, or add a "the package may be out of date - rebuild"
  hint to the introspection-failure message.
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
- **P2 - make an invalid image/stage a compile error (typed stage chain), not a runtime throw.** Today
  `StepBuilder` forces `FilteringAttributes(...)` at **compile time** (type-state), but image/stage validity is
  only a **runtime** guard (`EnsureImageAllowed`/`IsImageAllowed`): `.PostImage()` on a **PreValidation** step -
  or on a **Delete** step (Delete has no post-image; only a pre-image in PostOperation) - compiles fine and only
  throws when the chain runs. **Goal: the project must not compile with an invalid combination.** This lives
  entirely in the fluent builder (not in `Execute`), so it is achievable: replace the enum `.Stage(PluginStages.X)`
  with **typed stage methods** (`.PreValidation()` / `.PreOperation()` / `.PostOperation()`) returning message x
  stage-specific interfaces that expose only the images that stage/message actually supports (Create+PostOperation
  -> `.PostImage()` only; Delete+PostOperation -> `.PreImage()` only; PreValidation -> no image). Also encode the
  **logical order** in the type-state (message -> stage -> mode -> filtering -> images) so the chain forces the
  developer down the valid path. Trade-off: many more interfaces, and the loss of the `.Stage(enum)` ergonomics.
  **Keep the runtime check in `Build()` as a backstop** that validates *regardless of call order* (a config
  assembled out of order, or built by a path that bypasses the fluent typing, is still caught), and fix its
  message: `.PostImage()`/`.PreImage()` called **before** `.Stage()` currently reads the default `(PluginStages)0`
  as "stage '0'". Guards exist and are tested (`RoutingTests.StepBuilder_Disallows*`).
- **P3 - message-dependent `Execute` target is a redesign, not a type-state tweak.** For **Delete** /
  Associate / SetState the platform `Target` is an `EntityReference`, but `EntityPluginBase<TEntity>` exposes a
  single `Execute(ILocalPluginContext, TEntity target)` and `ResolveTarget` materializes a typed `TEntity`
  holding only the id. Typing `target` per message (an `EntityReference` for Delete, a full entity for
  Create/Update) is **not reachable with the current design**: the message/stage are chosen dynamically in
  `RegisterSteps()`, so the compiler cannot specialize `Execute`'s signature - it would need message/stage as
  **generic type parameters** of the base, or **per-message typed handlers** (`OnDelete(ctx, EntityReference)` vs
  `OnCreate(ctx, Account)`), i.e. a major redesign. Near-term: keep the uniform typed target (never null - a
  deliberate choice), document it on `EntityPluginBase.Execute`, and consider a typed helper (e.g.
  `ctx.TargetReference`) for reference messages. Related footgun: on PreValidation/PreOperation **Create** the
  record is not yet committed, so `target.Id` is `Guid.Empty` (the scaffolded `AccountPostCreatePlugin` uses
  `target.Id` and is PostOperation for that reason); a runtime trace/warning when `target.Id` is empty on a
  Create pre-stage would help.
