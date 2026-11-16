# Contributing

This section is for people who build, test or contribute to the Vela framework itself. To use Vela, start at
[Getting started](../getting-started/install.md): you do not need to clone this repository.

- The full contributor guide (quality gates, coverage and mutation thresholds, the build smoke test, the release
  process) is [`CONTRIBUTING.md`](https://github.com/allandecastro/vela/blob/main/CONTRIBUTING.md) in the
  repository. AI coding agents get a summary in
  [`AGENTS.md`](https://github.com/allandecastro/vela/blob/main/AGENTS.md).
- [Architecture](../advanced/architecture.md) describes the solution layout and how a plugin flows through the
  framework.
- [Mutation testing](../advanced/mutation-testing.md) describes the Stryker gates.
- [Add a typed plugin message](extending-plugins.md) walks through the most common framework change.
- The [open issues](https://github.com/allandecastro/vela/issues) list work to pick up.

## Build and test

```bash
dotnet build Vela.sln -c Release
dotnet test Vela.sln -c Release      # Windows
```

The runtime, the harness and their tests target `net462`. `dotnet build` works on every OS, but `dotnet test` runs a
`net462` test project only on Windows. On macOS and Linux:

- run the `net10.0` projects (`Vela.Cli.Tests`, `Vela.Analyzers.Tests`, `Vela.CodeFixes.Tests`) with `dotnet test`;
- run the `net462` ones (`Vela.Tests`, `Vela.Testing.Tests`, `Vela.DocSamples`) with Mono and the xUnit console
  runner, as `CONTRIBUTING.md` shows. Mono can crash partway through these suites on macOS; when it does, rerun
  them, or run them on Windows.

## Regenerate the step API

The step builder surface (`src/Vela.Plugins/Steps/StepApi.cs`, `StepBuilder.Impl.cs` and `TypedTriggers.cs`) is
generated from `tools/codegen/Vela.StepApiGen/MessageModel.cs`. Change the model, then run:

```bash
dotnet run --project tools/codegen/Vela.StepApiGen
```

CI runs the generator with `-- --check` and fails when the committed files are stale. See
[Add a typed plugin message](extending-plugins.md).

## Run the build smoke test

The build smoke test (`Generate_ThenDotnetBuild_Succeeds`) packs the framework as version 1.0.0, scaffolds a
solution with `vela init` and builds it. CI always runs it; run it locally when the scaffold changes. Clear the cached
1.0.0 packages first, or the build compiles against an old framework:

```bash
rm -rf ~/.nuget/packages/vela.{plugins,testing,messages}/1.0.0
VELA_BUILD_SMOKE=1 dotnet test tests/Vela.Cli.Tests -c Release --filter "FullyQualifiedName~Generate_ThenDotnetBuild_Succeeds"
```

## Build the documentation

The documentation samples in `docs/samples/Vela.DocSamples` are part of `Vela.sln`: they build with the analyzers,
their tests run, and the pages include them with `[!code-csharp[](...#region)]`. Build the site locally with:

```bash
docfx docs/docfx.json --warningsAsErrors
```

The docs workflow runs the same command on every pull request that touches `docs/`, and it must report 0 warnings.
