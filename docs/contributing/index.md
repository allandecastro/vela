# Contributing

This section is for people who **build, test or contribute to the Vela framework itself**. To *use* Vela, start at
[Getting started](../getting-started/install.md); you do not need to clone this repository.

The full contributor guide (quality gates, coverage and mutation thresholds, the build smoke test and the release
process) is [`CONTRIBUTING.md`](https://github.com/allandecastro/vela/blob/main/CONTRIBUTING.md) in the repository,
and AI coding agents get a summary in [`AGENTS.md`](https://github.com/allandecastro/vela/blob/main/AGENTS.md). See
[Architecture](../advanced/architecture.md) for the solution layout, and the
[open issues](https://github.com/allandecastro/vela/issues) for work to pick up.

## Build and test

```bash
dotnet build Vela.sln -c Release
dotnet test Vela.sln -c Release      # Windows
```

The runtime, the harness and their tests target `net462`, which `dotnet test` runs on Windows only. On macOS and
Linux, run the `net10.0` test projects with `dotnet test` and the `net462` ones with Mono and the xUnit console
runner (see `CONTRIBUTING.md`).

The step builder surface (`src/Vela.Plugins/Steps/StepApi.cs` and `StepBuilder.Impl.cs`) is generated from
`tools/codegen/Vela.StepApiGen/MessageModel.cs`: change the model, then run
`dotnet run --project tools/codegen/Vela.StepApiGen` (CI runs it with `-- --check`). See
[Add a typed plugin message](extending-plugins.md).

The build smoke test (`Generate_ThenDotnetBuild_Succeeds`) packs the framework as 1.0.0, scaffolds a solution with
`vela init` and builds it. It runs in CI; locally, clear the cached 1.0.0 packages first, or the build compiles
against an old framework:

```bash
rm -rf ~/.nuget/packages/vela.{plugins,testing,messages}/1.0.0
VELA_BUILD_SMOKE=1 dotnet test tests/Vela.Cli.Tests -c Release --filter "FullyQualifiedName~Generate_ThenDotnetBuild_Succeeds"
```

The documentation samples in `docs/samples/Vela.DocSamples` are part of `Vela.sln`: they build with the analyzers
and their tests run, and the pages include them with `[!code-csharp[](...#region)]`. Build the site locally with
`docfx docs/docfx.json --warningsAsErrors`, as the docs workflow does on every pull request.
