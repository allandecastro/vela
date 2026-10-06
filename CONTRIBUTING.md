# Contributing to Vela

This guide is for people who want to **build, test, or contribute to the Vela framework itself**. If you
just want to *use* Vela to build Dataverse plugins and Custom APIs, see the [README](README.md) - you do
not need to clone this repository.

Looking for something to work on? See the [open issues](https://github.com/allandecastro/vela/issues) for planned and candidate work.

## Solution layout

```text
Vela.sln
src/
  Vela.Plugins/   # Framework runtime, contracts, primitives, and shared definitions
  Vela.Cli/       # CLI (command `vela`) for manifests and Dataverse sync
  Vela.Testing/   # Test harness package
tests/
  Vela.Tests/     # Unit tests
.github/workflows/ci.yml
```

## Architecture rules

Keep these rules in mind when adding new code:

1. **Core** (`Vela.Plugins`) contains framework-level runtime code, contracts, primitives, and generic immutable definitions.
2. **Tools** (`Vela.Cli`) contains the shared CLI used locally and in CI.
3. Generated early-bound model code belongs to the **consumer/plugin repo**, not this framework repo.
4. Use the framework APIs to build plugins and metadata generators; do not keep sample app code in the release path.
5. Keep registration metadata and runtime execution concerns separate.

## Build and test

```powershell
dotnet build Vela.sln -c Release
dotnet test Vela.sln -c Release
```

### Test stack and coverage

The test project uses:

1. `xUnit` for unit tests
2. `Moq` for mocks
3. `Microsoft.NET.Test.Sdk` for `dotnet test`
4. `coverlet.collector` for code coverage

Run coverage (Cobertura) with:

```powershell
dotnet test .\tests\Vela.Tests\Vela.Tests.csproj `
  -c Release `
  --collect:"XPlat Code Coverage"
```

CI enforces a **branch-coverage gate** (via `coverlet.msbuild`) on the two areas where edge-case coverage matters
most - the **diff engine** (plan/apply idempotence) and the **in-memory harness**. The run fails if branch
coverage drops below the threshold. Reproduce a gate locally, e.g. the diff engine:

```bash
dotnet test tests/Vela.Cli.Tests/Vela.Cli.Tests.csproj -c Release \
  -p:CollectCoverage=true -p:ThresholdType=branch -p:ThresholdStat=total -p:Threshold=88 \
  -p:Include="[Vela.Cli]Vela.Cli.DiffEngine%2c[Vela.Cli]Vela.Cli.DefinitionNormalizer"
```

Thresholds are set just below current coverage to catch regressions without churn; raise them as coverage improves.

### Quality gates

Two gate families guard the correctness-critical cores, and both follow the same policy:

- **Branch coverage** (`ci.yml`) - diff engine, in-memory harness, and the plugin runtime (`Vela.Plugins`).
- **Mutation score** (`mutation.yml`, Stryker.NET) - the diff engine + normalizer, and the harness query core.

**Policy - 80% floor, ratcheted above it.** Every gated core stays at **≥ 80%** on *both* metrics; `80` is a hard
minimum, never to be lowered below. On top of that floor the enforced threshold is **ratcheted to just under the
current measured value** (≈2 pts), so it catches regressions without flaking - and is tightened as the score rises.
Branch coverage is the easier metric (currently ~85-92%); mutation score is stricter (a surviving mutant means a
test passed even though the code was broken), so it sits lower (~80% harness, ~90% diff engine). Treat the gate as
a *regression detector*, not a target to write tests for: if a run dips under the floor, **kill a surviving
mutant** (or add the missing test) to lift the score rather than relaxing the gate.

The tools tests include an **opt-in build smoke test** (`Generate_ThenDotnetBuild_Succeeds`) that packs
`Vela.Plugins`, scaffolds a solution via `vela init`, and runs `dotnet build` on it - the only test that
actually compiles the generated C#. It is skipped unless enabled (it restores/builds, so it needs the SDK
and the Microsoft packages):

```powershell
$env:VELA_BUILD_SMOKE = "1"
dotnet test .\tests\Vela.Cli.Tests --filter Generate_ThenDotnetBuild_Succeeds
```

## Release

Release artifacts are produced by the release pipeline and should include only the framework packages
(`Vela.Plugins`, `Vela.Testing`) and the shared CLI tool (`Vela.Cli`).

The packages follow [Semantic Versioning](https://semver.org/) and ship as two independent release lines: the
**framework** (`Vela.Plugins` + `Vela.Testing`, versioned together) and the **CLI** (`Vela.Cli`). Record every
notable change in [`CHANGELOG.md`](CHANGELOG.md) under **Unreleased** as you merge it; cutting a release means
renaming that section (with a date) and pushing the line's tag - `framework-vX.Y.Z` or `cli-vX.Y.Z` - which
packs and publishes only that line. See the changelog's *Versioning policy* section for the pre-1.0 rules.
