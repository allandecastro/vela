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
    private static ScaffoldOptions Options(string outputDir, bool console = true, bool integrations = true, bool tests = true, bool force = false) => new(
        SolutionName: "ZavaSales",
        Prefix: "Zava",
        OutputDirectory: outputDir,
        IncludeConsole: console,
        IncludeIntegrations: integrations,
        IncludeTests: tests,
        CoreVersion: "1.0.0",
        Force: force,
        Interactive: false);

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

    [Fact]
    public void Generate_ThenDotnetBuild_Succeeds()
    {
        // Heavy integration test: restores + builds a generated solution end to end, which is the only thing
        // that actually compiles the generated C#. Opt-in (VELA_BUILD_SMOKE=1) so the default suite stays fast
        // and offline; needs the .NET SDK plus network/cache for the Microsoft SDK packages.
        if (Environment.GetEnvironmentVariable("VELA_BUILD_SMOKE") != "1")
        {
            return;
        }

        string? coreProject = FindRepoProject("Vela.Plugins");
        string? testingProject = FindRepoProject("Vela.Testing");
        string? messagesProject = FindRepoProject("Vela.Messages");
        Assert.True(coreProject is not null, "Could not locate Vela.Plugins.csproj from the test directory.");
        Assert.True(testingProject is not null, "Could not locate Vela.Testing.csproj from the test directory.");
        Assert.True(messagesProject is not null, "Could not locate Vela.Messages.csproj from the test directory.");

        RunInTempDir(temp =>
        {
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(Options(temp) with { ManagedIdentity = true }));

            string root = Path.Combine(temp, "ZavaSales");
            string localFeed = Path.Combine(root, "local-packages");

            // The scaffold resolves Vela from nuget.org and emits no nuget.config; here we build against the
            // repo's *current* Vela.Plugins / Vela.Testing (1.0.0) instead, so the test writes its own
            // local-packages nuget.config (additive; nuget.org still resolves the Microsoft SDK / test packages)
            // and packs those projects into it. This keeps the build check on local sources, not a published feed.
            Directory.CreateDirectory(localFeed);
            File.WriteAllText(Path.Combine(root, "nuget.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n" +
                "    <add key=\"local-packages\" value=\"local-packages\" />\n  </packageSources>\n</configuration>\n");
            // Vela.Plugins depends on the Vela.Messages package, so it must be in the local feed too, otherwise
            // the generated project's restore cannot resolve it (Vela.Messages 1.0.0 is not on nuget.org).
            Assert.Equal(0, RunDotnet($"pack \"{messagesProject}\" -c Release -o \"{localFeed}\"", temp));
            Assert.Equal(0, RunDotnet($"pack \"{coreProject}\" -c Release -o \"{localFeed}\"", temp));
            Assert.Equal(0, RunDotnet($"pack \"{testingProject}\" -c Release -o \"{localFeed}\"", temp));

            // Restore serially first: the multi-targeted Shared project is referenced by several projects, and a
            // parallel solution restore can race writing its nuget.g.props ("file already exists"). Then build
            // without restoring again.
            string sln = Path.Combine(root, "ZavaSales.sln");
            Assert.Equal(0, RunDotnet($"restore \"{sln}\" --disable-parallel", temp));

            // Single-process build (-m:1): two plugin projects run GeneratePackageOnBuild into the shared
            // artifacts/ folder, and a parallel build can race on the pack output ("used by another process").
            int build = RunDotnet($"build \"{sln}\" -c Release --no-restore -m:1", temp);
            Assert.Equal(0, build);
        });
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

    private static int RunDotnet(string arguments, string workingDirectory)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        using Process? process = Process.Start(psi);
        Assert.NotNull(process);
        process!.WaitForExit();
        return process.ExitCode;
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
