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
| `src/Vela.Analyzers` | netstandard2.0 | VELA001-VELA024 and the source generator for typed images and `{Handler}Context`. Rule ids in `DiagnosticIds.cs`; release tracking in `AnalyzerReleases.*.md`. |
| `src/Vela.CodeFixes` | netstandard2.0 | Code fixes, packed inside `Vela.Plugins`. |
| `src/Vela.Testing` | net462 | `InMemoryOrganizationService`, `PluginContext` (one plugin), `PluginPipeline` (the whole pipeline: stages, transactions, async steps, Custom APIs). |
| `src/Vela.Cli` | net10.0 | `Program.cs` dispatches. `Cli/CliCommands.cs` is the command and option table that drives the help and each command's option allowlist. `PlanApplyCommand`, `DiffEngine`, `DataverseRegistration.*` (apply), `PackageScanner` / `IsolatedPackageScan` (the scan runs in a child process), `ProjectScaffolder` / `ScaffoldTemplates*` (`vela init`), `AddCommand` (`vela add`), `ModelsCommand` (`vela models`). |
| `tests/*` | | One test project per `src` project; `Vela.Tests` and `Vela.Testing.Tests` are net462, the rest net10.0. |
| `docs/` | | docfx site. `docs/samples/Vela.DocSamples` (net462, in `Vela.sln`) holds compiled snippets that the pages include. |
| `tools/codegen/Vela.StepApiGen` | net10.0 | Generates the step API (below). |

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
  tests/Vela.Tests/bin/Release/net462/Vela.Tests.dll tests/Vela.Testing.Tests/bin/Release/net462/Vela.Testing.Tests.dll
```

When the scaffold (`ProjectScaffolder`, `ScaffoldTemplates*`, `AddCommand`) changes, run the build smoke test. It
packs the framework as 1.0.0, so clear that version from the NuGet cache first:

```bash
rm -rf ~/.nuget/packages/vela.{plugins,testing,messages}/1.0.0
VELA_BUILD_SMOKE=1 dotnet test tests/Vela.Cli.Tests -c Release --no-build --filter "FullyQualifiedName~Generate_ThenDotnetBuild_Succeeds"
```

Docs: `docfx docs/docfx.json --warningsAsErrors` must report 0 warnings.

## Generated step API

`src/Vela.Plugins/Steps/StepApi.cs` and `StepBuilder.Impl.cs` are generated from
`tools/codegen/Vela.StepApiGen/MessageModel.cs` (one line per message). Never edit them by hand:

```bash
dotnet run --project tools/codegen/Vela.StepApiGen              # regenerate
dotnet run --project tools/codegen/Vela.StepApiGen -- --check   # exit 1 if stale (CI runs this)
```

A new typed message touches more files: [docs/contributing/extending-plugins.md](docs/contributing/extending-plugins.md).

## CI gates

- `ci.yml`: build, the codegen `--check`, every test suite, the build smoke test, and branch-coverage gates on the
  diff engine, the harness, the runtime, the analyzers and the code fixes.
- `mutation.yml`: Stryker mutation-score gates on the correctness-critical cores.
- Policy: every gated core stays at **80% or more** on both coverage and mutation score. A threshold may be
  ratcheted up, never lowered below 80. If a run dips, kill the surviving mutant or add the missing test.
- `docs.yml`: docfx with `--warningsAsErrors` on pull requests that touch `docs/`, deployment from `main`.

## Conventions

- **Commits and PRs carry no AI attribution**: no `Co-Authored-By` trailer for an AI, no "Generated with" line.
- **CHANGELOG**: every user-visible change gets an entry under `## [Unreleased]`, under the existing heading of
  its kind and line: `### Changed (Framework, breaking)`, `### Added (Framework)`, `### Changed (Framework)`,
  `### Fixed (Framework)`, `### Changed (CLI, breaking)`, `### Added (CLI)`, `### Fixed (CLI)`.
- **Web resources are a work-in-progress preview** (`--webresources`, `DataverseRegistration.WebResources.cs`, the
  client-side scaffold): exclude them from coverage and mutation gates, and don't invest in them.
- **Plugin TFM**: the libraries and the scaffolded plugin projects target net462. Plugin *packages* fail to
  register above net471, so never move a plugin project above net471, whatever Microsoft's docs recommend.
- **Dataverse behaviour**: don't infer it from Microsoft docs alone; several documented behaviours were disproved on
  a live environment (images on `*Multiple`, `FilterOn` on Create, the owner/state Update split). Code comments and
  CHANGELOG entries mark live-verified facts ("verified live"); keep them and say so when a claim is unverified.
- **Every fix has a regression test** that asserts real behaviour, not just that nothing threw.
- **Docs**: a C# snippet on a canonical page lives in `docs/samples/Vela.DocSamples` and is included with
  `[!code-csharp[](...#region)]`. The `AGENTS.md` that `vela init` scaffolds
  (`src/Vela.Cli/Cli/Scaffold/ScaffoldTemplates.Agents.cs`) must match
  `docs/samples/Vela.DocSamples/AgentsGuideSamples.cs`; a test enforces it.
