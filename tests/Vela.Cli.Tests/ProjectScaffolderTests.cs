using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class ProjectScaffolderTests
{
    // The local-dev <Version> of the repo's framework projects: the build smoke tests pack them at this version.
    private const string LocalFrameworkVersion = "0.0.0-local";

    private static ScaffoldOptions Options(string outputDir, bool console = true, bool integrations = true, bool tests = true, bool force = false, bool demos = true) => new(
        SolutionName: "ZavaSales",
        Prefix: "Zava",
        OutputDirectory: outputDir,
        IncludeConsole: console,
        IncludeIntegrations: integrations,
        IncludeTests: tests,
        CoreVersion: LocalFrameworkVersion,
        Force: force,
        Interactive: false,
        IncludeSamples: demos);

    [Fact]
    public void Generate_Minimal_WritesBareShellWithoutDemoContent()
    {
        RunInTempDir(temp =>
        {
            // A --minimal / --no-demos run: Shared + Plugins csproj and the structural files, but no sample
            // models/services/plugins/Custom APIs and none of the demo-only projects.
            int exit = ProjectScaffolder.Generate(Options(temp, console: false, integrations: false, tests: false, demos: false));
            Assert.Equal(ExitSuccess, exit);

            string root = Path.Combine(temp, "ZavaSales");

            // Structural files are still written, so the empty solution builds unedited.
            AssertFileExists(root, "ZavaSales.sln");
            AssertFileExists(root, "README.md");
            AssertFileExists(root, ".gitignore");
            AssertFileExists(root, "Directory.Build.props");
            AssertFileExists(root, "Directory.Packages.props");
            AssertFileExists(root, "src", "Zava.Shared", "Zava.Shared.csproj");
            AssertFileExists(root, "src", "Zava.Plugins", "Zava.Plugins.csproj");

            // No demo content.
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.Shared", "Models")), "minimal should not emit sample models");
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.Shared", "Services")), "minimal should not emit the sample service");
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.Plugins", "Account")), "minimal should not emit sample plugins");
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.Plugins", "Contact")), "minimal should not emit sample plugins");
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.Plugins", "GlobalCustomApis")), "minimal should not emit the sample global Custom API");

            // No demo-only projects.
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.Integrations")), "minimal should not emit the Integrations project");
            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.ConsoleApp")), "minimal should not emit the ConsoleApp project");
            Assert.False(Directory.Exists(Path.Combine(root, "tests")), "minimal should not emit the tests project");

            // The Plugins project holds only the empty assembly-wide registrar and message catalogue - no plugin type.
            string pluginsDir = Path.Combine(root, "src", "Zava.Plugins");
            Assert.Equal(
                ["Messages.cs", "ProjectServices.cs"],
                Directory.GetFiles(pluginsDir, "*.cs", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray());
        });
    }

    [Fact]
    public void Generate_Default_IsMinimal_WithAnEmptyTestProject()   // A-53
    {
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            string root = Path.Combine(temp, "ZavaSales");
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            // Exactly the structural files: no sample code, no Integrations/ConsoleApp, an empty test project.
            Assert.Equal(
                [
                    ".editorconfig",
                    ".gitignore",
                    ".vela/scaffold-manifest.json",
                    "AGENTS.md",
                    "Directory.Build.props",
                    "Directory.Packages.props",
                    "PROJECT.md",
                    "README.md",
                    "ZavaSales.sln",
                    "src/Zava.Plugins/Messages.cs",
                    "src/Zava.Plugins/ProjectServices.cs",
                    "src/Zava.Plugins/Zava.Plugins.csproj",
                    "src/Zava.Shared/Zava.Shared.csproj",
                    "tests/Zava.Plugins.Tests/Zava.Plugins.Tests.csproj",
                ],
                files);

            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Assert.Contains("vela init --samples", readme);
            Assert.Contains("vela add test MyPlugin --entity Account --project tests/Zava.Plugins.Tests", readme);
        });
    }

    [Fact]
    public void Generate_Integrations_WithoutSamples_AddsEmptyIntegrationsAndTestsProjects()
    {
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--integrations", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            string root = Path.Combine(temp, "ZavaSales");
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            // The minimal layout plus the two Integrations project files: no demo plugin, Custom API or test.
            Assert.Equal(
                [
                    ".editorconfig",
                    ".gitignore",
                    ".vela/scaffold-manifest.json",
                    "AGENTS.md",
                    "Directory.Build.props",
                    "Directory.Packages.props",
                    "PROJECT.md",
                    "README.md",
                    "ZavaSales.sln",
                    "src/Zava.Integrations/Messages.cs",
                    "src/Zava.Integrations/ProjectServices.cs",
                    "src/Zava.Integrations/Zava.Integrations.csproj",
                    "src/Zava.Plugins/Messages.cs",
                    "src/Zava.Plugins/ProjectServices.cs",
                    "src/Zava.Plugins/Zava.Plugins.csproj",
                    "src/Zava.Shared/Zava.Shared.csproj",
                    "tests/Zava.Integrations.Tests/Zava.Integrations.Tests.csproj",
                    "tests/Zava.Plugins.Tests/Zava.Plugins.Tests.csproj",
                ],
                files);

            string sln = File.ReadAllText(Path.Combine(root, "ZavaSales.sln"));
            Assert.Contains("src\\Zava.Integrations\\Zava.Integrations.csproj", sln);
            Assert.Contains("tests\\Zava.Integrations.Tests\\Zava.Integrations.Tests.csproj", sln);
            Assert.Contains("..\\..\\src\\Zava.Integrations\\Zava.Integrations.csproj", File.ReadAllText(Path.Combine(root, "tests", "Zava.Integrations.Tests", "Zava.Integrations.Tests.csproj")));
            Assert.DoesNotContain("SignPluginPackage", File.ReadAllText(Path.Combine(root, "src", "Zava.Integrations", "Zava.Integrations.csproj")));
            // An integration test builds an HttpClient around a fake handler; on net48 that needs the GAC facade.
            Assert.Contains("<Reference Include=\"System.Net.Http\" />", File.ReadAllText(Path.Combine(root, "tests", "Zava.Integrations.Tests", "Zava.Integrations.Tests.csproj")));

            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Assert.Contains("`src/Zava.Integrations` - a second plugin package", readme);
            Assert.Contains("`tests/Zava.Integrations.Tests`", readme);
            Assert.Contains("vela init <name> --managed-identity --force", readme);
        });
    }

    [Fact]
    public void Generate_ManagedIdentity_WithoutSamples_AddsAnEmptyIntegrationsProjectWithTheSigningStep()
    {
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--managed-identity", "--no-tests", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            string root = Path.Combine(temp, "ZavaSales");
            string integrations = Path.Combine(root, "src", "Zava.Integrations");
            string integrationsCsproj = File.ReadAllText(Path.Combine(integrations, "Zava.Integrations.csproj"));
            string pluginsCsproj = File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj"));
            Assert.Contains("SignPluginPackage", integrationsCsproj);
            Assert.DoesNotContain("SignPluginPackage", pluginsCsproj);
            // The signed package is named with the publisher prefix up front; the assembly keeps the project name.
            Assert.Contains("<PackageId>zava_Zava.Integrations</PackageId>", integrationsCsproj);
            Assert.Contains("<AssemblyName>Zava.Integrations</AssemblyName>", integrationsCsproj);
            // The Plugins package takes the same prefix.
            Assert.Contains("<PackageId>zava_Zava.Plugins</PackageId>", pluginsCsproj);
            Assert.Contains("<AssemblyName>Zava.Plugins</AssemblyName>", pluginsCsproj);
            Assert.Equal(
                ["Messages.cs", "ProjectServices.cs"],
                Directory.GetFiles(integrations, "*.cs", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToArray());
            Assert.False(Directory.Exists(Path.Combine(root, "tests")));

            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Assert.Contains("This solution wires **opt-in** NuGet signing into the build of the **`Zava.Integrations`** project", readme);
            Assert.Contains("vela sign --package artifacts/zava_Zava.Integrations.1.0.0.nupkg", readme);
            Assert.DoesNotContain("Integrations.Tests", readme);
        });
    }

    [Fact]
    public void Generate_PublisherPrefix_NamesTheSampleCustomApis_AndIsRecordedInProjectMdOnly()   // A-50, #575
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp) with { PublisherPrefix = "zs" }));

            string root = Path.Combine(temp, "ZavaSales");
            Assert.Contains("UniqueName = \"zs_AccountEstimateValue\";", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Account", "CustomApis", "AccountEstimateValueCustomApi.cs")));
            Assert.Contains("UniqueName = \"zs_Ping\";", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "GlobalCustomApis", "PingCustomApi.cs")));
            Assert.Contains("UniqueName = \"zs_IntegrationHeartbeat\";", File.ReadAllText(Path.Combine(root, "src", "Zava.Integrations", "GlobalCustomApis", "IntegrationHeartbeatCustomApi.cs")));
            // The prefix is recorded in PROJECT.md, never in a project file (#575): the CLI takes it as --prefix.
            Assert.Contains("\npublisherPrefix: zs\n", File.ReadAllText(Path.Combine(root, "PROJECT.md")));
            string[] csprojs = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
            Assert.NotEmpty(csprojs);
            Assert.All(csprojs, csproj => Assert.DoesNotContain("VelaPublisherPrefix", File.ReadAllText(csproj)));
        });
    }

    [Fact]
    public void Generate_Minimal_WithPublisherPrefix_NamesThePluginPackageWithIt()
    {
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["VpmiSales", "--prefix", "Vpmi", "--publisher-prefix", "vpmi", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            string root = Path.Combine(temp, "VpmiSales");
            string pluginsCsproj = File.ReadAllText(Path.Combine(root, "src", "Vpmi.Plugins", "Vpmi.Plugins.csproj"));
            Assert.Contains("<PackageId>vpmi_Vpmi.Plugins</PackageId>", pluginsCsproj);
            Assert.Contains("<AssemblyName>Vpmi.Plugins</AssemblyName>", pluginsCsproj);

            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Assert.Contains("vela add customapi MyApi --global --prefix vpmi --project src/Vpmi.Plugins", readme);
            string agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
            Assert.Contains("vela plan -p artifacts/vpmi_Vpmi.Plugins.1.0.0.nupkg --solution <solution of vpmi_Vpmi.Plugins> --json", agents);
        });
    }

    [Fact]
    public void Generate_Minimal_WithoutPublisherPrefix_KeepsTheUnprefixedPluginPackage()
    {
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            string root = Path.Combine(temp, "ZavaSales");
            Assert.Contains("<PackageId>Zava.Plugins</PackageId>", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj")));
            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Assert.Contains("vela add customapi MyApi --global --prefix <publisherprefix> --project src/Zava.Plugins", readme);
            Assert.Contains("The project values live in `PROJECT.md`", readme);
        });
    }

    [Fact]
    public void Generate_Samples_NamesThePackagesWithTheDerivedPublisherPrefix()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));

            string root = Path.Combine(temp, "ZavaSales");
            Assert.Contains("<PackageId>zava_Zava.Plugins</PackageId>", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj")));
            Assert.Contains("<PackageId>zava_Zava.Integrations</PackageId>", File.ReadAllText(Path.Combine(root, "src", "Zava.Integrations", "Zava.Integrations.csproj")));
            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Assert.Contains("vela plan --package artifacts/zava_Zava.Plugins.1.0.0.nupkg", readme);
            Assert.Contains("vela sign --package artifacts/zava_Zava.Integrations.1.0.0.nupkg", readme);
            Assert.Contains("Test with `PluginPipeline` by default; use `PluginContext` only for edge cases", readme);
        });
    }

    [Fact]
    public void Generate_WithoutPublisherPrefix_DoesNotRecordADerivedGuess()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));

            string root = Path.Combine(temp, "ZavaSales");
            Assert.DoesNotContain("VelaPublisherPrefix", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj")));
            Assert.Contains("UniqueName = \"zava_AccountEstimateValue\";", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Account", "CustomApis", "AccountEstimateValueCustomApi.cs")));
        });
    }

    [Fact]
    public void Force_KeepsUserCode_AndEditedSamples_ButRegeneratesUneditedFiles()   // A-52
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));
            string root = Path.Combine(temp, "ZavaSales");
            string plugins = Path.Combine(root, "src", "Zava.Plugins");

            // The user adds a plugin, edits one sample and deletes another.
            string userPlugin = Path.Combine(plugins, "Account", "Plugins", "MyOwnPlugin.cs");
            File.WriteAllText(userPlugin, "// my code");
            string editedSample = Path.Combine(plugins, "Contact", "Plugins", "ContactRouterPlugin.cs");
            File.WriteAllText(editedSample, "// my edit");
            string deletedSample = Path.Combine(plugins, "GlobalCustomApis", "PingCustomApi.cs");
            File.Delete(deletedSample);

            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, force: true)));

            Assert.Equal("// my code", File.ReadAllText(userPlugin));
            Assert.Equal("// my edit", File.ReadAllText(editedSample));
            // Compare ignoring line endings: the template's follow the compiled source (CRLF on a Windows checkout),
            // the written file is LF-normalised.
            Assert.Equal(ScaffoldTemplates.PingCustomApi("Zava").ReplaceLineEndings("\n"), File.ReadAllText(deletedSample).ReplaceLineEndings("\n"));

            // The edit stays recognised as an edit on the next run too (the manifest keeps the hash Vela wrote).
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, force: true)));
            Assert.Equal("// my edit", File.ReadAllText(editedSample));
        });
    }

    [Fact]
    public void Force_DroppingAProject_RemovesItsUneditedFiles_ButKeepsTheUsersOwn()   // A-52
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, integrations: true)));
            string integrations = Path.Combine(temp, "ZavaSales", "src", "Zava.Integrations");
            string userFile = Path.Combine(integrations, "Account", "Plugins", "Mine.cs");
            File.WriteAllText(userFile, "// mine");

            // Drive the write pass directly (no console capture, which would race the parallel tests).
            List<ProjectScaffolder.PlannedFile> plan = ProjectScaffolder.Plan(Options(temp, integrations: false, force: true), out _);
            ProjectScaffolder.WriteOutcome outcome = ProjectScaffolder.WritePlan(Path.Combine(temp, "ZavaSales"), plan);

            // Generated files are gone (including the csproj), the user's file and its folder survive, and the
            // solution no longer lists the project.
            Assert.False(File.Exists(Path.Combine(integrations, "Zava.Integrations.csproj")));
            Assert.False(Directory.Exists(Path.Combine(integrations, "GlobalCustomApis")), "an emptied folder is pruned");
            Assert.Equal("// mine", File.ReadAllText(userFile));
            Assert.DoesNotContain("Zava.Integrations.csproj", File.ReadAllText(Path.Combine(temp, "ZavaSales", "ZavaSales.sln")));

            string[] removed = outcome.Removed.ToArray();
            Assert.Contains("src/Zava.Integrations/Zava.Integrations.csproj", removed);
            Assert.DoesNotContain(removed, r => r.EndsWith("Mine.cs", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Force_WithoutAManifest_KeepsEveryExistingFileThatDiffers()   // A-52: a scaffold from an older CLI
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));
            string root = Path.Combine(temp, "ZavaSales");
            File.Delete(Path.Combine(root, ProjectScaffolder.ManifestPath));
            string csproj = Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj");
            File.WriteAllText(csproj, "<Project><!-- customised --></Project>");
            string integrationsCsproj = Path.Combine(root, "src", "Zava.Integrations", "Zava.Integrations.csproj");

            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, integrations: false, force: true)));

            // Unknown provenance: nothing is overwritten and nothing is deleted.
            Assert.Contains("customised", File.ReadAllText(csproj));
            Assert.True(File.Exists(integrationsCsproj));
            Assert.True(File.Exists(Path.Combine(root, ProjectScaffolder.ManifestPath)));
        });
    }

    [Fact]
    public void Force_TreatsALineEndingOnlyChange_AsUnedited()   // a git autocrlf checkout must not read as an edit
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));
            string sample = Path.Combine(temp, "ZavaSales", "src", "Zava.Plugins", "GlobalCustomApis", "PingCustomApi.cs");
            File.WriteAllText(sample, File.ReadAllText(sample).ReplaceLineEndings("\r\n"));

            List<ProjectScaffolder.PlannedFile> plan = ProjectScaffolder.Plan(Options(temp, force: true), out _);
            ProjectScaffolder.WriteOutcome outcome = ProjectScaffolder.WritePlan(Path.Combine(temp, "ZavaSales"), plan);

            Assert.DoesNotContain("src/Zava.Plugins/GlobalCustomApis/PingCustomApi.cs", outcome.Kept);
            Assert.Equal(ProjectScaffolder.Hash("a\nb\n"), ProjectScaffolder.Hash("a\r\nb\r\n"));
        });
    }

    [Fact]
    public void RunProcess_KillsAChildThatOutlivesItsTimeout_AndReportsItsOutput()
    {
        // A child that prints, then waits far longer than the limit: it must be killed and the failure must carry
        // what it printed, rather than hanging the run.
        (string file, string args) = OperatingSystem.IsWindows()
            ? ("powershell", "-NoProfile -Command \"Write-Output started; Start-Sleep -Seconds 60\"")
            : ("/bin/sh", "-c \"echo started; sleep 60\"");

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        Xunit.Sdk.FailException failure = Assert.Throws<Xunit.Sdk.FailException>(
            () => RunProcess(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(2)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(40), $"took {watch.Elapsed}");
        Assert.Contains("did not finish within", failure.Message);
        Assert.Contains("started", failure.Message);
    }

    [Fact]
    public void RunProcess_ClosesStdin_SoAReadingChildDoesNotBlock()
    {
        (string file, string args) = OperatingSystem.IsWindows()
            ? ("cmd", "/c \"set /p x= & echo done\"")
            : ("/bin/sh", "-c \"read x; echo done\"");

        (int exit, string output) = RunProcess(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(20));

        Assert.Contains("done", output);
        Assert.True(exit is 0 or 1, output); // `read` at end-of-file returns 1 on some shells; it must not hang
    }

    [Fact]
    public void SmokeReportReaders_CountFromTheXml_NotTheLocalizedConsole()
    {
        RunInTempDir(temp =>
        {
            string trx = Path.Combine(temp, "r.trx");
            File.WriteAllText(trx, "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><ResultSummary outcome=\"Completed\"><Counters total=\"3\" executed=\"3\" passed=\"3\" failed=\"0\" error=\"0\" /></ResultSummary></TestRun>");
            Assert.Equal((3, 0), ReadTrxCounts(trx));

            string xml = Path.Combine(temp, "r.xml");
            File.WriteAllText(xml, "<assemblies><assembly name=\"a.dll\" total=\"4\" passed=\"3\" failed=\"1\" skipped=\"0\" errors=\"0\" /></assemblies>");
            Assert.Equal((3, 1), ReadXunitCounts(xml));
        });
    }

    [Fact]
    public void Generate_WritesAnEditorConfig_ThatKeepsUsingsInsideTheNamespace()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, demos: false)));
            string root = Path.Combine(temp, "ZavaSales");

            // The folder-named namespaces (Zava.Plugins.Account.Plugins) make a using above the namespace resolve
            // Account to the namespace (CS0118): the IDE places usings inside, and the build warns on one outside.
            string editorConfig = File.ReadAllText(Path.Combine(root, ".editorconfig"));
            Assert.Contains("[*.cs]", editorConfig);
            Assert.Contains("csharp_using_directive_placement = inside_namespace:warning", editorConfig);
            Assert.Contains("csharp_style_namespace_declarations = block_scoped:suggestion", editorConfig);
            Assert.Contains("dotnet_diagnostic.IDE0065.severity = warning", editorConfig);
            Assert.Contains("Zava.Plugins.Account.Plugins", editorConfig);
            Assert.DoesNotContain("{{", editorConfig);
            // Not a root file: settings of an enclosing repository still apply where this one sets nothing.
            Assert.DoesNotContain("root = true", editorConfig);

            // The .editorconfig severities reach the build only with EnforceCodeStyleInBuild.
            Assert.Contains("<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));

            // Manifest-tracked like every generated file, so --force regenerates it only while unedited.
            using System.Text.Json.JsonDocument manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ProjectScaffolder.ManifestPath)));
            Assert.Equal(ProjectScaffolder.Hash(editorConfig), manifest.RootElement.GetProperty("files").GetProperty(".editorconfig").GetString());
        });
    }

    [Fact]
    public void Force_KeepsAUserEditedEditorConfig()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, demos: false)));
            string root = Path.Combine(temp, "ZavaSales");
            string path = Path.Combine(root, ".editorconfig");
            File.WriteAllText(path, "[*.cs]\nindent_size = 2\n");

            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, demos: false, force: true)));

            Assert.Equal("[*.cs]\nindent_size = 2\n", File.ReadAllText(path));
        });
    }

    [Fact]
    public void Generate_WritesAManifestOfEveryGeneratedFile()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));
            string root = Path.Combine(temp, "ZavaSales");

            using System.Text.Json.JsonDocument manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ProjectScaffolder.ManifestPath)));
            Dictionary<string, string> files = manifest.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

            string[] onDisk = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => f != ".vela/scaffold-manifest.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(onDisk, files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(ProjectScaffolder.Hash(File.ReadAllText(Path.Combine(root, "ZavaSales.sln"))), files["ZavaSales.sln"]);
        });
    }

    [Fact]
    public void Generate_CreatesExpectedLayout()
    {
        RunInTempDir(temp =>
        {
            int exit = ProjectScaffolder.Generate(Options(temp));
            Assert.Equal(ExitSuccess, exit);

            string root = Path.Combine(temp, "ZavaSales");
            AssertFileExists(root, "README.md");
            AssertFileExists(root, ".gitignore");
            // No nuget.config / local-packages: Vela resolves from nuget.org (the default source).
            Assert.False(File.Exists(Path.Combine(root, "nuget.config")), "scaffold should not emit a nuget.config");
            Assert.False(Directory.Exists(Path.Combine(root, "local-packages")), "scaffold should not emit a local-packages feed");
            AssertFileExists(root, "ZavaSales.sln");
            AssertFileExists(root, "Directory.Build.props");
            AssertFileExists(root, "Directory.Packages.props");

            AssertFileExists(root, "src", "Zava.Shared", "Zava.Shared.csproj");
            AssertFileExists(root, "src", "Zava.Shared", "Models", "Generated", "Account.cs");
            AssertFileExists(root, "src", "Zava.Shared", "Models", "Generated", "Contact.cs");
            AssertFileExists(root, "src", "Zava.Shared", "Services", "GreetingService.cs");

            AssertFileExists(root, "src", "Zava.Plugins", "Zava.Plugins.csproj");
            AssertFileExists(root, "src", "Zava.Plugins", "Account", "Plugins", "AccountPostCreatePlugin.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Account", "Plugins", "AccountRelatedContactsPlugin.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Account", "Plugins", "AccountMessageReference.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Account", "Services", "AccountFollowUpService.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Account", "CustomApis", "AccountEstimateValueCustomApi.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Contact", "Plugins", "ContactRouterPlugin.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Contact", "Services", "ContactDuplicateService.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "Contact", "CustomApis", "ContactValidateCustomApi.cs");
            AssertFileExists(root, "src", "Zava.Plugins", "GlobalCustomApis", "PingCustomApi.cs");

            AssertFileExists(root, "src", "Zava.Integrations", "Zava.Integrations.csproj");
            AssertFileExists(root, "src", "Zava.Integrations", "Account", "Plugins", "AccountSyncRouterPlugin.cs");
            AssertFileExists(root, "src", "Zava.Integrations", "Account", "CustomApis", "AccountReconcileCustomApi.cs");
            AssertFileExists(root, "src", "Zava.Integrations", "Contact", "CustomApis", "ContactExportCustomApi.cs");
            AssertFileExists(root, "src", "Zava.Integrations", "GlobalCustomApis", "IntegrationHeartbeatCustomApi.cs");

            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Security", "SecurityExampleTests.cs");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Account", "Plugins", "AccountPostCreatePluginTests.cs");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Account", "Plugins", "AccountRelatedContactsPluginTests.cs");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Account", "CustomApis", "AccountEstimateValueCustomApiTests.cs");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Contact", "Plugins", "ContactRouterPluginTests.cs");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "Contact", "CustomApis", "ContactValidateCustomApiTests.cs");
            AssertFileExists(root, "tests", "Zava.Plugins.Tests", "GlobalCustomApis", "PingCustomApiTests.cs");

            AssertFileExists(root, "tests", "Zava.Integrations.Tests", "Zava.Integrations.Tests.csproj");
            AssertFileExists(root, "tests", "Zava.Integrations.Tests", "Account", "CustomApis", "AccountReconcileCustomApiTests.cs");
            AssertFileExists(root, "tests", "Zava.Integrations.Tests", "Contact", "CustomApis", "ContactExportCustomApiTests.cs");
            AssertFileExists(root, "tests", "Zava.Integrations.Tests", "GlobalCustomApis", "IntegrationHeartbeatCustomApiTests.cs");

            AssertFileExists(root, "src", "Zava.ConsoleApp", "Zava.ConsoleApp.csproj");
            AssertFileExists(root, "src", "Zava.ConsoleApp", "Program.cs");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generate_EachPluginProject_DeclaresOneEmptyRegistrar_AndAnEmptyMessageCatalogue(bool samples)
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, integrations: true, demos: samples)));
            string root = Path.Combine(temp, "ZavaSales");

            using System.Text.Json.JsonDocument manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ProjectScaffolder.ManifestPath)));
            System.Text.Json.JsonElement tracked = manifest.RootElement.GetProperty("files");

            foreach (string project in new[] { "Zava.Plugins", "Zava.Integrations" })
            {
                string dir = Path.Combine(root, "src", project);
                string services = File.ReadAllText(Path.Combine(dir, "ProjectServices.cs"));
                string messages = File.ReadAllText(Path.Combine(dir, "Messages.cs"));

                // The registrar is declared at assembly scope, before the namespace, for this project's own type.
                Assert.StartsWith($"[assembly: Vela.DI.VelaServiceRegistrar(typeof({project}.ProjectServices))]", services);
                Assert.Contains($"namespace {project}\n{{", services.Replace("\r\n", "\n"));
                Assert.Contains("public sealed class ProjectServices : IServiceRegistrar", services);
                Assert.Contains("public void Register(SimpleServiceResolver services, ILocalPluginContext context)", services);
                Assert.Contains("apply to every\n    /// plugin and Custom API of this assembly, before each one's own <c>ConfigureServices</c>", services.Replace("\r\n", "\n"));

                // Registers nothing by default: the message source line is a comment until Messages.cs has a key.
                Assert.Contains("// context.GetRequiredService<IMessageService>().AddSource(ProjectMessages.Source);", services);
                Assert.DoesNotMatch(@"(?m)^\s*context\.", services);
                Assert.DoesNotMatch(@"(?m)^\s*services\.", services);

                Assert.Contains($"namespace {project}\n{{", messages.Replace("\r\n", "\n"));
                Assert.Contains("public static class ProjectMessages", messages);
                Assert.Contains("public static readonly IMessageSource Source = new DictionaryMessageSource();", messages);
                Assert.DoesNotMatch(@"(?m)^\s*public const string", messages);

                // `using` directives sit inside the namespace block, like every other generated source file.
                foreach (string source in new[] { services, messages })
                {
                    Assert.True(source.IndexOf("using ", StringComparison.Ordinal) > source.IndexOf("namespace ", StringComparison.Ordinal), "usings must be inside the namespace");
                }

                // Exactly one registrar attribute per assembly, the samples included (the attribute allows one).
                int declarations = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                    .Sum(file => Regex.Matches(File.ReadAllText(file), @"\[assembly:\s*Vela\.DI\.VelaServiceRegistrar").Count);
                Assert.Equal(1, declarations);

                // Tracked by the manifest, so `vela init --force` regenerates them only while unedited.
                Assert.True(tracked.TryGetProperty($"src/{project}/ProjectServices.cs", out _));
                Assert.True(tracked.TryGetProperty($"src/{project}/Messages.cs", out _));
            }
        });
    }

    [Fact]
    public void Force_KeepsAnEditedRegistrar_AndRestoresADeletedMessageCatalogue()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, demos: false)));
            string dir = Path.Combine(temp, "ZavaSales", "src", "Zava.Plugins");
            string services = Path.Combine(dir, "ProjectServices.cs");
            string edited = File.ReadAllText(services).Replace("// context.GetRequiredService", "context.GetRequiredService");
            File.WriteAllText(services, edited);
            File.Delete(Path.Combine(dir, "Messages.cs"));

            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, demos: false, force: true)));

            Assert.Equal(edited, File.ReadAllText(services));
            Assert.Equal(ScaffoldTemplates.ProjectMessagesFile("Zava.Plugins").Replace("\r\n", "\n"), File.ReadAllText(Path.Combine(dir, "Messages.cs")).Replace("\r\n", "\n"));
        });
    }

    [Fact]
    public void Generate_DocumentationFile_IsOnInThePluginAndSharedProjects_ButNotInTheTests()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, integrations: true)));
            string root = Path.Combine(temp, "ZavaSales");
            string Read(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts]));

            // The plugin projects check their comments: CS1591 stays a plain warning, neither silenced nor an error.
            foreach (string plugin in new[] { "Zava.Plugins", "Zava.Integrations" })
            {
                string csproj = Read("src", plugin, $"{plugin}.csproj");
                Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", csproj);
                Assert.DoesNotMatch(@"<NoWarn>[^<]*1591", csproj);
                Assert.DoesNotContain("<WarningsAsErrors>", csproj);
                Assert.DoesNotContain("<TreatWarningsAsErrors>", csproj);
            }

            // Shared emits its documentation for IntelliSense but silences CS1591: `vela models` writes there, and its
            // models, messages and roles do not document every member.
            string shared = Read("src", "Zava.Shared", "Zava.Shared.csproj");
            Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", shared);
            Assert.Contains("<NoWarn>$(NoWarn);CS1591</NoWarn>", shared);

            Assert.DoesNotContain("GenerateDocumentationFile", Read("tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj"));
            Assert.DoesNotContain("GenerateDocumentationFile", Read("tests", "Zava.Integrations.Tests", "Zava.Integrations.Tests.csproj"));
            Assert.DoesNotContain("GenerateDocumentationFile", Read("Directory.Build.props"));
            Assert.DoesNotContain(".vela-runs", Read(".gitignore"));
        });
    }

    [Fact]
    public void Generate_WritesAnAgentsGuide_ForThisSolution()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));

            string agents = File.ReadAllText(Path.Combine(temp, "ZavaSales", "AGENTS.md"));

            // The step, Custom API and test patterns, each with the current API.
            Assert.Contains("## Write a step", agents);
            Assert.Contains("RegisterStep().OnUpdate().PreOperation().Sync()", agents);
            Assert.Contains(".FilterOn(a => a.CreditLimit)", agents);
            Assert.Contains(".WithStepKey(\"credit-review\")", agents);
            Assert.Contains("ctx.Service", agents);
            Assert.Contains("ctx.SystemService", agents);
            Assert.Contains("ctx.AsUser(userId)", agents);
            Assert.Contains("## Write a Custom API", agents);
            Assert.Contains("RegisterCustomApi(\"zava_EstimateValue\")", agents);
            Assert.Contains("## Write a test", agents);
            Assert.Contains("PluginPipeline.Empty().Register<AccountCreditPlugin>()", agents);
            Assert.Contains("Test with `PluginPipeline` by default; use `PluginContext` only for edge cases", agents);

            // The CLI loop on this solution's own names, always with --solution.
            Assert.Contains("dotnet build ZavaSales.sln -c Release", agents);
            Assert.Contains("dotnet test tests/Zava.Plugins.Tests -c Release", agents);
            Assert.Contains("vela plan  --packages-dir artifacts --solution <solution> --json", agents);
            Assert.Contains("vela apply --packages-dir artifacts --solution <solution>", agents);
            Assert.Contains("vela add customapi EstimateValue --global --function --prefix <publisherPrefix> --project src/Zava.Plugins/Zava.Plugins.csproj", agents);
            Assert.Contains("--project src/Zava.Plugins/Zava.Plugins.csproj", agents);
            Assert.DoesNotContain("{{", agents);

            // The guide never tells an agent how to skip the apply confirmation.
            Assert.DoesNotContain("--auto-approve", agents);
            Assert.Contains("asks for confirmation - run only when the user asks", agents);

            // Each C# block carries its usings, inside the namespace the scaffold gives its folder, so it compiles
            // pasted into a file `vela add` created: above the namespace, the Account segment would hide the model.
            string lf = agents.Replace("\r\n", "\n");
            Assert.Contains("namespace Zava.Plugins.Account.Plugins\n{\n    using Microsoft.Xrm.Sdk;", lf);
            Assert.Contains("namespace Zava.Plugins.Tests.Account.Plugins\n{", lf);
            Assert.Contains("    using Vela.Messages;", agents);
            Assert.Contains("    using Zava.Shared.Models.Generated;", agents);
            Assert.Contains("    using Zava.Plugins.Account.Plugins;", agents);
            Assert.DoesNotContain("\nusing ", lf);
            Assert.Contains("Put the `using` directives inside a block `namespace { }`", agents);
            Assert.Contains("(error CS0118)", agents);

            // The conventions: behaviour-named handlers, the project-wide registrar, documented public types.
            Assert.Contains("`--handler <Method>`", agents);
            Assert.Contains("`ProjectServices.cs`", agents);
            Assert.Contains("https://allandecastro.github.io/vela/guides/localization.html", agents);
            Assert.Contains("`/// <summary>`", agents);
        });
    }

    [Fact]
    public void AgentsGuide_Snippets_AreTheCompiledDocSamples()
    {
        // Every top-level member (class, test method) of the guide's C# blocks must appear in
        // docs/samples/Vela.DocSamples/AgentsGuideSamples.cs as one contiguous run of lines, and the members in the
        // guide's order. CI compiles and runs that file with the analyzers, so the guide cannot drift from code that
        // builds: a reordered, missing or extra line in a member fails here (PR #487 review).
        string? pluginsProject = FindRepoProject("Vela.Plugins");
        Assert.NotNull(pluginsProject);
        string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pluginsProject)!, "..", ".."));
        string[] compiled = NormalizedLines(File.ReadAllText(Path.Combine(repoRoot, "docs", "samples", "Vela.DocSamples", "AgentsGuideSamples.cs")));

        string guide = ScaffoldTemplates.AgentsGuide(Options("unused"));
        string[][] members = GuideCSharpMembers(guide);
        string[] code = [.. compiled.Where(line => !IsStructuralLine(line))];

        Assert.NotEmpty(members);
        int searchFrom = 0;
        foreach (string[] member in members)
        {
            int at = IndexOfRun(code, member, searchFrom);
            Assert.True(at >= 0, $"This AGENTS.md snippet is not, in order and contiguous, in AgentsGuideSamples.cs:\n{string.Join("\n", member)}");
            searchFrom = at + member.Length;
        }

        // Each guide block is one namespace block of the sample, which declares exactly that block's usings (the
        // sample's models namespace standing for the scaffold's): the compiled sample proves the usings suffice, so a
        // block pasted into a `vela add` file compiles without the reader guessing its imports.
        string[][] guideUsings = [.. GuideCSharpBlocks(guide).Select(block => UsingsOf(block)
            .Select(u => u.Replace("Zava.Shared.Models.Generated", "Vela.DocSamples.Models")).ToArray())];
        string[][] sampleUsings = [.. SampleNamespaceBlocks(string.Join("\n", compiled)).Select(UsingsOf)];

        // Each guide block is a block namespace named as the scaffold names that folder, the same as the sample's.
        string[] guideNamespaces = [.. GuideCSharpBlocks(guide).Select(block => block.TrimStart('\n').Split('\n')[0])];
        Assert.All(guideNamespaces, ns => Assert.StartsWith("namespace Zava.Plugins.", ns, StringComparison.Ordinal));
        Assert.Equal(compiled.Where(line => line.StartsWith("namespace ", StringComparison.Ordinal)), guideNamespaces);
        Assert.Equal(sampleUsings.Length, guideUsings.Length);
        for (int i = 0; i < guideUsings.Length; i++)
        {
            Assert.NotEmpty(guideUsings[i]);
            Assert.Equal(sampleUsings[i].OrderBy(u => u, StringComparer.Ordinal), guideUsings[i].OrderBy(u => u, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void AgentsGuide_drift_check_rejects_a_block_missing_a_using()
    {
        string[] sample = [.. SampleNamespaceBlocks("namespace N\n{\n    using System;\n    using Vela.Messages;\n    class A { }\n}\n").Select(UsingsOf).Single()];
        string[] guide = UsingsOf(GuideCSharpBlocks("```csharp\nusing System;\n\nclass A { }\n```").Single());

        Assert.Equal(["using System;", "using Vela.Messages;"], sample);
        Assert.NotEqual(sample, guide);
    }

    private static string[] GuideCSharpBlocks(string guide)
        => [.. guide.Replace("\r\n", "\n").Split("```").Where(b => b.StartsWith("csharp", StringComparison.Ordinal)).Select(b => b.Substring("csharp".Length))];

    // The sample file's top-level namespace declarations, in order.
    private static string[] SampleNamespaceBlocks(string sample)
        => [.. Regex.Split(sample.Replace("\r\n", "\n"), @"^namespace \S+\n", RegexOptions.Multiline).Skip(1)];

    private static string[] UsingsOf(string code)
        => [.. code.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("using ", StringComparison.Ordinal) && line.EndsWith(";", StringComparison.Ordinal))];

    [Fact]
    public void AgentsGuide_drift_check_rejects_a_reordered_snippet()
    {
        // The check above must catch a guide whose lines all exist in the sample but in another order.
        string[] compiled = NormalizedLines("class A\n{\n    .FilterOn(a => a.X)\n    .Handle(On);\n}\n");
        string[][] reordered = GuideCSharpMembers("```csharp\nclass A\n{\n    .Handle(On);\n    .FilterOn(a => a.X)\n}\n```");
        string[][] same = GuideCSharpMembers("```csharp\nclass A\n{\n    .FilterOn(a => a.X)\n    .Handle(On);\n}\n```");

        string[] code = [.. compiled.Where(line => !IsStructuralLine(line))];
        Assert.Equal(-1, IndexOfRun(code, Assert.Single(reordered), 0));
        Assert.Equal(0, IndexOfRun(code, Assert.Single(same), 0));
    }

    private static string[] NormalizedLines(string text)
        => text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();

    // A namespace declaration, a using directive or a lone brace: compared separately (namespaces, usings) or not at
    // all (braces), so a sample may close a class after an extra member the guide leaves out.
    private static bool IsStructuralLine(string trimmed)
        => trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || (trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.EndsWith(";", StringComparison.Ordinal))
            || trimmed is "{" or "}";

    // The guide's ```csharp blocks split into paragraphs (runs of non-blank lines), structural lines left out.
    private static string[][] GuideCSharpMembers(string guide)
    {
        List<string[]> members = [];
        string[] blocks = guide.Replace("\r\n", "\n").Split("```");
        foreach (string block in blocks.Where(b => b.StartsWith("csharp", StringComparison.Ordinal)))
        {
            List<string> current = [];
            bool previousBlank = true;
            foreach (string line in block.Substring("csharp".Length).Split('\n'))
            {
                string trimmed = line.Trim();
                if (IsStructuralLine(trimmed))
                {
                    continue;
                }

                bool blank = trimmed.Length == 0;
                if (!blank && previousBlank && current.Count > 0)
                {
                    members.Add([.. current]);
                    current = [];
                }

                if (!blank)
                {
                    current.Add(line.Trim());
                }

                previousBlank = blank;
            }

            if (current.Count > 0)
            {
                members.Add([.. current]);
            }
        }

        return [.. members];
    }

    private static int IndexOfRun(string[] haystack, string[] run, int from)
    {
        for (int i = from; i + run.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, run.Length).SequenceEqual(run))
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void Generate_AgentsGuide_OmitsTheTestProjectWhenThereIsNone_AndKeepsUserEdits()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, console: false, integrations: false, tests: false, demos: false)));

            string path = Path.Combine(temp, "ZavaSales", "AGENTS.md");
            string agents = File.ReadAllText(path);
            Assert.DoesNotContain("Zava.Plugins.Tests", agents);
            Assert.DoesNotContain("dotnet test", agents);
            // PR #487 review: no Vela.Testing reference nor test project, so no test guidance either.
            Assert.DoesNotContain("Vela.Testing", agents);
            Assert.DoesNotContain("## Write a test", agents);
            Assert.DoesNotContain("PluginPipeline", agents);
            Assert.Contains("(`Vela.Plugins` and the `vela` CLI)", agents);
            Assert.Contains("## Write a Custom API", agents);

            // An edited AGENTS.md is kept by a later --force run (the manifest only rewrites files Vela still owns).
            File.WriteAllText(path, "my own instructions");
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, console: false, integrations: false, tests: false, demos: false, force: true)));
            Assert.Equal("my own instructions", File.ReadAllText(path));
        });
    }

    [Fact]
    public void Generate_EmitsDirectoryPackagesProps_DisablingCentralPackageManagement()   // #315
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp));
            string props = File.ReadAllText(Path.Combine(temp, "ZavaSales", "Directory.Packages.props"));

            // The solution-root Directory.Packages.props shadows any parent inherited from an enclosing repo and turns
            // CPM off, so the scaffold's inline PackageReference versions stay valid (no NU1008).
            Assert.Contains("<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>", props);
        });
    }

    [Fact]
    public void Generate_TestProject_ReferencesPluginsAndVelaTesting()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp));

            string testsCsproj = File.ReadAllText(Path.Combine(
                temp, "ZavaSales", "tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj"));

            Assert.Contains("Vela.Testing", testsCsproj);
            Assert.Contains("xunit", testsCsproj);
            Assert.Contains("..\\..\\src\\Zava.Plugins\\Zava.Plugins.csproj", testsCsproj);
        });
    }

    [Fact]
    public void Generate_OmitsTestProject_WhenDisabled()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp, tests: false));

            Assert.False(Directory.Exists(Path.Combine(temp, "ZavaSales", "tests", "Zava.Plugins.Tests")));
            Assert.False(Directory.Exists(Path.Combine(temp, "ZavaSales", "tests", "Zava.Integrations.Tests")));
        });
    }

    [Fact]
    public void Generate_OmitsIntegrationsTests_WhenIntegrationsDisabled()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp, integrations: false));

            string tests = Path.Combine(temp, "ZavaSales", "tests");
            Assert.True(Directory.Exists(Path.Combine(tests, "Zava.Plugins.Tests")));
            Assert.False(Directory.Exists(Path.Combine(tests, "Zava.Integrations.Tests")));
        });
    }

    [Fact]
    public void Generate_WithManagedIdentity_AddsSigningTargetToIntegrationsOnly()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp) with { ManagedIdentity = true });

            string src = Path.Combine(temp, "ZavaSales", "src");
            string integrationsCsproj = File.ReadAllText(Path.Combine(src, "Zava.Integrations", "Zava.Integrations.csproj"));
            string pluginsCsproj = File.ReadAllText(Path.Combine(src, "Zava.Plugins", "Zava.Plugins.csproj"));

            Assert.Contains("SignPluginPackage", integrationsCsproj);
            Assert.DoesNotContain("SignPluginPackage", pluginsCsproj);
        });
    }

    [Fact]
    public void Generate_WithManagedIdentity_ProducesWellFormedXmlProjects()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp) with { ManagedIdentity = true });

            string root = Path.Combine(temp, "ZavaSales");
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".csproj") || f.EndsWith(".props") || f.EndsWith(".config")))
            {
                // Throws on malformed XML (e.g. a '--' inside an XML comment).
                System.Xml.Linq.XDocument.Load(file);
            }
        });
    }

    [Fact]
    public void Generate_WithoutManagedIdentity_HasNoSigningTarget()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp));

            string integrationsCsproj = File.ReadAllText(
                Path.Combine(temp, "ZavaSales", "src", "Zava.Integrations", "Zava.Integrations.csproj"));
            Assert.DoesNotContain("SignPluginPackage", integrationsCsproj);
        });
    }

    [Fact]
    public void Generate_OmitsOptionalProjects_WhenDisabled()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp, console: false, integrations: false));

            string src = Path.Combine(temp, "ZavaSales", "src");
            Assert.False(Directory.Exists(Path.Combine(src, "Zava.Integrations")));
            Assert.False(Directory.Exists(Path.Combine(src, "Zava.ConsoleApp")));
            Assert.True(Directory.Exists(Path.Combine(src, "Zava.Plugins")));
        });
    }

    [Fact]
    public void Generate_SolutionReferencesEveryGeneratedProject()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp));

            string sln = File.ReadAllText(Path.Combine(temp, "ZavaSales", "ZavaSales.sln"));
            Assert.Contains("src\\Zava.Shared\\Zava.Shared.csproj", sln);
            Assert.Contains("src\\Zava.Plugins\\Zava.Plugins.csproj", sln);
            Assert.Contains("src\\Zava.Integrations\\Zava.Integrations.csproj", sln);
            Assert.Contains("tests\\Zava.Plugins.Tests\\Zava.Plugins.Tests.csproj", sln);
            Assert.Contains("tests\\Zava.Integrations.Tests\\Zava.Integrations.Tests.csproj", sln);
            Assert.Contains("src\\Zava.ConsoleApp\\Zava.ConsoleApp.csproj", sln);
        });
    }

    [Fact]
    public void Generate_Fails_WhenTargetExists_WithoutForce()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));

            int exit = ProjectScaffolder.Generate(Options(temp));
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    [Fact]
    public void Generate_Overwrites_WithForce()
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, force: true)));
        });
    }

    [Fact]
    public void Generate_WithForce_RemovesStaleProjects()
    {
        RunInTempDir(temp =>
        {
            // First generate with Integrations, then regenerate (force) without it.
            ProjectScaffolder.Generate(Options(temp, integrations: true));
            string integrationsDir = Path.Combine(temp, "ZavaSales", "src", "Zava.Integrations");
            Assert.True(Directory.Exists(integrationsDir));

            ProjectScaffolder.Generate(Options(temp, integrations: false, force: true));
            Assert.False(Directory.Exists(integrationsDir));
            Assert.True(Directory.Exists(Path.Combine(temp, "ZavaSales", "src", "Zava.Plugins")));
        });
    }

    [OptInFact("VELA_BUILD_SMOKE=1")]
    public void Generate_ThenDotnetBuild_Succeeds()
    {
        // Heavy integration test: restores + builds a generated --samples solution end to end, which is the only thing
        // that actually compiles the generated C#, then runs its generated tests and scans the packages as `vela plan`
        // does. Opt-in (VELA_BUILD_SMOKE=1) so the default suite stays fast and offline; needs the .NET SDK plus
        // network/cache for the Microsoft SDK packages (and mono to run the net48 tests off Windows).
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp) with { ManagedIdentity = true }));
            string root = Path.Combine(temp, "ZavaSales");

            // Also compile both `vela add customapi --function` forms (typed bound, lightweight global).
            string pluginsProject = Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj");
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "GetScore", "--entity", "Account", "--function", "--prefix", "zava", "--project", pluginsProject]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Lookup", "--global", "--function", "--prefix", "zava", "--project", pluginsProject]));

            // `vela add test` for the sample Custom API, whose class (AccountEstimateValueCustomApi) and request
            // (EstimateValueRequest) are not named after its unique name: they are read from its source.
            string testsProject = Path.Combine(root, "tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj");
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountEstimateValue", "--kind", "customapi", "--force", "--project", testsProject]));

            // A test that drives an HttpClient through a fake handler, as integration tests do: it only compiles on
            // net48 when the test project references the System.Net.Http facade.
            File.WriteAllText(Path.Combine(root, "tests", "Zava.Integrations.Tests", "HttpClientFakeHandlerTests.cs"), """
                using System.Net;
                using System.Net.Http;
                using System.Threading;
                using System.Threading.Tasks;
                using Xunit;

                namespace Zava.Integrations.Tests;

                public class HttpClientFakeHandlerTests
                {
                    private sealed class FakeHandler : HttpMessageHandler
                    {
                        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
                    }

                    [Fact]
                    public void HttpClient_UsesTheFakeHandler()
                    {
                        using var client = new HttpClient(new FakeHandler());
                        Assert.Equal(HttpStatusCode.Accepted, client.GetAsync("https://example.invalid/").GetAwaiter().GetResult().StatusCode);
                    }
                }
                """);

            // Exactly the sample steps: AccountRelatedContacts 1, AccountPostCreate 1, ContactRouter 2, ContactSync 1,
            // AccountSyncRouter 2. The abstract AccountMessageReference cheat-sheet (16 steps) is never deployed.
            RegistrationItem[] items = BuildTestAndScan(temp, root, expectedCustomApis: ["zava_AccountEstimateValue", "zava_GetScore", "zava_Lookup"], minimumSteps: 7, maximumSteps: 7);
            Assert.DoesNotContain(items, item => item.Type.EndsWith(".AccountMessageReference", StringComparison.Ordinal));
        });
    }

    [OptInFact("VELA_BUILD_SMOKE=1")]
    public void Generate_ThenDotnetBuild_Succeeds_Minimal()
    {
        // The default (minimal) scaffold must build, test and plan cleanly as generated, and again once filled with
        // every `vela add` kind - the code an agent or a developer actually starts from.
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--publisher-prefix", "zava", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));
            string root = Path.Combine(temp, "ZavaSales");

            // As generated: an empty package that registers nothing.
            BuildTestAndScan(temp, root, expectedCustomApis: [], minimumSteps: 0, maximumSteps: 0, runTests: false);

            // Stand in for `vela models`, then add one of everything.
            Directory.CreateDirectory(Path.Combine(root, "src", "Zava.Shared", "Models", "Generated"));
            File.WriteAllText(Path.Combine(root, "src", "Zava.Shared", "Models", "Generated", "Account.cs"), ScaffoldTemplates.AccountModel("Zava"));
            string plugins = Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj");
            string tests = Path.Combine(root, "tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj");
            string[][] adds =
            [
                ["plugin", "AccountCreated", "--entity", "Account", "--filtering-attributes", "Name"],
                ["step", "AccountCreated", "--entity", "Account", "--message", "Update", "--filtering-attributes", "name,Telephone1"],
                ["plugin", "AccountBulk", "--entity", "Account", "--message", "UpdateMultiple", "--filtering-attributes", "Name"],
                ["plugin", "AccountAssigned", "--entity", "Account", "--message", "Assign"],
                ["plugin", "AccountGuard", "--entity", "Account", "--message", "Delete", "--stage", "PreOperation"],
                ["plugin", "AccountLinked", "--entity", "Account", "--message", "Associate"],
                ["plugin", "AccountUnlinked", "--entity", "Account", "--message", "Disassociate"],
                ["plugin", "AccountRead", "--entity", "Account", "--message", "Retrieve"],
                ["plugin", "AccountSearch", "--entity", "Account", "--message", "RetrieveMultiple"],
                // A table logical name resolves to its early-bound class, and a column logical name to its property,
                // through the model.
                ["plugin", "AccountImported", "--entity", "account", "--message", "CreateMultiple", "--filtering-attributes", "name"],
                ["plugin", "AccountState", "--entity", "Account", "--message", "SetState"],
                ["service", "AccountScoring", "--entity", "Account"],
                ["customapi", "Recalculate", "--global", "--prefix", "zava"],
                ["customapi", "Score", "--entity", "Account", "--function", "--prefix", "zava"],
            ];
            foreach (string[] add in adds)
            {
                Assert.Equal(ExitSuccess, AddCommand.Run([.. add, "--project", plugins]));
            }

            string[][] testAdds =
            [
                // Each one drives its step through PluginPipeline the way a caller sends the message, and must pass.
                ["test", "AccountCreated", "--message", "Update"],
                ["test", "AccountAssigned", "--entity", "Account", "--message", "Assign"],
                ["test", "AccountBulk", "--entity", "Account", "--message", "UpdateMultiple"],
                ["test", "AccountGuard"],
                ["test", "AccountLinked"],
                ["test", "AccountUnlinked"],
                ["test", "AccountRead"],
                ["test", "AccountSearch"],
                ["test", "AccountImported"],
                ["test", "AccountState"],
                ["test", "Recalculate", "--kind", "customapi"],
                ["test", "Score", "--kind", "customapi", "--entity", "Account"],
            ];
            foreach (string[] add in testAdds)
            {
                Assert.Equal(ExitSuccess, AddCommand.Run([.. add, "--project", tests]));
            }

            // `vela add test` completes an existing class: a step added after its test gets its tests on the next run,
            // and AccountCreated, tested for its Update step only above, gets its Create step's.
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Telephone1", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountCreated", "--project", tests]));
            string touchedTests = File.ReadAllText(Path.Combine(root, "tests", "Zava.Plugins.Tests", "Account", "Plugins", "AccountTouchedTests.cs"));
            Assert.Contains("public void Fires_On_Update_PostOperation()", touchedTests);

            // Every handler `add` writes takes its generated {Handler}Context, at a PreValidation stage too, and `add
            // step` makes a hand-written, non-partial plugin partial so that context compiles; the existing handler
            // typed with the message interface keeps compiling.
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountChecked", "--entity", "Account", "--message", "Update", "--stage", "PreValidation", "--filtering-attributes", "Name", "--project", plugins]));
            string legacy = Path.Combine(root, "src", "Zava.Plugins", "Account", "Plugins", "AccountLegacy.cs");
            File.WriteAllText(legacy, """
                namespace Zava.Plugins.Account.Plugins
                {
                    using Vela.Context;
                    using Vela.Plugin;
                    using Zava.Shared.Models.Generated;

                    /// <summary>A hand-written plugin that predates the typed contexts.</summary>
                    public sealed class AccountLegacy(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
                    {
                        /// <inheritdoc />
                        protected override void RegisterPluginSteps()
                        {
                            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);
                        }

                        private void OnCreated(ICreatePostContext<Account> ctx) => ctx.Trace("Created {0}.", ctx.Response.id);
                    }
                }
                """);
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountLegacy", "--entity", "Account", "--message", "Delete", "--stage", "PreOperation", "--project", plugins]));
            Assert.Contains("public sealed partial class AccountLegacy(", File.ReadAllText(legacy));

            BuildTestAndScan(temp, root, expectedCustomApis: ["zava_Recalculate", "zava_Score"], minimumSteps: 16, maximumSteps: 16);
        });
    }

    [OptInFact("VELA_BUILD_SMOKE=1")]
    public void Generate_ThenDotnetBuild_Succeeds_MinimalWithIntegrations()
    {
        // `vela init --integrations --managed-identity` without --samples: the empty Integrations package (with its
        // inert signing target) and its empty tests project must build and plan as generated, then take a plugin, a
        // Custom API and a test like the Plugins pair does.
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--publisher-prefix", "zava", "--integrations", "--managed-identity", "--output", temp, "--core-version", LocalFrameworkVersion], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));
            string root = Path.Combine(temp, "ZavaSales");

            BuildTestAndScan(temp, root, expectedCustomApis: [], minimumSteps: 0, maximumSteps: 0, runTests: false);
            Assert.True(File.Exists(Path.Combine(root, "artifacts", "zava_Zava.Integrations.1.0.0.nupkg")), "the empty Integrations package should still be built, named with the publisher prefix");
            Assert.True(File.Exists(Path.Combine(root, "artifacts", "zava_Zava.Plugins.1.0.0.nupkg")), "the Plugins package should be named with the publisher prefix");

            Directory.CreateDirectory(Path.Combine(root, "src", "Zava.Shared", "Models", "Generated"));
            File.WriteAllText(Path.Combine(root, "src", "Zava.Shared", "Models", "Generated", "Account.cs"), ScaffoldTemplates.AccountModel("Zava"));
            string integrations = Path.Combine(root, "src", "Zava.Integrations", "Zava.Integrations.csproj");
            string integrationsTests = Path.Combine(root, "tests", "Zava.Integrations.Tests", "Zava.Integrations.Tests.csproj");
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountSync", "--entity", "Account", "--filtering-attributes", "Name", "--project", integrations]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Export", "--global", "--prefix", "zava", "--project", integrations]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["service", "AuditService", "--project", integrations]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "Export", "--kind", "customapi", "--project", integrationsTests]));

            // Every generated tests project must run at least one test, so give the Plugins pair one too.
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountAssigned", "--entity", "Account", "--message", "Assign", "--project", Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj")]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountAssigned", "--entity", "Account", "--message", "Assign", "--project", Path.Combine(root, "tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj")]));

            BuildTestAndScan(temp, root, expectedCustomApis: ["zava_Export"], minimumSteps: 2, maximumSteps: 2);
        });
    }

    // Packs the repo's current Vela packages into a local feed, restores and builds the generated solution (failing on
    // any Vela analyzer warning), runs its generated tests, and scans its packages as `vela plan` does.
    private static RegistrationItem[] BuildTestAndScan(string temp, string root, string[] expectedCustomApis, int minimumSteps, int maximumSteps = int.MaxValue, bool runTests = true)
    {
        string? coreProject = FindRepoProject("Vela.Plugins");
        string? testingProject = FindRepoProject("Vela.Testing");
        string? messagesProject = FindRepoProject("Vela.Messages");
        Assert.True(coreProject is not null, "Could not locate Vela.Plugins.csproj from the test directory.");
        Assert.True(testingProject is not null, "Could not locate Vela.Testing.csproj from the test directory.");
        Assert.True(messagesProject is not null, "Could not locate Vela.Messages.csproj from the test directory.");

        // The scaffold resolves Vela from nuget.org and emits no nuget.config; here we build against the repo's
        // *current* Vela.Plugins / Vela.Testing (LocalFrameworkVersion) instead, so the test writes its own local-packages nuget.config
        // (additive; nuget.org still resolves the Microsoft SDK / test packages) and packs those projects into it.
        string localFeed = Path.Combine(root, "local-packages");
        if (!Directory.Exists(localFeed))
        {
            Directory.CreateDirectory(localFeed);
            File.WriteAllText(Path.Combine(root, "nuget.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n" +
                "    <add key=\"local-packages\" value=\"local-packages\" />\n  </packageSources>\n</configuration>\n");
            // Vela.Plugins depends on the Vela.Messages package, so it must be in the local feed too.
            Assert.Equal(0, RunDotnet($"pack \"{messagesProject}\" -c Release -o \"{localFeed}\"", temp).ExitCode);
            Assert.Equal(0, RunDotnet($"pack \"{coreProject}\" -c Release -o \"{localFeed}\"", temp).ExitCode);
            Assert.Equal(0, RunDotnet($"pack \"{testingProject}\" -c Release -o \"{localFeed}\"", temp).ExitCode);
        }

        // Restore serially first: the multi-targeted Shared project is referenced by several projects, and a parallel
        // solution restore can race writing its nuget.g.props ("file already exists"). Then build without restoring.
        string sln = Path.Combine(root, "ZavaSales.sln");
        Assert.Equal(0, RunDotnet($"restore \"{sln}\" --disable-parallel", temp).ExitCode);

        // Default parallel build, as users run it. It also guards #406: the temp path reaches MSBuild in two forms
        // on macOS (/var vs /private/var), so the test projects' references miss the solution configuration and,
        // without ShouldUnsetParentConfigurationAndPlatform=false, a second (Debug) build of each plugin project
        // packs into the same .nupkg ("being used by another process"). A failed build keeps its binlog.
        string binlog = Path.Combine(temp, "build.binlog");
        (int build, string buildOutput) = RunDotnet($"build \"{sln}\" -c Release --no-restore -bl:\"{binlog}\"", temp);
        if (build != 0)
        {
            string kept = KeepBinlog(binlog);
            Assert.Fail($"dotnet build exited with {build}; binlog kept at {kept}\n{buildOutput}");
        }

        // Match the diagnostic id, not the word "warning", which MSBuild localizes ("avertissement" on a French host).
        string[] velaWarnings = buildOutput.Split('\n')
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\bVELA\d{3}\s*:"))
            .Distinct()
            .ToArray();
        Assert.True(velaWarnings.Length == 0, "Generated code raised Vela analyzer warnings:\n" + string.Join("\n", velaWarnings));

        // The plugin projects generate their documentation file and keep CS1591 a warning: every public member the
        // scaffold, `vela add` and the typed-context generator write carries a <summary>, so a fresh solution has none.
        string[] missingDocs = buildOutput.Split('\n')
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\bCS1591\s*:"))
            .Distinct()
            .ToArray();
        Assert.True(missingDocs.Length == 0, "Generated code is missing XML documentation:\n" + string.Join("\n", missingDocs));

        if (runTests)
        {
            RunGeneratedTests(root);
        }

        // Scan the built packages as `vela plan` does: this runs every step and Custom API Build(), so generated code
        // that breaks a registration rule (a function without a response, a Create step without FilterOn, ...) fails.
        List<string> scanTemps = [];
        try
        {
            RegistrationItem[] items = Directory.GetFiles(Path.Combine(root, "artifacts"), "*.nupkg")
                .SelectMany(nupkg => PackageScanner.BuildLocalStateFromPackages(
                    new SharedOptions([nupkg], null, null, null, null, null, null, null, false, false, false, false),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), scanTemps).Items)
                .ToArray();
            string[] customApis = items.Where(item => item.Kind == "custom-api").Select(item => item.Definition.GetProperty("uniqueName").GetString()!).ToArray();
            foreach (string expected in expectedCustomApis)
            {
                Assert.Contains(expected, customApis);
            }

            int steps = items.Count(item => item.Kind == "step");
            Assert.InRange(steps, minimumSteps, maximumSteps);
            return items;
        }
        finally
        {
            foreach (string dir in scanTemps)
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Runs every generated net48 test assembly: `dotnet test` on Windows, the xunit console under mono elsewhere.
    private static void RunGeneratedTests(string root)
    {
        string[] assemblies = Directory.GetDirectories(Path.Combine(root, "tests"))
            .Select(dir => Path.Combine(dir, "bin", "Release", "net48", Path.GetFileName(dir) + ".dll"))
            .ToArray();
        Assert.NotEmpty(assemblies);
        foreach (string assembly in assemblies)
        {
            Assert.True(File.Exists(assembly), $"Test assembly not built: {assembly}");

            // The counts come from the runner's XML report, never its console text, which is localized (a French
            // runner prints "réussite : N"): a TRX file from `dotnet test`, the xunit XML from the console runner.
            string report = Path.Combine(root, Path.GetFileNameWithoutExtension(assembly) + (OperatingSystem.IsWindows() ? ".trx" : ".xunit.xml"));
            (int exit, string output) = OperatingSystem.IsWindows()
                ? RunProcess("dotnet", $"test \"{assembly}\" --logger \"trx;LogFileName={report}\" --blame-hang-timeout 2m", root)
                : RunProcess("mono", $"\"{FindXunitConsole()}\" \"{assembly}\" -noshadow -xml \"{report}\"", root);
            Assert.True(exit == 0, output);
            Assert.True(File.Exists(report), $"The test runner wrote no report at {report}.\n{output}");

            (int passed, int failed) = OperatingSystem.IsWindows() ? ReadTrxCounts(report) : ReadXunitCounts(report);
            Assert.True(failed == 0, output);
            // A runner that discovers nothing also exits 0: require that tests actually ran.
            Assert.True(passed > 0, $"No generated test ran in {assembly}.\n{output}");
        }
    }

    // TRX: <TestRun><ResultSummary><Counters passed="N" failed="N" .../></ResultSummary></TestRun> (namespaced).
    internal static (int Passed, int Failed) ReadTrxCounts(string trxPath)
    {
        System.Xml.Linq.XElement counters = System.Xml.Linq.XDocument.Load(trxPath).Descendants()
            .First(element => element.Name.LocalName == "Counters");
        return ((int?)counters.Attribute("passed") ?? 0, ((int?)counters.Attribute("failed") ?? 0) + ((int?)counters.Attribute("error") ?? 0));
    }

    // xunit v2 XML: <assemblies><assembly total="N" passed="N" failed="N" errors="N" .../></assemblies>.
    internal static (int Passed, int Failed) ReadXunitCounts(string xmlPath)
    {
        System.Xml.Linq.XElement[] assemblies = System.Xml.Linq.XDocument.Load(xmlPath).Descendants("assembly").ToArray();
        return (
            assemblies.Sum(a => (int?)a.Attribute("passed") ?? 0),
            assemblies.Sum(a => ((int?)a.Attribute("failed") ?? 0) + ((int?)a.Attribute("errors") ?? 0)));
    }

    private static string FindXunitConsole()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string? console = Directory.Exists(Path.Combine(packages, "xunit.runner.console"))
            ? Directory.GetFiles(Path.Combine(packages, "xunit.runner.console"), "xunit.console.exe", SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}net462{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        Assert.True(console is not null, "xunit.runner.console (net462 xunit.console.exe) is not in the NuGet cache; restore it to run the generated tests under mono.");
        return console!;
    }

    private static string? FindRepoProject(string projectName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "src", projectName, $"{projectName}.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    // Copies a failed build's binlog out of the temp dir (deleted after the test) to VELA_SMOKE_LOG_DIR (CI uploads
    // it) or, unset, the system temp folder.
    private static string KeepBinlog(string binlog)
    {
        string dir = Environment.GetEnvironmentVariable("VELA_SMOKE_LOG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.GetTempPath();
        Directory.CreateDirectory(dir);
        string kept = Path.Combine(dir, $"vela-smoke-build-{DateTime.UtcNow:yyyyMMddHHmmss}.binlog");
        if (File.Exists(binlog))
        {
            File.Copy(binlog, kept, overwrite: true);
            return kept;
        }

        return "(none: the build wrote no binlog)";
    }

    // MSBuild commands run without node reuse or build servers (see RunProcess).
    private static (int ExitCode, string Output) RunDotnet(string arguments, string workingDirectory) =>
        RunProcess("dotnet", arguments.StartsWith("test ", StringComparison.Ordinal) ? arguments : arguments + " -nodeReuse:false --disable-build-servers", workingDirectory);

    // Every child of the smoke build gets a hard timeout: a hang becomes a readable failure (with what it printed)
    // instead of the CI job timing out with no log.
    internal static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(5);

    internal static (int ExitCode, string Output) RunProcess(string fileName, string arguments, string workingDirectory, TimeSpan? timeout = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            // stdin redirected and closed at once: a child that prompts (first-run notices, a test host waiting
            // for input) reads end-of-file instead of blocking on the test runner's console.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Belt and braces: ask the .NET CLI / MSBuild / VSTest for English output (nothing parses console text for
        // results, but failure messages read better). No MSBuild node reuse or build servers: a lingering node
        // inherits the output pipes and keeps them open after the build exits, so reading them to the end would hang.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["VSLANG"] = "1033";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.StandardInput.Close();

        // Both streams are drained concurrently (sequential reads deadlock once a pipe buffer fills).
        System.Text.StringBuilder output = new();
        object gate = new();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (gate) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (gate) { output.AppendLine(e.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        TimeSpan limit = timeout ?? ChildTimeout;
        if (!process.WaitForExit(limit))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited meanwhile
            }

            process.WaitForExit(TimeSpan.FromSeconds(10));
            string captured;
            lock (gate)
            {
                captured = output.ToString();
            }

            Assert.Fail($"'{fileName} {arguments}' did not finish within {limit.TotalMinutes:0.#} minutes and was killed. Output so far:\n{captured}");
        }

        // The exit is in; give the async readers a bounded moment to flush. Only the parameterless WaitForExit() waits
        // for the redirected streams to reach end-of-file (the timed overload returns once the process exits, so the
        // last lines could still be in flight); it runs on a task with a bound, so a grandchild still holding the pipes
        // cannot turn into a hang here either.
        System.Threading.Tasks.Task.Run(process.WaitForExit).Wait(TimeSpan.FromSeconds(30));
        lock (gate)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    [Fact]
    public void Generate_WithoutWebResources_OmitsTheProject()
    {
        RunInTempDir(temp =>
        {
            ProjectScaffolder.Generate(Options(temp));
            string root = Path.Combine(temp, "ZavaSales");

            Assert.False(Directory.Exists(Path.Combine(root, "src", "Zava.WebResources")));
            Assert.DoesNotContain(".esproj", File.ReadAllText(Path.Combine(root, "ZavaSales.sln")));
        });
    }

    [Fact]
    public void Generate_WithWebResources_CreatesProjectAndWiresTheSolution()
    {
        RunInTempDir(temp =>
        {
            ScaffoldOptions options = Options(temp) with
            {
                IncludeWebResources = true,
                WebResourceNamespace = "adc.sales",
                WebResourcesFolder = "dist",
            };

            int exit = ProjectScaffolder.Generate(options);
            Assert.Equal(ExitSuccess, exit);

            string root = Path.Combine(temp, "ZavaSales");
            string project = Path.Combine(root, "src", "Zava.WebResources");
            AssertFileExists(root, "src", "Zava.WebResources", "Zava.WebResources.esproj");
            AssertFileExists(root, "src", "Zava.WebResources", "package.json");
            AssertFileExists(root, "src", "Zava.WebResources", "tsconfig.json");
            AssertFileExists(root, "src", "Zava.WebResources", "tsup.config.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "web-resources.entries.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "vitest.config.ts");
            AssertFileExists(root, "src", "Zava.WebResources", ".gitignore");
            AssertFileExists(root, "src", "Zava.WebResources", "src", "account", "Forms", "main.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "src", "account", "Forms", "main.test.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "src", "generated", "forms", "account", "main.form.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "src", "generated", "option-sets", "account.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "src", "generated", "tables", "contact.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "src", "generated", "index.ts");
            AssertFileExists(root, "src", "Zava.WebResources", "test", "entries.test.ts");

            // The .sln references the .esproj under the JavaScript project type id.
            string sln = File.ReadAllText(Path.Combine(root, "ZavaSales.sln"));
            Assert.Contains("Zava.WebResources.esproj", sln);
            Assert.Contains(ScaffoldTemplates.JavaScriptProjectTypeGuid, sln);

            // tsup carries the chosen global name and output folder, imports the entry map, and forces the `.js`
            // out-extension (so the deployed name never carries tsup's `.global` suffix).
            string tsup = File.ReadAllText(Path.Combine(project, "tsup.config.ts"));
            Assert.Contains("globalName: 'adc.sales'", tsup);
            Assert.Contains("outDir: 'dist'", tsup);
            Assert.Contains("import { entries } from './web-resources.entries'", tsup);
            Assert.Contains("entry: entries,", tsup);
            Assert.Contains("outExtension()", tsup);
            Assert.Contains("return { js: '.js' };", tsup);

            // The entry map is the source of truth: it carries the prefixed name -> source module mirror.
            string entries = File.ReadAllText(Path.Combine(project, "web-resources.entries.ts"));
            Assert.Contains("export const entries = {", entries);
            Assert.Contains("'adc_/js/account/Forms/main': 'src/account/Forms/main.ts',", entries);
            Assert.Contains("} as const;", entries);

            // package.json carries @types/node (the entries guard imports node:url / node:path, so a tsc --noEmit
            // typecheck needs the Node types) alongside the project name.
            string packageJson = File.ReadAllText(Path.Combine(project, "package.json"));
            Assert.Contains("\"name\": \"zava-webresources\"", packageJson);
            Assert.Contains("\"@types/node\":", packageJson);

            // tsconfig lists "node" explicitly and defines the @generated path alias (baseUrl + paths).
            string tsconfig = File.ReadAllText(Path.Combine(project, "tsconfig.json"));
            Assert.Contains("\"types\": [\"vitest/globals\", \"node\"]", tsconfig);
            Assert.Contains("\"@generated\": [\"src/generated\"]", tsconfig);

            // vitest resolves the same @generated alias so tests import the barrel like the source does.
            Assert.Contains("'@generated': fileURLToPath(new URL('./src/generated', import.meta.url))",
                File.ReadAllText(Path.Combine(project, "vitest.config.ts")));

            Assert.Contains("dist/", File.ReadAllText(Path.Combine(project, ".gitignore")));

            // The example entry exports onLoad and onSave (via defineForm), so the freshly scaffolded entries guard
            // passes; it is a real example, not a stub (typed accessors + a typed Web API query wired with defineForm).
            string main = File.ReadAllText(Path.Combine(project, "src", "account", "Forms", "main.ts"));
            // The form namespace is lower-cased and aligned with the ribbon (<prefix>.Forms.<entity>).
            Assert.Contains("export const { onLoad, onSave } = defineForm(AccountMain, AccountForm, { namespace: 'zava.Forms.account' });", main);
            Assert.Contains("export class AccountForm extends VelaForm(AccountMain)", main);
            Assert.Contains("createWebApi()", main);
            // Imports the generated symbols through the @generated barrel, not deep paths.
            Assert.Contains("import { AccountMain, Contact, account_customertypecode } from '@generated';", main);

            // The example generated model set is present (with the EXAMPLE regenerate header) so the form compiles.
            string generatedForm = File.ReadAllText(Path.Combine(project, "src", "generated", "forms", "account", "main.form.ts"));
            Assert.Contains("// EXAMPLE - regenerate with:", generatedForm);
            Assert.Contains("export const AccountMain =", generatedForm);

            // The example barrel re-exports the model set for the @generated alias, including the ribbon command
            // context alias as a type-only re-export.
            string barrel = File.ReadAllText(Path.Combine(project, "src", "generated", "index.ts"));
            Assert.Contains("export { AccountMain } from './forms/account/main.form';", barrel);
            Assert.Contains("export type { AccountCmd } from './forms/account/main.form';", barrel);
            Assert.Contains("export { Contact } from './tables/contact';", barrel);
            Assert.Contains("export type AccountCmd = FormCommandContext<typeof AccountMain>;", generatedForm);

            // The example test demonstrates both layers: form rules and the typed Web API (via fakeForm({ webApi })).
            string mainTest = File.ReadAllText(Path.Combine(project, "src", "account", "Forms", "main.test.ts"));
            Assert.Contains("import { fakeForm, createFakeWebApi } from 'vela-client/testing';", mainTest);
            Assert.Contains("reads related contacts through the typed Web API", mainTest);
            Assert.Contains("fakeForm(AccountMain, AccountForm, { recordId:", mainTest);
            Assert.Contains("form.instance.relatedContacts()", mainTest);
            Assert.Contains("form.restore();", mainTest);

            // The entries guard reads the entry map and asserts each entry module exports the FormXML handlers.
            string entriesTest = File.ReadAllText(Path.Combine(project, "test", "entries.test.ts"));
            Assert.Contains("exports onLoad/onSave", entriesTest);
            Assert.Contains("import { entries } from '../web-resources.entries'", entriesTest);

            // The project README documents form scripts and ribbon commands so both are discoverable.
            string readme = File.ReadAllText(Path.Combine(project, "README.md"));
            Assert.Contains("## Ribbon commands", readme);
            Assert.Contains("vela add ribbon-script <entity>", readme);
            Assert.Contains("point the button's action at", readme);
        });
    }

    [Fact]
    public void BuildScaffoldReport_Lists_Solution_Root_Files_And_Projects()
    {
        string json = ProjectScaffolder.BuildScaffoldReport(
            "ZavaSales",
            "/tmp/ZavaSales",
            29,
            new[]
            {
                new ScaffoldTemplates.ProjectEntry("Zava.Shared", "src\\Zava.Shared\\Zava.Shared.csproj", ScaffoldTemplates.StableGuid("Zava.Shared")),
                new ScaffoldTemplates.ProjectEntry("Zava.Plugins", "src\\Zava.Plugins\\Zava.Plugins.csproj", ScaffoldTemplates.StableGuid("Zava.Plugins")),
            });

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("ZavaSales", doc.RootElement.GetProperty("solution").GetString());
        Assert.Equal("/tmp/ZavaSales", doc.RootElement.GetProperty("root").GetString());
        Assert.Equal(29, doc.RootElement.GetProperty("files").GetInt32());
        Assert.Contains("Zava.Plugins", doc.RootElement.GetProperty("projects").EnumerateArray().Select(e => e.GetString()));
    }

    private static void AssertFileExists(string root, params string[] segments)
    {
        string path = root;
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        Assert.True(File.Exists(path), $"Expected file to exist: {path}");
    }

    [Fact]
    public void Force_PreservesUserEditedRootFiles_ButRegeneratesTheSolution()   // B9-scaffold / Part B
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp)));
            string root = Path.Combine(temp, "ZavaSales");

            // The user customizes root files.
            File.WriteAllText(Path.Combine(root, "README.md"), "MY CUSTOM README");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "my-custom-ignore");
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project><!-- mine --></Project>");

            // Regenerate with --force.
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp, force: true)));

            // User-editable root files are preserved...
            Assert.Equal("MY CUSTOM README", File.ReadAllText(Path.Combine(root, "README.md")));
            Assert.Equal("my-custom-ignore", File.ReadAllText(Path.Combine(root, ".gitignore")));
            Assert.Contains("mine", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
            // ...while the managed solution file is (re)written.
            Assert.Contains("Microsoft Visual Studio Solution File", File.ReadAllText(Path.Combine(root, "ZavaSales.sln")));
        });
    }

    private static void RunInTempDir(Action<string> action)
    {
        string temp = Path.Combine(Path.GetTempPath(), "vela-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            action(temp);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir should not fail the test.
            }
        }
    }
}
