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

- **P3 — Security model in the harness (roles, privileges, business units)** — XrmMockup can test security
  roles / BU setup; Vela's harness deliberately has none (see LIMITATIONS). Reconsider a *minimal, opt-in*
  privilege model so plugins that branch on access rights (`RetrievePrincipalAccess`/`CheckAccess`, or
  privilege-denied paths) can be exercised. Must stay off by default — most plugin tests should not need it,
  and a full security simulation is a large surface. Scope carefully before committing; currently listed under
  "Won't do" — moving it here means it is up for reconsideration, not yet accepted.

  Enforcement gaps still open in the opt-in security model (from cross-checking the Dataverse "how record access
  is determined" / access-rights docs): **org-wide sharing** ("share with the entire organization") and team
  **Member's privilege inheritance** mode (Team-privileges-only vs Basic) are not yet modelled. (Append/AppendTo
  on lookups and clamping a share to the sharer's own rights are done.)

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
- **Security & identity model** — sharing (`GrantAccess`/`ModifyAccess`/`RevokeAccess`), column-level security
  (`IsSecured`/field security profiles), security-role templates, and Entra ID group teams. The in-memory harness
  has no privilege/security model on purpose, so these can't be meaningfully simulated and aren't plugin-authoring
  concerns. (`AssignRequest` ownership is supported as a plain `ownerid` update.) **Note:** a *minimal, opt-in*
  privilege model for the harness (roles/privileges/BU + access checks) is now up for reconsideration — see the
  P3 item under "Test harness" above. The broader sharing/FLS/role-template surface stays out of scope.
- **`vela models`**: relationship navigation properties + `OrganizationServiceContext`/LINQ (lazy loading +
  change tracking don't fit the plugin sandbox — use `Query.For<T>()`), localized multi-language labels, and
  custom class/property casing. For those, use `pac modelbuilder`.
- **Business-table specifics** (recurring-appointment expansion + master↔instance custom-column linking, queues,
  subjects, SLA, currency, catalog, feedback, templates) — data-model / app-builder concerns, not plugin-authoring.
  Any such table is generated/handled like any other; ActivityParty partylists are already typed (#65).
