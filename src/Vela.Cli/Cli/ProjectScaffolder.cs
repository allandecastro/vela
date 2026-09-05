using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.OutputWriter;
using static Vela.Cli.ScaffoldTemplates;

namespace Vela.Cli;

/// <summary>
/// Orchestrates <c>vela init</c>: resolves the target layout, writes the generated files, and reports a
/// summary. All file-system side effects live here; content comes from <see cref="ScaffoldTemplates"/>.
/// </summary>
internal static class ProjectScaffolder
{
    public static int Generate(ScaffoldOptions options)
    {
        string root = Path.GetFullPath(Path.Combine(options.OutputDirectory, options.SolutionName));

        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !options.Force)
        {
            return Fail($"Target directory already exists and is not empty: {root}. Use --force to overwrite.", ExitInvalidArguments, includeUsage: false);
        }

        string prefix = options.Prefix;

        string sharedName = $"{prefix}.Shared";
        string pluginsName = $"{prefix}.Plugins";
        string integrationsName = $"{prefix}.Integrations";
        string consoleName = $"{prefix}.ConsoleApp";
        string testsName = $"{prefix}.Plugins.Tests";
        string integrationsTestsName = $"{prefix}.Integrations.Tests";

        // On --force, remove the project directories we manage so a regeneration that drops a project
        // (e.g. --no-integrations) does not leave a stale one behind. Scoped to our own folders so any
        // user-added files at the solution root are preserved.
        if (options.Force && Directory.Exists(root))
        {
            (string folder, string project)[] managed =
            [
                ("src", sharedName), ("src", pluginsName), ("src", integrationsName), ("src", consoleName),
                ("tests", testsName), ("tests", integrationsTestsName),
            ];
            foreach ((string folder, string project) in managed)
            {
                string projectDir = Path.Combine(root, folder, project);
                if (Directory.Exists(projectDir))
                {
                    Directory.Delete(projectDir, recursive: true);
                }
            }
        }

        if (options.ManagedIdentity && !options.IncludeIntegrations)
        {
            // stderr so it never pollutes the --json report on stdout.
            Console.Error.WriteLine("Warning: --managed-identity applies to the Integrations project, which is excluded; ignoring it.");
        }

        List<ProjectEntry> projects = [];
        int fileCount = 0;

        void Write(string relativeToRoot, string content)
        {
            string fullPath = Path.Combine(root, relativeToRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            fileCount++;
        }

        // Solution files canonically use backslash separators; dotnet accepts them on every platform.
        // The .sln lives at the solution root, so paths are prefixed with the project's folder (src/ or tests/).
        void AddProject(string name, string folder) =>
            projects.Add(new ProjectEntry(name, $"{folder}\\{name}\\{name}.csproj", StableGuid(name)));

        // ---- Shared ----
        AddProject(sharedName, "src");
        Write(Path.Combine("src", sharedName, $"{sharedName}.csproj"), SharedCsproj(options));
        Write(Path.Combine("src", sharedName, "Models", "Generated", "Account.cs"), AccountModel(prefix));
        Write(Path.Combine("src", sharedName, "Models", "Generated", "Contact.cs"), ContactModel(prefix));
        Write(Path.Combine("src", sharedName, "Services", "IGreetingService.cs"), GreetingServiceInterface(prefix));
        Write(Path.Combine("src", sharedName, "Services", "GreetingService.cs"), GreetingService(prefix));

        // ---- Plugins ----
        // Code is organised by table (Account/, Contact/) with Plugins/ and CustomApis/ subfolders;
        // unbound (global) Custom APIs live under GlobalCustomApis/.
        AddProject(pluginsName, "src");
        Write(Path.Combine("src", pluginsName, $"{pluginsName}.csproj"), PluginCsproj(pluginsName, sharedName, options.CoreVersion));
        Write(Path.Combine("src", pluginsName, "Account", "Plugins", "AccountPostCreatePlugin.cs"), AccountPostCreatePlugin(prefix));
        Write(Path.Combine("src", pluginsName, "Account", "Plugins", "AccountRelatedContactsPlugin.cs"), AccountRelatedContactsPlugin(prefix));
        Write(Path.Combine("src", pluginsName, "Account", "Plugins", "AccountMessageReference.cs"), AccountMessageReference(prefix));
        Write(Path.Combine("src", pluginsName, "Account", "Services", "AccountFollowUpService.cs"), AccountFollowUpService(prefix));
        Write(Path.Combine("src", pluginsName, "Account", "CustomApis", "AccountEstimateValueCustomApi.cs"), AccountEstimateValueCustomApi(prefix));
        Write(Path.Combine("src", pluginsName, "Contact", "Plugins", "ContactRouterPlugin.cs"), ContactRouterPlugin(prefix));
        Write(Path.Combine("src", pluginsName, "Contact", "Services", "ContactDuplicateService.cs"), ContactDuplicateService(prefix));
        Write(Path.Combine("src", pluginsName, "Contact", "CustomApis", "ContactValidateCustomApi.cs"), ContactValidateCustomApi(prefix));
        Write(Path.Combine("src", pluginsName, "GlobalCustomApis", "PingCustomApi.cs"), PingCustomApi(prefix));

        // ---- Integrations (optional) ----
        if (options.IncludeIntegrations)
        {
            AddProject(integrationsName, "src");
            Write(Path.Combine("src", integrationsName, $"{integrationsName}.csproj"), PluginCsproj(integrationsName, sharedName, options.CoreVersion, withSigningTarget: options.ManagedIdentity));
            Write(Path.Combine("src", integrationsName, "Account", "Plugins", "AccountSyncRouterPlugin.cs"), AccountSyncRouterPlugin(prefix));
            Write(Path.Combine("src", integrationsName, "Account", "CustomApis", "AccountReconcileCustomApi.cs"), AccountReconcileCustomApi(prefix));
            Write(Path.Combine("src", integrationsName, "Contact", "Plugins", "ContactSyncPlugin.cs"), ContactSyncPlugin(prefix));
            Write(Path.Combine("src", integrationsName, "Contact", "CustomApis", "ContactExportCustomApi.cs"), ContactExportCustomApi(prefix));
            Write(Path.Combine("src", integrationsName, "GlobalCustomApis", "IntegrationHeartbeatCustomApi.cs"), IntegrationHeartbeatCustomApi(prefix));
        }

        // ---- Plugins.Tests (optional) ----
        // A unit-test project that runs the example plugins/Custom APIs with Vela.Testing — no Dataverse
        // connection. Mirrors the by-table layout of the Plugins project it tests.
        if (options.IncludeTests)
        {
            AddProject(testsName, "tests");
            Write(Path.Combine("tests", testsName, $"{testsName}.csproj"), TestsCsproj(testsName, pluginsName, sharedName, options.CoreVersion));
            Write(Path.Combine("tests", testsName, "Security", "SecurityExampleTests.cs"), SecurityExampleTests(prefix));
            Write(Path.Combine("tests", testsName, "Account", "Plugins", "AccountPostCreatePluginTests.cs"), AccountPostCreatePluginTests(prefix));
            Write(Path.Combine("tests", testsName, "Account", "Plugins", "AccountRelatedContactsPluginTests.cs"), AccountRelatedContactsPluginTests(prefix));
            Write(Path.Combine("tests", testsName, "Account", "CustomApis", "AccountEstimateValueCustomApiTests.cs"), AccountEstimateValueCustomApiTests(prefix));
            Write(Path.Combine("tests", testsName, "Contact", "Plugins", "ContactRouterPluginTests.cs"), ContactRouterPluginTests(prefix));
            Write(Path.Combine("tests", testsName, "Contact", "CustomApis", "ContactValidateCustomApiTests.cs"), ContactValidateCustomApiTests(prefix));
            Write(Path.Combine("tests", testsName, "GlobalCustomApis", "PingCustomApiTests.cs"), PingCustomApiTests(prefix));
        }

        // ---- Integrations.Tests (optional) ----
        // Unit tests for the Integrations sample package, mirroring its by-table layout. Only generated when
        // both the Integrations project and tests are included.
        if (options.IncludeIntegrations && options.IncludeTests)
        {
            AddProject(integrationsTestsName, "tests");
            Write(Path.Combine("tests", integrationsTestsName, $"{integrationsTestsName}.csproj"), TestsCsproj(integrationsTestsName, integrationsName, sharedName, options.CoreVersion));
            Write(Path.Combine("tests", integrationsTestsName, "Account", "CustomApis", "AccountReconcileCustomApiTests.cs"), AccountReconcileCustomApiTests(prefix));
            Write(Path.Combine("tests", integrationsTestsName, "Contact", "CustomApis", "ContactExportCustomApiTests.cs"), ContactExportCustomApiTests(prefix));
            Write(Path.Combine("tests", integrationsTestsName, "GlobalCustomApis", "IntegrationHeartbeatCustomApiTests.cs"), IntegrationHeartbeatCustomApiTests(prefix));
        }

        // ---- ConsoleApp (optional) ----
        if (options.IncludeConsole)
        {
            AddProject(consoleName, "src");
            Write(Path.Combine("src", consoleName, $"{consoleName}.csproj"), ConsoleCsproj(options));
            Write(Path.Combine("src", consoleName, "Program.cs"), ConsoleProgram(prefix));
        }

        // ---- WebResources (optional, TypeScript) ----
        // An opt-in JS/TS SDK project (.esproj) so Visual Studio "Build" runs npm. Its src/generated holds the
        // regenerated form models (vela models --forms); src/ is hand-written form/ribbon/shared code.
        string webResourcesName = $"{prefix}.WebResources";
        if (options.IncludeWebResources)
        {
            projects.Add(new ProjectEntry(
                webResourcesName,
                $"src\\{webResourcesName}\\{webResourcesName}.esproj",
                StableGuid(webResourcesName),
                JavaScriptProjectTypeGuid));

            // The JS global name (esbuild wrapper root) and the form/ribbon namespaces are lower-cased so the
            // deployed globals are consistent regardless of the prefix casing.
            string globalName = options.WebResourceNamespace.ToLowerInvariant();
            Write(Path.Combine("src", webResourcesName, $"{webResourcesName}.esproj"), WebResourcesEsproj());
            Write(Path.Combine("src", webResourcesName, "README.md"), WebResourcesReadme(prefix, globalName, options.WebResourcesFolder));
            Write(Path.Combine("src", webResourcesName, "package.json"), WebResourcesPackageJson(prefix));
            Write(Path.Combine("src", webResourcesName, "tsconfig.json"), WebResourcesTsconfig());
            Write(Path.Combine("src", webResourcesName, "tsup.config.ts"), WebResourcesTsupConfig(globalName, options.WebResourcesFolder));
            Write(Path.Combine("src", webResourcesName, "web-resources.entries.ts"), WebResourcesEntries(globalName));
            Write(Path.Combine("src", webResourcesName, "vitest.config.ts"), WebResourcesVitestConfig());
            Write(Path.Combine("src", webResourcesName, ".gitignore"), WebResourcesGitignore(options.WebResourcesFolder));
            Write(Path.Combine("src", webResourcesName, "src", "account", "Forms", "main.ts"), WebResourcesExampleForm(prefix));
            Write(Path.Combine("src", webResourcesName, "src", "account", "Forms", "main.test.ts"), WebResourcesExampleFormTest());
            // A ready-made example model set so the example form compiles and its test passes out of the box;
            // `vela models` overwrites src/generated with real models from your environment.
            Write(Path.Combine("src", webResourcesName, "src", "generated", "forms", "account", "main.form.ts"), WebResourcesExampleGeneratedForm());
            Write(Path.Combine("src", webResourcesName, "src", "generated", "option-sets", "account.ts"), WebResourcesExampleGeneratedOptionSets());
            Write(Path.Combine("src", webResourcesName, "src", "generated", "tables", "contact.ts"), WebResourcesExampleGeneratedTable());
            Write(Path.Combine("src", webResourcesName, "src", "generated", "index.ts"), WebResourcesExampleGeneratedBarrel());
            Write(Path.Combine("src", webResourcesName, "test", "entries.test.ts"), WebResourcesEntriesTest());
        }

        // ---- Solution-wide files ----
        Write("Directory.Build.props", DirectoryBuildProps());
        Write($"{options.SolutionName}.sln", Solution(projects));
        Write(".gitignore", Gitignore());
        // No nuget.config: Vela.Plugins / Vela.Testing (and the Microsoft SDK packages) all resolve from
        // nuget.org, the default source. Add your own nuget.config only if you use a private feed.
        Write("README.md", RootReadme(options));

        if (options.Json)
        {
            Console.WriteLine(BuildScaffoldReport(options.SolutionName, root, fileCount, projects));
            return ExitSuccess;
        }

        Console.WriteLine($"Created {options.SolutionName} ({fileCount} files) at {root}");
        Console.WriteLine("Projects:");
        foreach (ProjectEntry project in projects)
        {
            Console.WriteLine($"  - {project.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine($"  dotnet build {options.SolutionName}.sln -c Release");
        if (options.IncludeTests)
        {
            Console.WriteLine($"  dotnet test tests/{testsName} -c Release");
            if (options.IncludeIntegrations)
            {
                Console.WriteLine($"  dotnet test tests/{integrationsTestsName} -c Release");
            }
        }

        // `dotnet build -c Release` packages the plugin project(s) into artifacts/ (GeneratePackageOnBuild +
        // PackageOutputPath). `dotnet pack` is intentionally not used: run cold it emits an assembly-less package.
        Console.WriteLine($"  vela plan --packages-dir artifacts --interactive --verbose");
        if (options.IncludeWebResources)
        {
            Console.WriteLine($"  cd src/{webResourcesName} && npm install && npm run build   # bundle the form web resources");
            Console.WriteLine("  vela add ribbon-script <entity>   # add a typed ribbon command (wire the button in the Command Designer)");
            Console.WriteLine($"  vela apply --webresources src/{webResourcesName}/{options.WebResourcesFolder} --solution <name> --execute --interactive   # deploy them");
        }

        return ExitSuccess;
    }

    // The --json success report: the solution, its root, the file count and the generated project names.
    internal static string BuildScaffoldReport(string solution, string root, int fileCount, IReadOnlyList<ProjectEntry> projects) =>
        JsonSerializer.Serialize(
            new
            {
                solution,
                root,
                files = fileCount,
                projects = projects.Select(p => p.Name).ToArray(),
            },
            new JsonSerializerOptions { WriteIndented = true });
}
