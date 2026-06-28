# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list — keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## Quality & maintainability

- **Branch-coverage gates — at terminal point (was P1)** — CI enforces branch coverage (via `coverlet.msbuild`)
  on the diff engine (**90%**, actual ~92%), the in-memory harness (**87%**, actual ~89%) and the plugin runtime
  `Vela.Plugins` (**83%**, actual ~85%; line/method ~92%). The `Vela.Plugins` gate merges coverage from both
  `Vela.Tests` and `Vela.Testing.Tests` (collect one to JSON, `MergeWith` the other, threshold the combined
  result). All three are at their sensible terminal level — the practical, well-asserted branches are covered and
  the gates are tightened to ~2 pts below actual. **Parked residual (P3, low ROI):** the last few points to 90%
  branch on `Vela.Plugins` are a long tail of intricate/semi-unreachable paths — Custom API execution edge cases
  (`CustomApiBase`), `LocalPluginContext` reflection (telemetry logger, bulk-image type variants), runtime
  branches of `PluginContextExtensions`/`.Changes`, plus `EarlyBound` (~56%, internal). Only worth chasing if a
  hard 90% is required; otherwise these would test for the number, not robustness.
- **Mutation testing (Stryker.NET)** — **live via the weekly/on-demand `mutation.yml` workflow** (report in the
  run summary + HTML artifact). Diff engine (`DiffEngine` + `DefinitionNormalizer`) is at **~90%** mutation score
  (`--threshold-break 85`): the runs surfaced + fixed the unasserted diff ordering and pinned every normalized
  field value. A second job mutates the **in-memory harness query core** (`InMemoryQueryEngine`, `FetchXmlParser`,
  `EntityValues`) — `--threshold-break 0` until the first CI run sets the baseline (Stryker can't build net462 off
  Windows, so the harness can't be measured locally; both jobs run on `windows-latest`). **Next (P2):** raise the
  harness break threshold after the first run, and act on its survivors. Remaining diff-engine survivors are
  equivalent mutants (match-key field lookups consistent on both diff sides, functionally-identical literals).

## Project hygiene

- **P2 — nuget.org publishing path** — the packages are not yet published; document/automate the release.
- **P3 — Optional real-Dataverse integration test tier** (beyond the generate-then-build smoke test) to
  catch CLI regressions against a live dev org.

## Won't do (by design — see LIMITATIONS.md)

- Synchronous-only cache, no singleton DI lifetime, no runtime tracing-verbosity switch — Dataverse sandbox
  constraints.
- `MainOperation` reserved for Custom APIs; plugin stage/image rules — platform constraints.
- Multi-select option set as a Custom API parameter type — **not supported by Dataverse** (`customapifieldtype`
  has no such value), so nothing to implement.
- CLI managing solutions, security roles, web resources, environment variables — out of scope (solution
  tooling's job).
- **Security & identity model** — sharing (`GrantAccess`/`ModifyAccess`/`RevokeAccess`), access checks
  (`RetrievePrincipalAccess`/`RetrieveAccessRights`/`CheckAccess`), column-level security (`IsSecured`/field
  security profiles), security-role templates, and Entra ID group teams. The in-memory harness has no
  privilege/security model on purpose, so these can't be meaningfully simulated and aren't plugin-authoring
  concerns. (`AssignRequest` ownership is supported as a plain `ownerid` update.)
- **`vela models`**: relationship navigation properties + `OrganizationServiceContext`/LINQ (lazy loading +
  change tracking don't fit the plugin sandbox — use `Query.For<T>()`), localized multi-language labels, and
  custom class/property casing. For those, use `pac modelbuilder`.
- **Business-table specifics** (recurring-appointment expansion + master↔instance custom-column linking, queues,
  subjects, SLA, currency, catalog, feedback, templates) — data-model / app-builder concerns, not plugin-authoring.
  Any such table is generated/handled like any other; ActivityParty partylists are already typed (#65).
