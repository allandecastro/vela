# Mutation testing

Vela guards its correctness-critical cores with two gate families, and mutation
testing (Stryker.NET) is the stricter of the two. A surviving mutant means a test
passed even though the code was broken, so mutation score sits below branch coverage
and is the sharper regression signal. This page explains what the `mutation.yml`
workflow gates, the measure-first approach, what you can run locally, and the current
gated cores.

For the companion branch-coverage gate and the test stack, see
[Architecture](architecture.md).

## What `mutation.yml` gates

Two gate families guard the correctness-critical cores, and both follow the same
policy:

- **Branch coverage** (`ci.yml`) - diff engine, in-memory harness, and the plugin
  runtime (`Vela.Plugins`).
- **Mutation score** (`mutation.yml`, Stryker.NET) - the diff engine + normalizer, and
  the harness query core, plus the CLI reconciliation cores below.

The `mutation.yml` workflow runs weekly / on demand; each job reports into the run
summary and an HTML artifact.

## Policy: measure-first, then ratchet

The gate is a **regression detector**, not a target to write tests for.

- **80% score floor.** Every gated core **measures ≥ 80%**. That is the minimum,
  never to be lowered below it.
- **Gate ~2 points below the score.** The enforced `--break-at` is pinned just under
  the measured value (about 2 points), so it catches regressions without flaking on
  timeout-variance, and is tightened as the score rises. A core measured right at the
  floor therefore carries a gate just under 80 (e.g. `cli-apply` at ~80.1% / gate 78).
- **Measure first.** Each core was measured before a threshold was set, then the
  `--break-at` was pinned just under the measured score.

When a run dips under the floor, **kill a surviving mutant** (or add the missing test)
to lift the score rather than relaxing the gate. Many remaining survivors are
equivalent mutants (exception-message strings, never-null / never-empty edges,
culture / version fallbacks) - kill the real ones, don't chase message-text
assertions.

## Running it locally

Stryker can't build net462 off Windows, so the **harness is CI-only** - it can't be
measured locally. The net10 CLI cores **can** be measured locally, one file at a time,
from `tests/Vela.Cli.Tests`:

```powershell
dotnet-stryker --mutate "**/<file>.cs"
```

All jobs run on the self-hosted Windows runner.

## Gated cores and approximate scores

| Job | Core | Approx. score | `--break-at` |
| --- | --- | --- | --- |
| diff engine | `DiffEngine` + `DefinitionNormalizer` | ~90% | 85 |
| harness query core | `InMemoryQueryEngine`, `FetchXmlParser`, `EntityValues` | ~91.6% | 89 |
| security enforcement wiring | `InMemoryOrganizationService.Security.cs` | ~84.8% | 82 |
| security model | decision logic | - | - |
| `cli-apply` | `DataverseRegistration.Apply.cs` | ~80.1% | 78 |
| `cli-customapi` | `DataverseRegistration.CustomApi.cs` | ~82.7% | 80 |
| `cli-reader` | `DataverseRegistration.cs` | ~85.3% | 83 |
| `cli-steps` | `DataverseRegistration.Steps.cs` | ~89.4% | 87 |
| `cli-scanner` | `PackageScanner.cs` | ~82.6% | 80 |

Notes on the scores:

- The **diff engine** runs surfaced and fixed the unasserted diff ordering and pinned
  every normalized field value.
- The **harness query core** and **security enforcement wiring** are net462 and so are
  measured only in CI.
- Some CLI methods carry a `// Stryker disable all` block where a real `ServiceClient`
  can't run offline (the `cli-reader` live-connection methods) or where package code is
  loaded and executed (`BuildLocalStateFromPackages` in the scanner needs a compiled
  plugin-assembly fixture).

The remaining optional work (P2) is a compiled-plugin-fixture integration test to cover
`BuildLocalStateFromPackages` and the package-payload / partial-type-resolution branches
in `ApplyDiff` end-to-end.
