using System;
using System.IO;
using System.Linq;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>
/// #575: the project values are flags. <c>vela plan</c> / <c>apply</c> require <c>--solution</c>, <c>vela add customapi</c>
/// requires <c>--prefix</c>, and <c>vela init</c> records both in a <c>PROJECT.md</c> that the CLI never reads. Redirects
/// the console, so it runs in the serialized ConsoleRedirect collection.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ProjectValuesTests
{
    private const string ConnectionString = "AuthType=ClientSecret;Url=https://x.crm.dynamics.com;TenantId=t;ClientId=c;ClientSecret=s";

    // ---- plan / apply require --solution ----

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void PlanAndApply_WithoutSolution_FailWithInvalidArguments_AndNamePROJECTmd(string command)
    {
        WithPackage(package =>
        {
            (bool ok, int exit, string error) = Parse(command, ["--package", package, "--connection-string", ConnectionString]);

            Assert.False(ok);
            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains($"vela {command} requires --solution <uniquename>", error);
            Assert.Contains("PROJECT.md", error);
            Assert.Contains($"one 'vela {command} -p <package> --solution <name>' per solution", error);
        });
    }

    [Theory]
    [InlineData("plan", "--solution")]
    [InlineData("apply", "--solution-unique-name")]
    public void PlanAndApply_WithSolution_Parse(string command, string option)
    {
        WithPackage(package =>
        {
            (bool ok, int exit, _) = Parse(command, ["--package", package, option, "ZavaCore", "--connection-string", ConnectionString], out SharedOptions options);

            Assert.True(ok);
            Assert.Equal(ExitSuccess, exit);
            Assert.Equal("ZavaCore", options.SolutionUniqueName);
        });
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void PlanAndApply_Interactive_PromptForTheSolution(string command)
    {
        WithPackage(package =>
        {
            (bool ok, _, _) = Parse(command, ["--package", package, "--interactive", "--connection-string", ConnectionString], out SharedOptions options, stdin: "ZavaCore\n");

            Assert.True(ok);
            Assert.Equal("ZavaCore", options.SolutionUniqueName);
        });
    }

    [Fact]
    public void PlanAndApply_Interactive_StillFail_WhenTheSolutionPromptIsLeftEmpty()
    {
        WithPackage(package =>
        {
            (bool ok, int exit, string error) = Parse("plan", ["--package", package, "--interactive", "--connection-string", ConnectionString], stdin: "\n");

            Assert.False(ok);
            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("vela plan requires --solution", error);
        });
    }

    [Fact]
    public void Inspect_DoesNotRequireTheSolution()
    {
        WithPackage(package =>
        {
            (bool ok, _, _) = Parse("inspect", ["--package", package, "--connection-string", ConnectionString], out SharedOptions options);

            Assert.True(ok);
            Assert.Null(options.SolutionUniqueName);
        });
    }

    [Fact]
    public void Inspect_WithWebResources_StillRequiresTheSolution()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vela-wr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            (bool ok, int exit, string error) = Parse("inspect", ["--webresources", folder, "--connection-string", ConnectionString]);

            Assert.False(ok);
            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("--webresources requires --solution", error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- add customapi requires --prefix ----

    [Fact]
    public void AddCustomApi_WithoutPrefix_Fails_AndNeverGuessesFromTheProjectPrefix()
    {
        // "Acme" would derive the valid "acme": it is not used.
        RunInProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>", (project, dir) =>
        {
            (int exit, string error) = CaptureError(() => AddCommand.Run(["customapi", "Recalc", "--global", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("vela add customapi requires --prefix <publisherprefix>", error);
            Assert.Contains("vela solution show <solution> --json", error);
            Assert.DoesNotContain("VelaPublisherPrefix", error);
            Assert.False(File.Exists(Path.Combine(dir, "GlobalCustomApis", "Recalc.cs")));
        });
    }

    [Fact]
    public void AddCustomApi_WithoutPrefix_QuotesALegacyVelaPublisherPrefix_WithoutUsingIt()
    {
        RunInProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Zava.Plugins</RootNamespace><VelaPublisherPrefix>zs</VelaPublisherPrefix></PropertyGroup></Project>", (project, dir) =>
        {
            (int exit, string error) = CaptureError(() => AddCommand.Run(["customapi", "Recalc", "--global", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("requires --prefix", error);
            Assert.Contains("<VelaPublisherPrefix>zs</VelaPublisherPrefix>", error);
            Assert.Contains("pass --prefix zs", error);
            Assert.False(File.Exists(Path.Combine(dir, "GlobalCustomApis", "Recalc.cs")));
        });
    }

    [Fact]
    public void AddCustomApi_StillValidatesThePrefix()
    {
        RunInProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>", (project, dir) =>
        {
            (int exit, string error) = CaptureError(() => AddCommand.Run(["customapi", "Recalc", "--global", "--prefix", "zavasales", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("Invalid --prefix 'zavasales'", error);
            Assert.False(File.Exists(Path.Combine(dir, "GlobalCustomApis", "Recalc.cs")));
        });
    }

    [Fact]
    public void AddTest_CustomApi_WithAnUnreadableUniqueName_RequiresThePrefix()
    {
        // The unique name comes from another file, so the test falls back to <prefix>_<Name>: that needs --prefix.
        string root = Path.Combine(Path.GetTempPath(), "vela-addtest-prefix-" + Guid.NewGuid().ToString("N"));
        try
        {
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            string testsDir = Path.Combine(root, "tests", "Acme.Plugins.Tests");
            Directory.CreateDirectory(Path.Combine(pluginsDir, "GlobalCustomApis"));
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(root, "Acme.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(pluginsDir, "Acme.Plugins.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            string tests = Path.Combine(testsDir, "Acme.Plugins.Tests.csproj");
            File.WriteAllText(tests,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins.Tests</RootNamespace><VelaPublisherPrefix>zs</VelaPublisherPrefix></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"..\\..\\src\\Acme.Plugins\\Acme.Plugins.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(pluginsDir, "GlobalCustomApis", "Recalc.cs"),
                "namespace Acme.Plugins.GlobalCustomApis\n{\n    public sealed class Recalc : CustomApiBase\n    {\n" +
                "        public Recalc(string u, string s) : base(u, s) { }\n\n" +
                "        protected override void RegisterCustomApi() => RegisterCustomApi(ApiNames.Recalc, Handle).AsAction();\n    }\n}\n");

            (int exit, string error) = CaptureError(() => AddCommand.Run(["test", "Recalc", "--kind", "customapi", "--project", tests]));
            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("vela add test Recalc --kind customapi", error);
            Assert.Contains("requires --prefix", error);
            Assert.Contains("pass --prefix zs", error);

            (int withPrefix, _) = CaptureError(() => AddCommand.Run(["test", "Recalc", "--kind", "customapi", "--prefix", "acme", "--project", tests]));
            Assert.Equal(ExitSuccess, withPrefix);
            string content = File.ReadAllText(Path.Combine(testsDir, "GlobalCustomApis", "RecalcTests.cs"));
            Assert.Contains("acme_Recalc", content);
            Assert.DoesNotContain("zs_Recalc", content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ---- vela init writes PROJECT.md ----

    [Fact]
    public void Init_WithSolution_WritesPROJECTmd_MappingEveryPackageToIt()
    {
        RunInit(["X", "--prefix", "X", "--publisher-prefix", "xs", "--solution", "XSol", "--integrations"], root =>
        {
            Assert.Equal(
                "---\n" +
                "schemaVersion: 1\n" +
                "publisherPrefix: xs\n" +
                "packages:\n" +
                "  xs_X.Plugins: XSol\n" +
                "  xs_X.Integrations: XSol\n" +
                "---\n" +
                "\n" +
                "# Project\n" +
                "\n" +
                "Values the Vela commands need. Pass them as flags: `--solution` on plan/apply, `--prefix` on\n" +
                "add customapi. Check a solution's prefix with `vela solution show <solution> --json`. The\n" +
                "environment URL is per user and never lives in the repo.\n" +
                "\n" +
                "Vela writes this file once, at `vela init`, and never edits it afterwards. Everything below the\n" +
                "front matter belongs to the team.\n",
                File.ReadAllText(Path.Combine(root, "PROJECT.md")));

            // No project file records the prefix any more; the package ids keep it.
            string[] csprojs = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
            Assert.NotEmpty(csprojs);
            Assert.All(csprojs, csproj => Assert.DoesNotContain("VelaPublisherPrefix", File.ReadAllText(csproj)));
            Assert.Contains("<PackageId>xs_X.Plugins</PackageId>", File.ReadAllText(Path.Combine(root, "src", "X.Plugins", "X.Plugins.csproj")));
            Assert.Contains("<PackageId>xs_X.Integrations</PackageId>", File.ReadAllText(Path.Combine(root, "src", "X.Integrations", "X.Integrations.csproj")));

            // Tracked like every generated file, so `init --force` only rewrites it while unedited.
            Assert.Contains("\"PROJECT.md\"", File.ReadAllText(Path.Combine(root, ".vela", "scaffold-manifest.json")));

            string agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
            Assert.Contains("Project values live in `PROJECT.md`; pass them as flags, do not ask for them again", agents);
            Assert.Contains("vela solution show <solution> --json", agents);
            Assert.Contains("vela plan -p artifacts/xs_X.Plugins.1.0.0.nupkg --solution <solution of xs_X.Plugins> --json", agents);
            Assert.Contains("vela plan -p artifacts/xs_X.Integrations.1.0.0.nupkg --solution <solution of xs_X.Integrations> --json", agents);
            Assert.DoesNotContain("Always pass `--solution`", agents);

            Assert.Contains("vela plan --packages-dir artifacts --solution XSol", File.ReadAllText(Path.Combine(root, "README.md")));
        });
    }

    [Fact]
    public void Init_WithoutSolutionOrPublisherPrefix_LeavesTheValuesEmptyWithAComment()
    {
        RunInit(["ZavaSales", "--prefix", "Zava"], root =>
        {
            string text = File.ReadAllText(Path.Combine(root, "PROJECT.md"));
            Assert.StartsWith("---\nschemaVersion: 1\npublisherPrefix:  # the publisher prefix of your solution: vela solution show <solution> --json\npackages:\n  Zava.Plugins:  # the unique name of the solution this package deploys into\n---\n", text);
            Assert.DoesNotContain("Integrations", text);
            Assert.Contains("\n# Project\n", text);
        });
    }

    [Fact]
    public void Init_Samples_RecordsThePrefixThePackagesAlreadyCarry()
    {
        // --samples names the packages and the sample Custom APIs with the derived prefix: PROJECT.md records that one.
        RunInit(["ZavaSales", "--prefix", "Zava", "--samples"], root =>
        {
            string text = File.ReadAllText(Path.Combine(root, "PROJECT.md"));
            Assert.Contains("\npublisherPrefix: zava\n", text);
            Assert.Contains("\n  zava_Zava.Plugins:  # the unique name of the solution this package deploys into\n", text);
            Assert.Contains("\n  zava_Zava.Integrations:  #", text);
            Assert.Contains("vela add customapi RecalculateTotals --prefix zava", File.ReadAllText(Path.Combine(root, "README.md")));
        });
    }

    [Fact]
    public void Init_Force_KeepsAnEditedPROJECTmd()
    {
        RunInit(["ZavaSales", "--prefix", "Zava", "--solution", "ZavaCore"], root =>
        {
            string path = Path.Combine(root, "PROJECT.md");
            File.AppendAllText(path, "\nOur notes.\n");
            string edited = File.ReadAllText(path);

            Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--solution", "Other", "--force", "--output", Path.GetDirectoryName(root)!, "--core-version", "1.0.0"], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));

            Assert.Equal(edited, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData("1Sol")]
    [InlineData("Zava-Core")]
    [InlineData("Zava.Core")]
    public void Init_RejectsAnInvalidSolutionUniqueName(string solution)
    {
        (int exit, string error) = CaptureError(() =>
            ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--solution", solution], out _, out int code) ? ExitSuccess : code);

        Assert.Equal(ExitInvalidArguments, exit);
        Assert.Contains($"Invalid --solution '{solution}'", error);
    }

    [Theory]
    [InlineData("ZavaCore", true)]
    [InlineData("_core", true)]
    [InlineData("zava_core_2", true)]
    [InlineData("", false)]
    [InlineData("2core", false)]
    [InlineData("zava core", false)]
    public void IsValidSolutionUniqueName(string value, bool expected)
        => Assert.Equal(expected, ScaffoldOptions.IsValidSolutionUniqueName(value));

    [Fact]
    public void IsValidSolutionUniqueName_CapsTheLengthAt50()
    {
        Assert.True(ScaffoldOptions.IsValidSolutionUniqueName(new string('a', 50)));
        Assert.False(ScaffoldOptions.IsValidSolutionUniqueName(new string('a', 51)));
    }

    [Fact]
    public void Help_ListsInitSolution_AndMarksPlanApplySolutionRequired()
    {
        Assert.Contains(CliCommands.Find("init")!.Options, o => o.Name == "--solution");
        foreach (string command in new[] { "plan", "apply" })
        {
            CliOption solution = CliCommands.Find(command)!.Options.Single(o => o.Name == "--solution");
            Assert.StartsWith("Required", solution.Description);
            Assert.Contains("--solution <uniquename>", CliCommands.Find(command)!.Usage[0]);
        }

        Assert.DoesNotContain("Required", CliCommands.Find("inspect")!.Options.Single(o => o.Name == "--solution").Description);
        Assert.DoesNotContain("VelaPublisherPrefix", string.Join("\n", CliCommands.Find("add")!.Options.Select(o => o.Description).Concat(CliCommands.Find("add")!.Notes!)));
    }

    // ---- helpers ----

    private static (bool Ok, int Exit, string Error) Parse(string command, string[] args, string stdin = "") =>
        Parse(command, args, out _, stdin);

    private static (bool Ok, int Exit, string Error) Parse(string command, string[] args, out SharedOptions options, string stdin = "")
    {
        TextReader originalIn = Console.In;
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        StringWriter error = new();
        try
        {
            Console.SetIn(new StringReader(stdin));
            Console.SetOut(new StringWriter());
            Console.SetError(error);
            bool ok = CommandLineOptions.TryParseSharedOptions(command, args, out options, out int exit);
            return (ok, exit, error.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static (int Exit, string Error) CaptureError(Func<int> run)
    {
        TextWriter original = Console.Error;
        StringWriter capture = new();
        Console.SetError(capture);
        try
        {
            return (run(), capture.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static void WithPackage(Action<string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string package = Path.Combine(dir, "Zava.Plugins.1.0.0.nupkg");
            File.WriteAllBytes(package, [0x50, 0x4b]);
            action(package);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void RunInProject(string csproj, Action<string, string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-add-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string project = Path.Combine(dir, "Project.csproj");
            File.WriteAllText(project, csproj);
            action(project, dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void RunInit(string[] args, Action<string> action)
    {
        string temp = Path.Combine(Path.GetTempPath(), "vela-init-pm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        TextWriter originalOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            Assert.True(ScaffoldOptions.TryParse([.. args, "--output", temp, "--core-version", "1.0.0"], out ScaffoldOptions options, out _));
            Assert.Equal(ExitSuccess, ProjectScaffolder.Generate(options));
            action(Path.Combine(temp, options.SolutionName));
        }
        finally
        {
            Console.SetOut(originalOut);
            Directory.Delete(temp, recursive: true);
        }
    }
}
