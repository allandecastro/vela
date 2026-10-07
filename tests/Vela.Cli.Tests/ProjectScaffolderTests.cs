using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class ProjectScaffolderTests
{
    private static ScaffoldOptions Options(string outputDir, bool console = true, bool integrations = true, bool tests = true, bool force = false, bool demos = true) => new(
        SolutionName: "ZavaSales",
        Prefix: "Zava",
        OutputDirectory: outputDir,
        IncludeConsole: console,
        IncludeIntegrations: integrations,
        IncludeTests: tests,
        CoreVersion: "1.0.0",
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

            // The Plugins project has no source files at all - it is a clean canvas.
            string pluginsDir = Path.Combine(root, "src", "Zava.Plugins");
            Assert.Empty(Directory.GetFiles(pluginsDir, "*.cs", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Generate_Default_IsMinimal_WithAnEmptyTestProject()   // A-53
    {
        RunInTempDir(temp =>
        {
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--output", temp, "--core-version", "1.0.0"], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            string root = Path.Combine(temp, "ZavaSales");
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            // Exactly the structural files: no sample code, no Integrations/ConsoleApp, an empty test project.
            Assert.Equal(
                [
                    ".gitignore",
                    ".vela/scaffold-manifest.json",
                    "Directory.Build.props",
                    "Directory.Packages.props",
                    "README.md",
                    "ZavaSales.sln",
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
    public void Generate_PublisherPrefix_NamesTheSampleCustomApis_AndIsRecordedInTheProjects()   // A-50
    {
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp) with { PublisherPrefix = "zs" }));

            string root = Path.Combine(temp, "ZavaSales");
            Assert.Contains("RegisterCustomApi(\"zs_AccountEstimateValue\")", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Account", "CustomApis", "AccountEstimateValueCustomApi.cs")));
            Assert.Contains("ForMessage(\"zs_Ping\")", File.ReadAllText(Path.Combine(root, "tests", "Zava.Plugins.Tests", "GlobalCustomApis", "PingCustomApiTests.cs")));
            Assert.Contains("RegisterCustomApi(\"zs_IntegrationHeartbeat\"", File.ReadAllText(Path.Combine(root, "src", "Zava.Integrations", "GlobalCustomApis", "IntegrationHeartbeatCustomApi.cs")));
            foreach (string csproj in new[] { Path.Combine("src", "Zava.Plugins", "Zava.Plugins.csproj"), Path.Combine("src", "Zava.Integrations", "Zava.Integrations.csproj"), Path.Combine("tests", "Zava.Plugins.Tests", "Zava.Plugins.Tests.csproj") })
            {
                Assert.Contains("<VelaPublisherPrefix>zs</VelaPublisherPrefix>", File.ReadAllText(Path.Combine(root, csproj)));
            }
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
            Assert.Contains("RegisterCustomApi(\"zava_AccountEstimateValue\")", File.ReadAllText(Path.Combine(root, "src", "Zava.Plugins", "Account", "CustomApis", "AccountEstimateValueCustomApi.cs")));
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
            Assert.Equal(ScaffoldTemplates.PingCustomApi("Zava"), File.ReadAllText(deletedSample).Replace("\r\n", "\n"));

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
    public void Generate_NextSteps_PackageWithBuild_NotPack()
    {
        RunInTempDir(temp =>
        {
            TextWriter original = Console.Out;
            StringWriter captured = new();
            Console.SetOut(captured);
            try
            {
                ProjectScaffolder.Generate(Options(temp));
            }
            finally
            {
                Console.SetOut(original);
            }

            string output = captured.ToString();
            // The next steps package via the build and read the package from artifacts/ - never `dotnet pack`.
            Assert.Contains("dotnet build ZavaSales.sln -c Release", output);
            Assert.Contains("vela plan --packages-dir artifacts", output);
            Assert.DoesNotContain("dotnet pack", output);
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
        // network/cache for the Microsoft SDK packages (and mono to run the net462 tests off Windows).
        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp) with { ManagedIdentity = true }));
            string root = Path.Combine(temp, "ZavaSales");

            // Also compile both `vela add customapi --function` forms (typed bound, lightweight global).
            string pluginsProject = Path.Combine(root, "src", "Zava.Plugins", "Zava.Plugins.csproj");
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "GetScore", "--entity", "Account", "--function", "--prefix", "zava", "--project", pluginsProject]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Lookup", "--global", "--function", "--prefix", "zava", "--project", pluginsProject]));

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
            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--publisher-prefix", "zava", "--output", temp, "--core-version", "1.0.0"], out ScaffoldOptions options, out _));
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
                ["service", "AccountScoring", "--entity", "Account"],
                ["customapi", "Recalculate", "--global"],
                ["customapi", "Score", "--entity", "Account", "--function"],
            ];
            foreach (string[] add in adds)
            {
                Assert.Equal(ExitSuccess, AddCommand.Run([.. add, "--project", plugins]));
            }

            string[][] testAdds =
            [
                ["test", "AccountAssigned", "--entity", "Account", "--message", "Assign"],
                ["test", "AccountBulk", "--entity", "Account", "--message", "UpdateMultiple"],
                ["test", "Recalculate", "--kind", "customapi"],
                ["test", "Score", "--kind", "customapi", "--entity", "Account"],
            ];
            foreach (string[] add in testAdds)
            {
                Assert.Equal(ExitSuccess, AddCommand.Run([.. add, "--project", tests]));
            }

            BuildTestAndScan(temp, root, expectedCustomApis: ["zava_Recalculate", "zava_Score"], minimumSteps: 5, maximumSteps: 5);
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
        // *current* Vela.Plugins / Vela.Testing (1.0.0) instead, so the test writes its own local-packages nuget.config
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

        // Single-process build (-m:1): two plugin projects run GeneratePackageOnBuild into the shared artifacts/
        // folder, and a parallel build can race on the pack output ("used by another process").
        (int build, string buildOutput) = RunDotnet($"build \"{sln}\" -c Release --no-restore -m:1", temp);
        Assert.True(build == 0, buildOutput);
        string[] velaWarnings = buildOutput.Split('\n').Where(line => line.Contains("warning VELA", StringComparison.Ordinal)).Distinct().ToArray();
        Assert.True(velaWarnings.Length == 0, "Generated code raised Vela analyzer warnings:\n" + string.Join("\n", velaWarnings));

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

    // Runs every generated net462 test assembly: `dotnet test` on Windows, the xunit console under mono elsewhere.
    private static void RunGeneratedTests(string root)
    {
        string[] assemblies = Directory.GetDirectories(Path.Combine(root, "tests"))
            .Select(dir => Path.Combine(dir, "bin", "Release", "net462", Path.GetFileName(dir) + ".dll"))
            .ToArray();
        Assert.NotEmpty(assemblies);
        foreach (string assembly in assemblies)
        {
            Assert.True(File.Exists(assembly), $"Test assembly not built: {assembly}");
            (int exit, string output) = OperatingSystem.IsWindows()
                ? RunProcess("dotnet", $"test \"{assembly}\"", root)
                : RunProcess("mono", $"\"{FindXunitConsole()}\" \"{assembly}\" -noshadow", root);
            Assert.True(exit == 0, output);
            // A runner that discovers nothing also exits 0: require that tests actually ran.
            Assert.Matches(OperatingSystem.IsWindows() ? @"Passed:\s*[1-9]" : @"Total: [1-9]", output);
        }
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

    private static (int ExitCode, string Output) RunDotnet(string arguments, string workingDirectory) =>
        RunProcess("dotnet", arguments, workingDirectory);

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments, string workingDirectory)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process? process = Process.Start(psi);
        Assert.NotNull(process);
        Task<string> stdout = process!.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
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
