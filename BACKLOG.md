# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## CLI

- **P3 - rare parallel-build pack race (needs a reliable repro first).** Once, the `VELA_BUILD_SMOKE` test's
  `restore --disable-parallel` then `build --no-restore` (parallel) sequence failed with `The process cannot
  access the file '...<Plugin>.1.0.0.nupkg' because it is being used by another process` - two plugin projects
  (`.Plugins` + `.Integrations`) `GeneratePackageOnBuild` into the shared `artifacts/` (`PackageOutputPath`).
  **Not reproducible so far** (10 clean runs, including the documented `dotnet build <sln> -c Release` *with*
  restore, which never raced - the flake was only on the test's `--no-restore` parallel path). The test is
  mitigated with `-m:1`. Because it can't be reproduced, any code fix would be unverifiable, so first capture an
  MSBuild binlog when it recurs to find the real cause (double-pack of one project vs. two writers), then fix
  (e.g. serialize the Pack target) rather than telling users to pass `-m:1`. User impact looks low: the
  documented command includes restore and did not race.
- **P3 - ribbon deploy end to end.** The classic `RibbonDiffXmlGenerator` (+ `RibbonSchema` validation) exists as
  a utility, but nothing wires the command bar automatically: registering a command on a button is left to the
  developer (Power Apps command designer, or a hand-authored `RibbonDiffXml`). Automating it would need a source
  of truth for the exported command functions (a build manifest from `vela-client`, or a scan) plus a Dataverse
  apply (modern `appaction` records / solution import). Deferred: low value versus letting the dev own the
  wiring. Design: `vela-client` `budapest/docs/plans/ribbon-command-bar.md` (PR-R4).

## Runtime (Vela.Plugins)

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
