# Contributing

This guide is for people who want to **build, test, or contribute to the Vela framework itself**. If you
just want to *use* Vela to build Dataverse plugins and Custom APIs, start at
[Getting started](../getting-started/install.md) - you do not need to clone this repository.

Looking for something to work on? See the
[open issues](https://github.com/allandecastro/vela/issues) for planned and candidate work.

See [Architecture](../advanced/architecture.md) for the solution layout and architecture rules.

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
- **Mutation score** (`mutation.yml`, Stryker.NET) - the diff engine and normalizer, the CLI reconciliation and
  security cores, and the harness query core (see [Mutation testing](../advanced/mutation-testing.md) for the full
  gated set).

**Policy - 80% floor, ratcheted above it.** Every gated core stays at **≥ 80%** on *both* metrics; `80` is a hard
minimum, never to be lowered below. On top of that floor the enforced threshold is **ratcheted to just under the
current measured value** (≈2 pts), so it catches regressions without flaking - and is tightened as the score rises.
Branch coverage is the easier metric (currently ~85-92%); mutation score is stricter (a surviving mutant means a
test passed even though the code was broken), so it sits lower (~80% harness, ~90% diff engine). Treat the gate as
a *regression detector*, not a target to write tests for: if a run dips under the floor, **kill a surviving
mutant** (or add the missing test) to lift the score rather than relaxing the gate. See
[Mutation testing](../advanced/mutation-testing.md) for the gated cores and their scores.

The tools tests include an **opt-in build smoke test** (`Generate_ThenDotnetBuild_Succeeds`) that packs
`Vela.Plugins`, scaffolds a solution via `vela init`, and runs `dotnet build` on it - the only test that
actually compiles the generated C#. It always runs in CI; locally it is reported as skipped unless enabled
(it restores/builds, so it needs the SDK and the Microsoft packages):

```bash
rm -rf ~/.nuget/packages/vela.plugins ~/.nuget/packages/vela.testing ~/.nuget/packages/vela.messages   # see cache note below
VELA_BUILD_SMOKE=1 dotnet test tests/Vela.Cli.Tests --filter Generate_ThenDotnetBuild_Succeeds
```

> [!IMPORTANT]
> The smoke test packs the framework into a local feed at version **1.0.0**, but the global NuGet cache
> (`~/.nuget/packages/vela.plugins/1.0.0`, `.../vela.testing/1.0.0`, `.../vela.messages/1.0.0`) *shadows* that feed. If a stale 1.0.0 is
> cached, the generated code is compiled against an **old framework** and you get misleading errors. Delete those
> two cache folders before running the smoke test (or any manual compile of `vela init` / `vela add` output
> against the local framework) so restore picks up the freshly packed sources.

## Release

Release artifacts are produced by the release pipeline and should include only the framework packages
(`Vela.Plugins`, `Vela.Testing`) and the shared CLI tool (`Vela.Cli`).

The packages follow [Semantic Versioning](https://semver.org/) and ship as two independent release lines: the
**framework** (`Vela.Plugins` + `Vela.Testing`, versioned together) and the **CLI** (`Vela.Cli`). Record every
notable change in `CHANGELOG.md` under **Unreleased** as you merge it; cutting a release means
renaming that section (with a date) and pushing the line's tag - `framework-vX.Y.Z` or `cli-vX.Y.Z` - which
packs and publishes only that line. See the changelog's *Versioning policy* section for the pre-1.0 rules.
