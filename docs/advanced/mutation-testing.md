# Mutation testing

Vela guards its correctness-critical cores with two gate families, and mutation
testing (Stryker.NET) is the stricter of the two. A surviving mutant means a test
passed even though the code was broken, so mutation score sits below branch coverage
and is the sharper regression signal. This page explains what the `mutation.yml`
workflow gates, the measure-first approach, what you can run locally, and the current
gated cores.

For the companion branch-coverage gate and the test stack, see
[Contributing](../contributing/index.md); for the project layout, see [Architecture](architecture.md).

## What `mutation.yml` gates

Two gate families guard the correctness-critical cores, and both follow the same
policy:

- **Branch coverage** (`ci.yml`) - diff engine, in-memory harness, plugin runtime
  (`Vela.Plugins`), analyzers and code fixes.
- **Mutation score** (`mutation.yml`, Stryker.NET) - the diff engine + normalizer, the
  harness query core and security model, the CLI cores, the analyzers and the plugin
  runtime (see the table below).

Each gated core is an **area**. `tests/stryker/areas.json` lists the areas (the test
project each runs from, its timeout, the source paths it covers), and
`tests/stryker/<area>.yml` is the area's Stryker configuration: the files it mutates,
its thresholds, and why they sit where they do. Stryker is pinned in
`.config/dotnet-tools.json`, so a new Stryker release cannot move the scores by itself.

The workflow runs in two modes, and each run reports into the job summary and an HTML
artifact:

- **Weekly and on demand:** every area, on all of its files, gated by the area's
  `break` threshold.
- **On a pull request that touches `src/`:** only the areas whose files the PR changes,
  and only the mutants in the changed files (`--since:main`), gated at the **80**
  floor. An incremental run scores a handful of mutants, so it is gated at the floor
  rather than at the area's ratcheted threshold. A `plan` job picks the areas
  (`tests/stryker/select-areas.ps1`).

## Policy: measure-first, then ratchet

The gate is a **regression detector**, not a target to write tests for.

- **80% score floor.** Every gated core **measures ≥ 80%**. That is the minimum,
  never to be lowered below it.
- **Measure first, then gate ~2 points below.** Each core is measured before a
  threshold is set, then the enforced `--break-at` is pinned just under the measured
  value (about 2 points). It catches regressions without flaking on timeout-variance,
  and is tightened as the score rises. No gate is ever set below 80: a core measured
  close to the floor is gated at 80 exactly (e.g. `cli-apply` at ~80.3% / gate 80).
- **Work-in-progress code is excluded, not counted.** A preview feature that is not
  mutation-gated yet (today: the web-resource branches of `apply`) is wrapped in
  `// Stryker disable all : <reason>` ... `// Stryker restore all` with the reason.

When a run dips under the floor, **kill a surviving mutant** (or add the missing test)
to lift the score rather than relaxing the gate. Many remaining survivors are
equivalent mutants (exception-message strings, never-null / never-empty edges,
culture / version fallbacks) - kill the real ones, don't chase message-text
assertions.

## Running it locally

Stryker can't build net462 off Windows, so the **harness and plugin-runtime areas are
CI-only** off Windows. The net10 areas (CLI cores, analyzers) can be measured anywhere.
Run an area from its test project directory with its configuration, or narrow it to
one file:

```powershell
dotnet tool restore
cd tests/Vela.Cli.Tests
dotnet tool run dotnet-stryker --config-file ../stryker/diff-engine.yml
dotnet tool run dotnet-stryker --config-file ../stryker/cli-steps.yml --mutate "**/DataverseRegistration.Steps.cs" --break-at 0
# What a pull request runs: only the files changed since main
dotnet tool run dotnet-stryker --config-file ../stryker/diff-engine.yml --since:main --break-at 80
```

To add an area, add its `tests/stryker/<area>.yml` and its row in
`tests/stryker/areas.json`, then measure it before choosing its `break`.

All jobs run on the self-hosted Windows runner (like `ci.yml`, to avoid GitHub Actions billing), so the areas run one after another.

## Gated cores and approximate scores

| Job | Core | Approx. score | `--break-at` |
| --- | --- | --- | --- |
| `diff-engine` | `DiffEngine` + `DefinitionNormalizer` | ~86.3% | 85 |
| harness query core | `InMemoryQueryEngine`, `FetchXmlParser`, `EntityValues` | ~91.6% | 89 |
| security enforcement wiring | `InMemoryOrganizationService.Security.cs` | ~84.8% | 82 |
| security model | `Security/SecurityModel.cs` (+ sharing, access teams, 1:N) | ~86.2% | 84 |
| `cli-apply` | `DataverseRegistration.Apply.cs` (web resources excluded) | ~80.3% | 80 |
| `cli-customapi` | `DataverseRegistration.CustomApi.cs` | ~82.7% | 80 |
| `cli-reader` | `DataverseRegistration.cs` | ~85.3% | 83 |
| `cli-steps` | `DataverseRegistration.Steps.cs` | ~89.4% | 87 |
| `cli-scanner` | `PackageScanner.cs` | ~81.6% | 80 |
| `security-cli` | `RoleGenerator.cs` + `RolePrivilegeMapper.cs` | ~89.4% | 80 |
| `analyzers` | the whole `Vela.Analyzers` project (diagnostics + image projection generator) | ~86.3% | 85 |
| `plugins-steps` | `Vela.Plugins/Steps` (generated `StepApi.cs` / `StepBuilder.Impl.cs` and the `SdkMessageNames.cs` data table excluded) | ~89% (net10 proxy, see note) | 80 |
| `plugins-context` | `Vela.Plugins/Context` | ~88% (net10 proxy, see note) | 80 |
| `plugins-services` | `Vela.Plugins/DI`, `Caching`, `Localization` | ~85% (net10 proxy, see note) | 80 |

Notes on the scores:

- The **diff engine** runs surfaced and fixed the unasserted diff ordering and pinned
  every normalized field value.
- The **harness query core** and **security enforcement wiring** are net462 and so are
  measured only in CI.
- Some CLI methods carry a `// Stryker disable all` block where a real `ServiceClient`
  can't run offline (the `cli-reader` live-connection methods). The scanner's
  `BuildLocalStateFromPackages` used to as well; it is now driven end-to-end by
  `PackageScannerBuildStateTests` on a real compiled plugin fixture.
- The **plugin runtime** areas are net462, so their scores above are estimates from a
  throwaway net10 build of `Vela.Plugins` and its tests (same mutate globs, both test
  projects), not the Windows run. Ratchet each area once its first Windows run is in.
