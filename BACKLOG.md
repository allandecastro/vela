# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list — keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## Quality & maintainability

- **Mutation testing (Stryker.NET)** — **live via the weekly/on-demand `mutation.yml` workflow** (report in the
  run summary + HTML artifact). Diff engine (`DiffEngine` + `DefinitionNormalizer`) is at **~90%** mutation score
  (`--break-at 85`): the runs surfaced + fixed the unasserted diff ordering and pinned every normalized
  field value. A second job mutates the **in-memory harness query core** (`InMemoryQueryEngine`, `FetchXmlParser`,
  `EntityValues`) at **~91.6%**, with `--break-at 89` (ratcheted ~2pts below the measured score). A third job gates
  the **security enforcement wiring** (`InMemoryOrganizationService.Security.cs`) at **~84.8%** (`--break-at 82`),
  and a fourth the **security model** decision logic. A fifth job (`cli-apply`) gates the **CLI apply
  reconciliation** (`DataverseRegistration.Apply.cs` — package/assembly/type sync, resume report, stale-version
  guard, and the `ApplyDiff` step/Custom-API orchestration incl. the ignorable-stage-fault skip) at **~69.5%**
  (`--break-at 67`), and a sixth (`cli-customapi`) the **CLI Custom API reconciliation**
  (`DataverseRegistration.CustomApi.cs` — customapi entity build, request/response parameter create/update/delete,
  type-code resolver) at **~82.7%** (`--break-at 80`), and a seventh (`cli-reader`) the **CLI plan reader**
  (`DataverseRegistration.cs` — reads assemblies/types/steps/Custom APIs out of Dataverse and maps them to the diff
  shape) at **~85.3%** (`--break-at 83`; the live-connection methods carry a `// Stryker disable all` block since a
  real `ServiceClient` can't run offline), and an eighth (`cli-steps`) the **CLI step reconciliation**
  (`DataverseRegistration.Steps.cs` — step entity build, lookup by key/signature, message/filter resolution,
  secure-config + state + image reconcile, stage/rank/name normalizers) at **~89.4%** (`--break-at 87`), and a
  ninth (`cli-scanner`) the **CLI package scanner** (`PackageScanner.cs` — extracts plugin `.nupkg`s, reads nuspec
  id/version, reflects over plugin/Custom-API types for their definitions) at **~82.6%** (`--break-at 80`;
  `BuildLocalStateFromPackages` carries a `// Stryker disable all` block — it `Assembly.LoadFrom`s and executes
  package code, so it needs a compiled plugin-assembly fixture) — all covered by driving every helper through the
  `IOrganizationService` seam / crafted `.nupkg`+stub-type fixtures. This completes the five CLI cores from the
  original measure-first run (the 752-survivor baseline). Stryker can't build net462 off Windows, so the harness
  can't be measured locally, but the net10 CLI cores **can** (`dotnet-stryker --mutate "**/<file>.cs"` from
  `tests/Vela.Cli.Tests`); all jobs run on the self-hosted Windows runner. **Every mutation core now measures
  ≥ 80%** (the former outlier `cli-apply` reached **80.1%** by covering the package-publish/type-wait path,
  partial type resolution via assembly sync, and the resume-report done++ boundaries); each gate sits ~2pts
  below its measured score as a regression margin. **Remaining (P2), optional:** a
  compiled-plugin-fixture integration test would cover `BuildLocalStateFromPackages` (scanner) and the
  package-payload / partial-type-resolution branches in `ApplyDiff` end-to-end. The survivors left behind are equivalent
  (`LogOperation` arg strings, `?? "1.0.0.0"`/culture fallbacks, static `ParameterSyncSpec` record literals,
  read-only `ColumnSet` columns, polling-loop `<`/`All`→`Any` bounds that need a 30s timeout to observe).
  The harness/security survivors that remain are mostly equivalent mutants
  (exception-message strings, never-null/never-empty edges) — kill the real ones, don't chase message-text
  assertions. Remaining diff-engine survivors are equivalent (match-key lookups consistent on both diff sides).

## Test harness (Vela.Testing)

- **Security model in the harness — SHIPPED** (opt-in, off by default): roles/privileges at the four access
  levels, business units, users and teams (incl. Member's privilege-inheritance mode), record sharing (PoA),
  1:N relationship cascades (delete/assign/share/reparent) and inherited access, **access teams** (team templates),
  **hierarchy security** (manager **and** position), **field-level security** (column security profiles), and
  **link-entity read-trimming** (a join to an unreadable record drops the primary row on an inner join or omits the
  linked columns on a left-outer, recursively). The enforcement wiring is isolated in
  `InMemoryOrganizationService.Security.cs` and mutation-gated (`--break-at 82`). Known gaps: cascaded child deletes
  are store-level for direct callers (they fire the child plugins through `PluginPipeline`); the `where`-on-an-
  unreadable-linked-column edge case is trimmed-before-filter by default and unverified against real Dataverse.


## Developer experience

- **Typed FetchXML — SHIPPED** (#46, #48, +long-tail): `Query.For<T>()` and any `QueryExpression` serialize to
  FetchXML with `ToFetchXml()` (no new builder — `TypedQuery` already had the full typed fluent API, so this is
  just the `FetchXmlWriter` serializer). `TracingOrganizationService` now logs the FetchXML string; `vela init`
  ships an example plugin + harness test. The FetchXML-only emit knobs QueryExpression can't hold are all in:
  `LateMaterialize()`, `AggregateLimit(n)`, `CountColumnDistinct(…)`, `OrderByAggregate(alias)`, `UtcDateGrouping()`,
  `RowAggregate(…)`, `RawChoiceOrder()`, `FromRetainedData()`, `SqlOptions("…")`.

- **P3 — nuget.org publishing path** — the packages are not yet published; document/automate the release.
- **Documentation site — SHIPPED**: a DocFX site under `docs/` (modern base + a custom Vela template overlay),
  published to GitHub Pages at <https://allandecastro.github.io/vela/>. ~26 pages across Getting started, Guides,
  Configuration, Advanced (architecture, mutation testing), Reference (limitations, FAQ) and Contributing, migrated
  from the README / `docs` / `CONTRIBUTING`. The README was slimmed to a shopfront pointing at the site, and the old
  `docs/LIMITATIONS.md` / `docs/Vela.Testing.md` were folded into `reference/limitations` and `guides/testing`.
- **P2 — Optional real-Dataverse integration test tier** (beyond the generate-then-build smoke test) to
  catch CLI regressions against a live dev org.
- **Branding refresh — SHIPPED**: replaced `branding/` with the organized pack (`symbol/`, `wordmark/`,
  `lockup/`, `stickers/`), aligned the symbol chevrons to the inverted V used across the pack, regenerated the
  PNG rasters (`icon.png`, `vela-chevron.svg.png`), updated `DESIGN_SYSTEM.md`, and repointed the README header +
  docs (navbar logo, favicon, ink hero lockup).
- **P2 — Generated `Roles` made dependency-light in `Shared.Models.Generated` — DONE (pending PR).** `vela models
  --roles` used to emit a `Roles` class whose configurator called `RoleBuilder`/`AccessLevel`/`Privilege` (test-only
  `Vela.Testing.Security`), so the file could not compile in the shared models project it was written to. Now the
  emitted code is BCL-only, so it lives with the table models and runtime plugin code can use `Roles.XName` /
  `Roles.XId` (no new project, no test dependency, no split). Shipped shape:
    - `Roles.XName` (`const string`) and `Roles.XId` (`static readonly Guid`) — usable by plugins, ConsoleApp, tests.
    - Grants as data: `public static readonly (string Entity, string Level, string Privilege)[] X = new[] { ("account",
      "BusinessUnit", "Read"), … };`. Entity stays a **string** (role grants span far more tables than are ever
      early-bound). Level/privilege are **strings** (typing them across the `Shared`(net462;net10.0) ↔
      `Vela.Testing`(net462) boundary would need a shared netstandard2.0 assembly — not worth a new project; the
      hand-written `RoleBuilder.Grant(entity, AccessLevel, Privilege)` path stays fully typed).
    - Harness overload `Role(string name, Guid id, params (string Entity, string Level, string Privilege)[] grants)`
      rehydrates the internal `RoleBuilder` (parses Level/Privilege → enums, throws on unknown). The `Action<RoleBuilder>`
      overloads remain for hand-authored roles.
    - Scaffolded `Shared.csproj` gains `System.ValueTuple` on its net462 target (4.6.2 lacks the in-box type; the
      tuple grants need it — compile-time reference only, forwards to mscorlib on the 4.7.2+ sandbox).
  Touched `RoleGenerator`, harness `SecurityModel`, `ModelsCommand` usage text, `ScaffoldTemplates`, docs
  (`models.md`/`security-model.md`/`limitations.md`), and the `RoleGenerator`/harness tests.

## Won't do (by design — see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html))

- Synchronous-only cache, no singleton DI lifetime, no runtime tracing-verbosity switch — Dataverse sandbox
  constraints.
- `MainOperation` reserved for Custom APIs; plugin stage/image rules — platform constraints.
- Multi-select option set as a Custom API parameter type — **not supported by Dataverse** (`customapifieldtype`
  has no such value), so nothing to implement.
- CLI **managing** (create/update/delete) solutions, security roles, web resources, environment variables — out of
  scope (solution tooling's job). Note: **reading** roles to generate a dependency-light `Roles` class in the shared
  models project (`vela models --roles`) is different and already shipped.
- **Entra ID (Azure AD) group teams** — out of scope for the harness security model (which now covers
  roles/privileges/BU/teams, sharing, 1:N cascades, access teams, manager **and** position hierarchy, and
  field-level security — see the "Test harness" section above). CLI **management** of security-role templates
  (create/update roles in an environment) also stays out of scope — that's solution-tooling's job; `vela models`
  only *reads* roles for the shared-models generator.
- **`vela models`**: relationship navigation properties + `OrganizationServiceContext`/LINQ (lazy loading +
  change tracking don't fit the plugin sandbox — use `Query.For<T>()`), localized multi-language labels, and
  custom class/property casing. For those, use `pac modelbuilder`.
- **Business-table specifics** (recurring-appointment expansion + master↔instance custom-column linking, queues,
  subjects, SLA, currency, catalog, feedback, templates) — data-model / app-builder concerns, not plugin-authoring.
  Any such table is generated/handled like any other; ActivityParty partylists are already typed (#65).
