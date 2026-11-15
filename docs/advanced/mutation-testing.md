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

- **Weekly and on demand:** every area (or, on a manual run, the areas named in the
  `areas` input: `gh workflow run mutation.yml -f areas=analyzers,plugins-steps`), on all of its files, gated by the area's
  `break` threshold.
- **On a pull request that touches `src/`:** only the areas whose files the PR changes,
  and only the mutants in the changed files (`--since:main`), gated at the **80**
  floor. An incremental run scores a handful of mutants, so it is gated at the floor
  rather than at the area's ratcheted threshold. A `plan` job picks the areas
  (`tests/stryker/select-areas.ps1`). A pull request that changes the shared setup
  (`tests/stryker/areas.json`, the selector, `mutation.yml` or the Stryker version
  pinned in `.config/dotnet-tools.json`) runs every area.

## Policy: measure-first, then ratchet

The gate is a **regression detector**, not a target to write tests for.

- **80% score floor.** Every gated core **measures ≥ 80%**. That is the minimum,
  never to be lowered below it.
- **Measure first, then gate ~2 points below.** Each core is measured before a
  threshold is set, then the enforced `--break-at` is pinned just under the measured
  value (about 2 points). It catches regressions without flaking on timeout-variance,
  and is tightened as the score rises. No gate is ever set below 80: a core measured
  less than 2 points above the floor is gated at 80 exactly.
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

In CI every job runs on a GitHub-hosted Windows runner (`windows-latest`, like `ci.yml`), one job per area, so the
areas run in parallel.

## Gated cores and scores

Scores from the full run on the former self-hosted Windows runner on 2026-10-11 (Stryker 4.15, every area on all
of its files), unless noted.

| Area | Core | Score | `break` |
| --- | --- | --- | --- |
| `diff-engine` | `DiffEngine` + `DefinitionNormalizer` | 87.0% | 85 |
| `harness-query` | `InMemoryQueryEngine`, `FetchXmlParser`, `EntityValues` | 94.1% | 92 |
| `security-enforcement` | `InMemoryOrganizationService.Security.cs` | 83.8% | 82 |
| `security-harness` | `Security/SecurityModel.cs` (+ sharing, access teams, 1:N) | 89.9% | 87 |
| `cli-apply` | `DataverseRegistration.Apply.cs` (web resources excluded) | 83.8% | 81 |
| `cli-customapi` | `DataverseRegistration.CustomApi.cs` | ~82.7% (earlier run) | 80 |
| `cli-reader` | `DataverseRegistration.cs` | 86.9% | 84 |
| `cli-steps` | `DataverseRegistration.Steps.cs` | 90.1% | 88 |
| `cli-scanner` | `PackageScanner.cs` | 84.2% | 82 |
| `security-cli` | `RoleGenerator.cs` + `RolePrivilegeMapper.cs` | 87.7% | 85 |
| `analyzers` | the whole `Vela.Analyzers` project (diagnostics + image projection generator) | ~86.3% (earlier run; timed out at 45 min on 2026-10-11) | 85 |
| `plugins-steps` | `Vela.Plugins/Steps` (generated `StepApi.cs` / `StepBuilder.Impl.cs` and the `SdkMessageNames.cs` data table excluded) | ~86% (net10 proxy; the 2026-10-11 run lost the runner) | 80 |
| `plugins-context` | `Vela.Plugins/Context` | 91.9% | 89 |
| `plugins-services` | `Vela.Plugins/DI`, `Caching`, `Localization` | 86.2% | 84 |
| `plugins-runtime` | `Vela.Plugins/Plugin` (PluginBase, EntityPluginBase, handler invocation) and `Retry` | 90.7% | 88 |
| `harness-store` | `InMemoryOrganizationService.cs` (the store core) | 89.9% | 87 |
| `harness-pipeline` | `PluginPipeline.cs`, `.Stages.cs`, `.Execution.cs` | 95.2% | 93 |

Notes on the scores:

- The **diff engine** runs surfaced and fixed the unasserted diff ordering and pinned
  every normalized field value.
- The **harness query core** and **security enforcement wiring** are net462 and so are
  measured only in CI.
- Some CLI methods carry a `// Stryker disable all` block where a real `ServiceClient`
  can't run offline (the `cli-reader` live-connection methods). The scanner's
  `BuildLocalStateFromPackages` used to as well; it is now driven end-to-end by
  `PackageScannerBuildStateTests` on a real compiled plugin fixture.
- The **#448 areas** (`plugins-runtime`, `harness-store`, `harness-pipeline`) measured 66.7-71.3% on a first
  Windows run before their mutation tests landed; the full run above has them at 89.9-95.2%. The **plugin runtime**
  areas used to carry estimates from a throwaway net10 build; `plugins-context`, `plugins-services` and
  `plugins-runtime` now have their Windows scores. `plugins-steps` still needs one (its 2026-10-11 job lost the
  runner), and so does `analyzers`, whose timeout went from 45 to 90 minutes, then to 180 after it ran out at 90
  twice on the former self-hosted runner (`diff-engine`, done in 28 of its 30 minutes, went to 60). The timeouts
  are unchanged on the GitHub-hosted runner until a full run there measures the areas again.
