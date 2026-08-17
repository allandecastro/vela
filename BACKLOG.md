# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list — keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md).

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
  guard) at **~64.7%** (`--break-at 62`), covered by driving every reconcile helper through the
  `IOrganizationService` seam / real nupkg+assembly fixtures. Stryker can't build net462 off Windows, so the harness
  can't be measured locally, but the net10 CLI cores **can** (`dotnet-stryker --mutate "**/<file>.cs"` from
  `tests/Vela.Cli.Tests`); all jobs run on the self-hosted Windows runner. **Next (P1), spread out — one CLI core
  per slice:** the Custom-API orchestration branches in `ApplyDiff` (paired with `DataverseRegistration.CustomApi.cs`),
  then `DataverseRegistration.cs`/`.Steps.cs` (the reader) and `PackageScanner.cs` — each gets its own `mutation.yml`
  job and ratcheted gate as coverage lands. The apply survivors left behind are equivalent (`LogOperation` arg
  strings, `?? "1.0.0.0"`/culture fallbacks, polling-loop `<`/`All`→`Any` bounds that need a 30s timeout to observe).
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

- **P3 — Remote debugging of cloud plugins** — let a developer step through a *real* Dataverse plugin
  execution from their machine. XrmFramework does this via an Azure Relay round-trip (client + generated
  proxy assembly). **We can likely do simpler:** explore a lighter mechanism (e.g. a relay/forwarder the
  plugin calls into only when a debug flag/shared-variable is set, or capturing the execution context to
  replay it locally against the in-memory harness) before committing to the full Azure Relay approach.
  Keep it opt-in and off by default so it never affects production registrations.
- **P3 — Modern C# in net462 plugin projects via PolySharp.** Plugins target .NET Framework 4.6.2, so the
  compiler blocks newer language features (records, `required`, init-only, file-scoped namespaces sometimes,
  etc.). Add [PolySharp](https://github.com/Sergio0694/PolySharp) (source-generated polyfills for the missing
  runtime attributes) to `vela init`'s scaffolded plugin projects so authors can use modern C# without runtime
  risk. Off the hot path (compile-time only), no effect on the produced assembly. (Idea seen in rezanid/xrmtools.)
- **Typed FetchXML — SHIPPED** (#46, #48, +long-tail): `Query.For<T>()` and any `QueryExpression` serialize to
  FetchXML with `ToFetchXml()` (no new builder — `TypedQuery` already had the full typed fluent API, so this is
  just the `FetchXmlWriter` serializer). `TracingOrganizationService` now logs the FetchXML string; `vela init`
  ships an example plugin + harness test. The FetchXML-only emit knobs QueryExpression can't hold are all in:
  `LateMaterialize()`, `AggregateLimit(n)`, `CountColumnDistinct(…)`, `OrderByAggregate(alias)`, `UtcDateGrouping()`,
  `RowAggregate(…)`, `RawChoiceOrder()`, `FromRetainedData()`, `SqlOptions("…")`.

- **P3 — nuget.org publishing path** — the packages are not yet published; document/automate the release.
- **P1 — A proper documentation wiki / site.** Grow beyond the (large) README into a navigable wiki with
  progressive complexity, mirroring what rezanid/xrmtools does well: **Getting started** (install, quickstart,
  first plugin), **Guides** (plugins, Custom APIs, `vela add`, DI, caching, testing with `Vela.Testing`, the
  security model, `vela models`/roles, `plan`/`apply`, signing), **Configuration** (auth, secrets, environments),
  **Advanced** (security-model deep dive, mutation testing, FetchXML builder once it lands), plus a **FAQ**. The
  existing README, `docs/LIMITATIONS.md`, `docs/Vela.Testing.md` and `CONTRIBUTING.md` are a strong base to split
  and expand. Decide the host (GitHub Wiki vs a docs site like DocFX/Docusaurus in `docs/`).
- **P2 — Optional real-Dataverse integration test tier** (beyond the generate-then-build smoke test) to
  catch CLI regressions against a live dev org.
- **P2 — Review the logo / branding.** Revisit the brand assets in `branding/` (chevron, wordmark, lockups,
  cards, slogan banners, favicon/tile) and `branding/DESIGN_SYSTEM.md` for consistency, quality and fit — and
  check every place they're surfaced (README header, NuGet package icon, docs) uses the right variant.

## Won't do (by design — see LIMITATIONS.md)

- Synchronous-only cache, no singleton DI lifetime, no runtime tracing-verbosity switch — Dataverse sandbox
  constraints.
- `MainOperation` reserved for Custom APIs; plugin stage/image rules — platform constraints.
- Multi-select option set as a Custom API parameter type — **not supported by Dataverse** (`customapifieldtype`
  has no such value), so nothing to implement.
- CLI **managing** (create/update/delete) solutions, security roles, web resources, environment variables — out of
  scope (solution tooling's job). Note: **reading** roles to generate a test-project `Roles` class (`vela models
  --roles`) is different and already shipped.
- **Entra ID (Azure AD) group teams** — out of scope for the harness security model (which now covers
  roles/privileges/BU/teams, sharing, 1:N cascades, access teams, manager **and** position hierarchy, and
  field-level security — see the "Test harness" section above). CLI **management** of security-role templates
  (create/update roles in an environment) also stays out of scope — that's solution-tooling's job; `vela models`
  only *reads* roles for the test-project generator.
- **`vela models`**: relationship navigation properties + `OrganizationServiceContext`/LINQ (lazy loading +
  change tracking don't fit the plugin sandbox — use `Query.For<T>()`), localized multi-language labels, and
  custom class/property casing. For those, use `pac modelbuilder`.
- **Business-table specifics** (recurring-appointment expansion + master↔instance custom-column linking, queues,
  subjects, SLA, currency, catalog, feedback, templates) — data-model / app-builder concerns, not plugin-authoring.
  Any such table is generated/handled like any other; ActivityParty partylists are already typed (#65).
