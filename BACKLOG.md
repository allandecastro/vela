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
  `EntityValues`) at **~86.2%**, with `--break-at 84` (ratcheted ~2pts below the measured score, comfortably above
  the project's 80% floor). Stryker can't build net462 off Windows, so the harness can't be measured locally; both
  jobs run on the self-hosted Windows runner. **Next (P3):** keep killing the harness survivors dumped by the
  "List surviving mutants" step and ratchet the gate as the score climbs. Remaining diff-engine survivors are
  equivalent mutants (match-key field lookups consistent on both diff sides, functionally-identical literals).

## Test harness (Vela.Testing)

- **Security model in the harness — SHIPPED** (opt-in, off by default): roles/privileges at the four access
  levels, business units, users and teams (incl. Member's privilege-inheritance mode), record sharing (PoA),
  1:N relationship cascades (delete/assign/share/reparent) and inherited access, **access teams** (team templates),
  **hierarchy security** (manager **and** position), and **field-level security** (column security profiles). The
  enforcement wiring is isolated in `InMemoryOrganizationService.Security.cs` and mutation-gated (`--break-at 82`).
  Known gaps: cascaded child deletes are store-level for direct callers (they fire the child plugins through
  `PluginPipeline`).
  - **P3 — Link-entity (joined-table) read-trimming.** A `RetrieveMultiple` that joins (`LinkEntity`) to a table
    the caller can't read is filtered on the **primary** entity but **not** on the joined side: an inner join
    should drop the primary row when the joined record isn't readable, and an outer join should mask the joined
    columns — the harness does neither today (documented in `docs/LIMITATIONS.md`). Deferred: low frequency in
    plugin tests, and the inner/outer-join security semantics need validating before implementing (push the
    `CanAccess` read predicate into the query engine's link expansion in `InMemoryQueryEngine`).


## Developer experience

- **P2 — Remote debugging of cloud plugins** — let a developer step through a *real* Dataverse plugin
  execution from their machine. XrmFramework does this via an Azure Relay round-trip (client + generated
  proxy assembly). **We can likely do simpler:** explore a lighter mechanism (e.g. a relay/forwarder the
  plugin calls into only when a debug flag/shared-variable is set, or capturing the execution context to
  replay it locally against the in-memory harness) before committing to the full Azure Relay approach.
  Keep it opt-in and off by default so it never affects production registrations.

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
- **Field-level (column) security** (`IsSecured` + field security profiles), **hierarchy security**
  (manager/position), **access-team templates**, and **Entra ID group teams** — still out of scope for the harness
  security model (which now covers roles/privileges/BU/teams, sharing and 1:N cascades — see the "Test harness"
  section above). CLI **management** of security-role templates (create/update roles in an environment) also stays
  out of scope — that's solution-tooling's job; `vela models` only *reads* roles for the test-project generator.
- **`vela models`**: relationship navigation properties + `OrganizationServiceContext`/LINQ (lazy loading +
  change tracking don't fit the plugin sandbox — use `Query.For<T>()`), localized multi-language labels, and
  custom class/property casing. For those, use `pac modelbuilder`.
- **Business-table specifics** (recurring-appointment expansion + master↔instance custom-column linking, queues,
  subjects, SLA, currency, catalog, feedback, templates) — data-model / app-builder concerns, not plugin-authoring.
  Any such table is generated/handled like any other; ActivityParty partylists are already typed (#65).
