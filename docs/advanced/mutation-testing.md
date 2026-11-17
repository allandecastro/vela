---
_description: "The mutation gates, their thresholds and how to run an area locally"
---

# Mutation testing

This page explains how Vela uses mutation testing (Stryker.NET) to guard its correctness-critical code: what the
`mutation.yml` workflow gates, the measure-first policy behind each threshold, how to run an area locally, and the
current scores.

A mutant is a small deliberate change to the code. A surviving mutant means the tests still passed with the code
broken, so the mutation score is a sharper regression signal than branch coverage. For the coverage gate and the
build commands, see [Contributing](../contributing/index.md); for the project layout, see
[Architecture](architecture.md).

## What the gates cover

Two gate families guard the correctness-critical code, and both follow the same policy:

- **Branch coverage** (`ci.yml`): the diff engine, the analyzers, the code fixes, the in-memory harness and the
  plugin runtime (`Vela.Plugins`).
- **Mutation score** (`mutation.yml`, Stryker.NET): the diff engine and normalizer, the harness query core, store,
  pipeline and security model, the CLI cores, the analyzers and the plugin runtime (see
  [the table below](#gated-areas-and-scores)).

Each mutation-gated core is an **area**:

- `tests/stryker/areas.json` lists the areas: the test project each runs from, its timeout in minutes, and the
  source paths it covers.
- `tests/stryker/<area>.yml` is the area's Stryker configuration: the files it mutates, its thresholds, and why they
  sit where they do.

Stryker is pinned in `.config/dotnet-tools.json`, so a new Stryker release cannot move the scores by itself.

## How the workflow runs

The workflow has two modes. Each run reports into the job summary, lists the surviving mutants in the job log, and
uploads an HTML report.

- **Weekly (Mondays) and on demand:** every area on all of its files, gated by the area's `break` threshold. A
  manual run can name the areas to run with the `areas` input:
  `gh workflow run mutation.yml -f areas=analyzers,plugins-steps`.
- **On a pull request** that touches `src/`, `tests/stryker/`, `.config/dotnet-tools.json` or the workflow: only the
  areas whose files the pull request changes, and only the mutants in the changed files (`--since:main`), gated at
  the **80** floor. An incremental run scores a handful of mutants, so it is gated at the floor rather than at the
  area's own threshold. A `plan` job picks the areas with `tests/stryker/select-areas.ps1`. A pull request that
  changes the shared setup (`areas.json`, the selector, `mutation.yml` or `.config/dotnet-tools.json`) runs every
  area.

Every job runs on a GitHub-hosted Windows runner (`windows-latest`, like `ci.yml`), because the `net462` projects
build only on Windows. Each area is its own job, so the areas run in parallel. When the `WINDOWS_RUNNER` repository
variable names another runner (e.g. a self-hosted one), the jobs run there instead and pull requests from forks are
skipped.

## Policy: measure first, then ratchet

The gate detects regressions. It is not a target to write tests for.

- **80% is the floor.** Every gated area measures at least 80%, and no threshold is ever set below 80.
- **Measure first, then gate about 2 points below.** Each area is measured before its threshold is set. The
  enforced `break` is then pinned about 2 points under the measured score: it catches regressions without failing on
  timing variance, and it is raised as the score rises. An area measured less than 2 points above the floor is
  gated at 80.
- **Work-in-progress code is excluded, not counted.** A preview feature that is not mutation-gated yet (today, the
  web-resource code of the CLI) is wrapped in `// Stryker disable all : <reason>` ... `// Stryker restore all`.

When a run dips under its threshold, kill a surviving mutant (or add the missing test) to lift the score; do not
lower the gate. Many remaining survivors are equivalent mutants (exception-message strings, never-null or
never-empty edges, culture and version fallbacks). Kill the real ones, and do not chase message-text assertions.

## Run an area locally

Stryker builds `net462` projects only on Windows, so the harness and plugin-runtime areas run only on Windows. The
`net10.0` areas (the CLI cores and the analyzers) run anywhere. Run an area from its test project directory with its
configuration, or narrow it to one file:

```powershell
dotnet tool restore
cd tests/Vela.Cli.Tests
dotnet tool run dotnet-stryker --config-file ../stryker/diff-engine.yml
dotnet tool run dotnet-stryker --config-file ../stryker/cli-steps.yml --mutate "**/DataverseRegistration.Steps.cs" --break-at 0
# What a pull request runs: only the files changed since main
dotnet tool run dotnet-stryker --config-file ../stryker/diff-engine.yml --since:main --break-at 80
```

To add an area, add its `tests/stryker/<area>.yml` and its row in `tests/stryker/areas.json`, then measure it before
you choose its `break`.

## Gated areas and scores

Scores come from the full run of 2026-10-11 on the former self-hosted Windows runner (Stryker 4.15, every area on all
of its files), unless noted.

| Area | Code mutated | Score | `break` |
| --- | --- | --- | --- |
| `diff-engine` | `DiffEngine` and `DefinitionNormalizer` | 87.0% | 85 |
| `harness-query` | `InMemoryQueryEngine`, `FetchXmlParser`, `EntityValues` | 94.1% | 92 |
| `security-enforcement` | `InMemoryOrganizationService.Security.cs` | 83.8% | 82 |
| `security-harness` | `Security/SecurityModel.cs` and its partials, `SecurityConfiguration.cs`, `SharingTable.cs`, `AccessTeamGrants.cs`, `Fakes/OneToManyRegistry.cs` | 89.9% | 87 |
| `cli-apply` | `DataverseRegistration.Apply.cs`, its `Apply.*.cs` partials and `PluginTypeRemoval.cs` (web resources excluded) | 83.8% (before the split; the partials and `PluginTypeRemoval.cs` not measured yet) | 81 |
| `cli-customapi` | `DataverseRegistration.CustomApi.cs` | about 82.7% (earlier run) | 80 |
| `cli-reader` | `DataverseRegistration.cs` | 86.9% | 84 |
| `cli-steps` | `DataverseRegistration.Steps.cs` | 90.1% | 88 |
| `cli-scanner` | `PackageScanner.cs` | 84.2% | 82 |
| `security-cli` | `RoleGenerator.cs` and `RolePrivilegeMapper.cs` | 87.7% | 85 |
| `analyzers` | the whole `Vela.Analyzers` project (rules and image projection generator) | about 86.3% (earlier run) | 85 |
| `plugins-steps` | `Vela.Plugins/Steps`, except the generated `StepApi.cs` / `StepBuilder.Impl.cs` and the `SdkMessageNames.cs` data table | about 86% (`net10.0` proxy) | 80 |
| `plugins-context` | `Vela.Plugins/Context` | 91.9% | 89 |
| `plugins-services` | `Vela.Plugins/DI`, `Caching`, `Localization` | 86.2% | 84 |
| `plugins-runtime` | `Vela.Plugins/Plugin` (`PluginBase`, `EntityPluginBase`, handler invocation) and `Retry` | 90.7% | 88 |
| `harness-store` | `InMemoryOrganizationService.cs` and its message-family partials (the store core; not `.Security.cs` or `.ReadShape.cs`) | 89.9% | 87 |
| `harness-pipeline` | `PluginPipeline.cs`, `.Stages.cs`, `.Execution.cs` | 95.2% | 93 |

Notes on the scores:

- **`analyzers` and `plugins-steps` have no full Windows score yet.** Their 2026-10-11 jobs did not complete: the
  `analyzers` job timed out and the `plugins-steps` job lost the runner. The `analyzers` timeout has since gone from
  45 to 180 minutes, and `diff-engine` (done in 28 of its 30 minutes) went to 60. A full run on the GitHub-hosted
  runner will measure them, and may adjust the timeouts.
- **The `net462` areas** (the harness, security and plugin-runtime areas) are measured only on Windows.
- **Some CLI methods are excluded** with a `// Stryker disable all` block where a real `ServiceClient` cannot run
  offline (the live-connection methods of `cli-reader`).
- **The diff engine runs** surfaced and fixed an unasserted diff ordering, and pinned every normalized field value.
- **`plugins-runtime`, `harness-store` and `harness-pipeline`** measured 66.7% to 71.3% on a first Windows run,
  before their mutation tests landed. The full run above has them at 89.9% to 95.2%.
