using System;
using System.IO;
using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>
/// The <c>vela add --json</c> success reports. Captures the process-global <see cref="Console.Out"/>, so it runs in
/// the serial console-redirect collection.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class AddCommandJsonTests
{
    [Fact]
    public void Add_Plugin_Json_Reports_The_Created_File()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string output = CaptureOut(() =>
                Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountValidator", "--entity", "Account", "--filtering-attributes", "Name", "--project", project, "--json"])));

            using JsonDocument doc = JsonDocument.Parse(output);
            Assert.Contains("plugin AccountValidator", doc.RootElement.GetProperty("description").GetString());
            string created = doc.RootElement.GetProperty("created")[0].GetString()!;
            Assert.EndsWith(Path.Combine("Account", "Plugins", "AccountValidator.cs"), created);
            Assert.True(File.Exists(created));
        });
    }

    [Fact]
    public void Add_Service_Json_Suppresses_The_Registration_Hint()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string output = CaptureOut(() =>
                Assert.Equal(ExitSuccess, AddCommand.Run(["service", "AccountFollowUp", "--entity", "Account", "--project", project, "--json"])));

            using JsonDocument doc = JsonDocument.Parse(output);
            Assert.Contains("service AccountFollowUp", doc.RootElement.GetProperty("description").GetString());
            // The human "Register it from ConfigureServices" hint must not appear in JSON mode.
            Assert.DoesNotContain("ConfigureServices", output);
        });
    }

    private static string CaptureOut(Action action)
    {
        TextWriter original = Console.Out;
        using StringWriter capture = new StringWriter();
        Console.SetOut(capture);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return capture.ToString();
    }

    private static void RunInProject(string rootNamespace, Action<string, string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-add-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string project = Path.Combine(dir, $"{rootNamespace}.csproj");
            File.WriteAllText(project,
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>{rootNamespace}</RootNamespace></PropertyGroup></Project>");
            action(project, dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }
}
