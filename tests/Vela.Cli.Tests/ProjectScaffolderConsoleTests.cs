using System;
using System.IO;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// Scaffolder tests that capture the process-global Console.Out. They run in the ConsoleRedirect collection (no
// parallelism), so another test writing to the console can neither leak into the capture nor lose its own output.
[Collection("ConsoleRedirect")]
public class ProjectScaffolderConsoleTests
{
    private static ScaffoldOptions Options(string outputDir) => new(
        SolutionName: "ZavaSales",
        Prefix: "Zava",
        OutputDirectory: outputDir,
        IncludeConsole: true,
        IncludeIntegrations: true,
        IncludeTests: true,
        CoreVersion: "1.0.0",
        Force: false,
        Interactive: false,
        IncludeSamples: true);

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
