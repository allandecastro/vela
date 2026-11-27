# AGENTS.md

Instructions for AI coding agents working on the Vela repository: a framework (`Vela.Plugins`), a CLI
(`Vela.Cli`, command `vela`) and a test harness (`Vela.Testing`) for Microsoft Dataverse plugins and Custom APIs.
Human contributors: [`CONTRIBUTING.md`](CONTRIBUTING.md). User docs: `docs/` (index for tools: [`docs/llms.txt`](docs/llms.txt)).

## Architecture map

| Path | TFM | What lives there |
| --- | --- | --- |
| `src/Vela.Plugins` | net462 | Runtime: `PluginBase` / `EntityPluginBase<T>` / `CustomApiBase`, the type-state step builder (`Steps/`), typed contexts (`Context/`), DI, caching, retry, localization, files. |
| `src/Vela.Plugins/Steps/StepImageRules.cs` | | The image property and stage rules per message, shared by the builder, VELA007, the harness and the CLI. |
| `src/Vela.Plugins/Steps/SdkMessageNames.cs` | | SDK request name to sdkmessage name (`SendEmail` registers on `Send`). |
| `src/Vela.Messages` | net462, net10.0 | `TypedRequest` / `TypedResponse`, `CustomApiRequest` / `CustomApiResponse`, typed CRUD requests. |
| `src/Vela.Analyzers` | netstandard2.0 | VELA001-VELA028 and the source generator for typed images and `{Handler}Context`. Rule ids in `DiagnosticIds.cs`; release tracking in `AnalyzerReleases.*.md`. |
| `src/Vela.CodeFixes` | netstandard2.0 | Code fixes, packed inside `Vela.Plugins`. |
| `src/Vela.Testing` | net462 | `InMemoryOrganizationService`, `PluginPipeline` (the public harness: stages, transactions, async steps, Custom APIs), the internal `PluginContext` (one plugin, for the framework's own tests). |
| `src/Vela.Cli` | net10.0 | `Program.cs` dispatches. `Cli/CliCommands.cs` is the command and option table that drives the help and each command's option allowlist. `PlanApplyCommand`, `DiffEngine`, `DataverseRegistration.*` (apply), `PackageScanner` / `IsolatedPackageScan` (the scan runs in a child process), `ProjectScaffolder` / `ScaffoldTemplates*` (`vela init`), `AddCommand` (`vela add`), `ModelsCommand` (`vela models`). |
| `tests/*` | | One test project per `src` project; `Vela.Tests` and `Vela.Testing.Tests` are net462, the rest net10.0. |
| `docs/` | | docfx site. `docs/samples/Vela.DocSamples` (net462, in `Vela.sln`) holds compiled snippets that the pages include. `docs/ai-layer/` (git-ignored) is vela-agentic's `docs/` at the tag in `docs/ai-layer.version`, filled by `docs/tools/Get-AgentsDocs.ps1`. |
| `tools/codegen/Vela.StepApiGen` | net10.0 | Generates the step API (below). |

Architecture rules (details in [docs/advanced/architecture.md](docs/advanced/architecture.md#architecture-rules)):
`Vela.Plugins` is the runtime and runs in the Dataverse sandbox; `Vela.Cli` is the one CLI used locally and in CI;
early-bound models belong to the consumer's repository; no sample application code in the shipped packages (samples
and fixtures live under `docs/samples` and `tests`); registration metadata and runtime execution meet only through
`StepDefinition`.

## Build and test

```bash
dotnet build Vela.sln -c Release
dotnet test tests/Vela.Cli.Tests -c Release            # net10.0 suites run anywhere with dotnet test
dotnet test tests/Vela.Analyzers.Tests -c Release
dotnet test tests/Vela.CodeFixes.Tests -c Release
```

The net462 suites (`Vela.Tests`, `Vela.Testing.Tests`, `Vela.DocSamples`) run with `dotnet test` on Windows only.
On macOS and Linux, build first, then run them with Mono and the xUnit console runner:

```bash
mono ~/.nuget/packages/xunit.runner.console/2.9.2/tools/net462/xunit.console.exe \
  tests/Vela.Tests/bin/Release/net462/Vela.Tests.dll tests/Vela.Testing.Tests/bin/Release/net462/Vela.Testing.Tests.dll \
  docs/samples/Vela.DocSamples/bin/Release/net462/Vela.DocSamples.dll
```

When the scaffold (`ProjectScaffolder`, `ScaffoldTemplates*`, `AddCommand`) changes, run the build smoke test. It
packs the framework as 0.0.0-local, so clear that version from the NuGet cache first:

```bash
rm -rf ~/.nuget/packages/vela.{plugins,testing,messages}/0.0.0-local
VELA_BUILD_SMOKE=1 dotnet test tests/Vela.Cli.Tests -c Release --no-build --filter "FullyQualifiedName~Generate_ThenDotnetBuild_Succeeds"
```

Docs: `pwsh docs/tools/Get-AgentsDocs.ps1` (the AI layer section), `dotnet tool restore`, then
`dotnet tool run docfx docs/docfx.json --warningsAsErrors` (DocFX is pinned in
`.config/dotnet-tools.json`) must report 0 warnings.
After adding, renaming or moving a docs page (give it a `_description` front matter) or changing a description, run
`dotnet run docs/tools/UpdateLlmsTxt.cs` and commit the regenerated `docs/llms.txt`.

## Generated step API

`StepApi.cs`, `StepBuilder.Impl.cs` and `TypedTriggers.cs` in `src/Vela.Plugins/Steps/` are generated from
`tools/codegen/Vela.StepApiGen/MessageModel.cs` (one line per message). Never edit them by hand:

```bash
dotnet run --project tools/codegen/Vela.StepApiGen              # regenerate
dotnet run --project tools/codegen/Vela.StepApiGen -- --check   # exit 1 if stale (CI runs this)
```

A new typed message touches more files: [docs/contributing/extending-plugins.md](docs/contributing/extending-plugins.md).

## CI gates

- `ci.yml` (GitHub-hosted `windows-latest`, fork pull requests included): build, the codegen `--check`, the build
  smoke test, then each test project once, with its branch-coverage gate on that same run: diff engine 90, harness
  87, runtime 83 (merged from `Vela.Tests` and `Vela.Testing.Tests`), analyzers 80, code fixes 82. A guard fails the
  job when a project referencing `Microsoft.NET.Test.Sdk` (including `docs/samples/Vela.DocSamples`) is not run.
- `mutation.yml` (GitHub-hosted `windows-latest`, one job per area, Stryker pinned in `.config/dotnet-tools.json`):
  one area per correctness core, configured in `tests/stryker/<area>.yml` and listed in `tests/stryker/areas.json`.
  Weekly on every file; on a pull request that touches `src/`, only the changed files of the affected areas
  (`--since:main`, break-at 80). Scores and break-at values:
  [docs/advanced/mutation-testing.md](docs/advanced/mutation-testing.md).
- The CLI/framework contract: `tests/ContractPluginFixture` (a real Vela.Plugins plugin) is checked on both sides
  against `tests/contracts/plugin-contract.golden.json`; a change to what a step or Custom API deploys updates the
  fixture and the golden file together.
- Policy: every gated core stays at **80% or more** on both coverage and mutation score. A threshold may be
  ratcheted up, never lowered below 80. If a run dips, kill the surviving mutant or add the missing test.
- Runners: the Windows jobs (`ci.yml`, `mutation.yml`, `docs.yml`, `release.yml`) run on `windows-latest` unless the
  `WINDOWS_RUNNER` repository variable names another runner as a JSON label array (e.g. `["self-hosted","Windows"]`);
  pull requests from forks are then skipped, and `changelog.yml` moves from `ubuntu-latest` to that runner too.
- `docs.yml`: on pull requests that touch `docs/`, `CHANGELOG.md` or `README.md`, checks that `docs/llms.txt` matches the tables of contents and
  builds docfx with `--warningsAsErrors`; deployment from `main`.

## Conventions

- **Commits and PRs carry no AI attribution**: no `Co-Authored-By` trailer for an AI, no "Generated with" line.
- **CHANGELOG**: every user-visible change gets an entry under `## [Unreleased]`, under the existing heading of
  its kind and line: `### Changed (Framework, breaking)`, `### Added (Framework)`, `### Changed (Framework)`,
  `### Fixed (Framework)`, `### Changed (CLI, breaking)`, `### Added (CLI)`, `### Changed (CLI)`, `### Fixed (CLI)`,
  `### Docs`. `changelog.yml` fails a PR that changes `src/`, `docs/`, `Directory.*.props` or a `NUGET.md` without adding a
  `- ` line under `## [Unreleased]`, unless it carries the `no-changelog` label (CI, tests, internal refactors).
- **Web resources are a work-in-progress preview** (`--webresources`, `DataverseRegistration.WebResources.cs`, the
  client-side scaffold): exclude them from coverage and mutation gates, and don't invest in them.
- **Plugin TFM**: the libraries target net462; the plugin projects scaffolded by `vela init` target net48.
- **Dataverse behaviour**: don't infer it from Microsoft docs alone; several documented behaviours were disproved on
  a live environment (images on `*Multiple`, `FilterOn` on Create, the owner/state Update split). Code comments and
  CHANGELOG entries mark live-verified facts ("verified live"); keep them and say so when a claim is unverified.
- **Every fix has a regression test** that asserts real behaviour, not just that nothing threw.
- **Docs**: a C# snippet on a canonical page lives in `docs/samples/Vela.DocSamples` and is included with
  `[!code-csharp[](...#region)]`. The `AGENTS.md` that `vela init` scaffolds
  (`src/Vela.Cli/Cli/Scaffold/ScaffoldTemplates.Agents.cs`) must match
  `docs/samples/Vela.DocSamples/AgentsGuideSamples.cs`; a test enforces it.
