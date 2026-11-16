using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// What the CLI prints from environment data cannot drive the terminal (control characters and ANSI escape sequences
/// are escaped), the options record does not print its secrets, and the package scan extracts into a private,
/// per-run temp directory.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class CliOutputHardeningTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\u001b[31mred", "a\\u001b[31mred")]
    [InlineData("over\rwrite", "over\\u000dwrite")]
    [InlineData("two\nlines", "two\\u000alines")]
    [InlineData("c1\u009bcsi", "c1\\u009bcsi")]
    [InlineData("tab\tkept", "tab\tkept")]
    public void EscapeControl_makes_every_control_character_visible(string raw, string expected) =>
        Assert.Equal(expected, OutputWriter.EscapeControl(raw));

    [Fact]
    public void EscapeControl_can_keep_line_breaks_but_never_a_lone_carriage_return()
    {
        Assert.Equal("a\nb\r\nc\\u000dd\\u001b", OutputWriter.EscapeControl("a\nb\r\nc\rd\u001b", keepNewlines: true));
        Assert.Equal(string.Empty, OutputWriter.EscapeControl(null));
    }

    [Fact]
    public void The_plan_summary_escapes_names_read_from_the_environment()
    {
        // A live step whose name and type carry an escape sequence (OSC title set, screen clear).
        JsonElement definition = JsonSerializer.SerializeToElement(new
        {
            pluginTypeName = $"Zava{Esc}]0;pwned\u0007", name = $"Step{Esc}[2J", message = "Update", stepKey = "k",
        });
        RegistrationItem removed = new("step", $"Zava{Esc}]0;pwned\u0007", definition);
        RegistrationDiff diff = new([], [], [], [removed], [], [], []);
        PlanPreview preview = OutputWriter.BuildPlanPreview("plan", $"https://zava{Esc}[31m", diff, [$"warn{Esc}[1m"], [], info: [$"info{Esc}[0m"]);

        StringWriter writer = new();
        OutputWriter.WritePlanSummary(preview, writer, color: false);

        string output = writer.ToString();
        Assert.DoesNotContain(Esc, output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", output, StringComparison.Ordinal);
        Assert.Contains("Step\\u001b[2J", output);
        Assert.Contains("warn\\u001b[1m", output);
    }

    [Fact]
    public void Fail_escapes_a_message_and_keeps_its_lines()
    {
        TextWriter original = Console.Error;
        StringWriter error = new();
        Console.SetError(error);
        try
        {
            OutputWriter.Fail($"first{Esc}[2K\nsecond", CliExitCodes.ExitError, includeUsage: false);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.DoesNotContain(Esc, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("first\\u001b[2K\nsecond", error.ToString());
    }

    [Fact]
    public void The_logs_command_escapes_trace_text_written_by_plugin_code()
    {
        TextWriter original = Console.Out;
        StringWriter output = new();
        Console.SetOut(output);
        try
        {
            LogsCommand.PrintHuman(
            [
                new Entity("plugintracelog", Guid.NewGuid())
                {
                    ["typename"] = $"Zava{Esc}[31m.Plugin",
                    ["messagename"] = "Update",
                    ["exceptiondetails"] = $"boom{Esc}[2J",
                    ["messageblock"] = $"trace{Esc}]0;title\u0007",
                },
            ], jobs: false, full: true);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.DoesNotContain(Esc, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("boom\\u001b[2J", output.ToString());
    }

    [Fact]
    public void SharedOptions_ToString_prints_no_secret()
    {
        SharedOptions options = new(["a.nupkg"], null, "AuthType=ClientSecret;ClientSecret=hunter2;Url=https://zava.crm.dynamics.com",
            null, null, "ZavaSales", "https://zava.crm.dynamics.com", null, AutoApprove: true, Interactive: false, Strict: false,
            Verbose: false, SecureConfigs: ["ErpKey=s3cret"]);

        string text = options.ToString();

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("ConnectionString = ***", text);
        Assert.Contains("SolutionUniqueName = ZavaSales", text);
        Assert.Contains("SecureConfigs = 1 value(s), redacted", text);
    }

    [Fact]
    public void A_private_temp_directory_is_new_per_run_and_not_under_a_shared_parent()
    {
        string first = PackageScanner.CreatePrivateTempDirectory("vela-pkg-");
        string second = PackageScanner.CreatePrivateTempDirectory("vela-pkg-");
        try
        {
            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(first));
            Assert.StartsWith("vela-pkg-", Path.GetFileName(first), StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(first));
            }
        }
        finally
        {
            Directory.Delete(first);
            Directory.Delete(second);
        }
    }

    [Fact]
    public void ExtractPackage_extracts_into_a_private_directory_not_the_shared_vela_tools_folder()
    {
        string nupkg = Path.Combine(Path.GetTempPath(), $"vela-hardening-{Guid.NewGuid():N}.nupkg");
        using (ZipArchive zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            using (StreamWriter writer = new(zip.CreateEntry("Zava.Plugins.nuspec").Open()))
            {
                writer.Write("<?xml version=\"1.0\"?><package><metadata><id>Zava.Plugins</id><version>1.0.0</version></metadata></package>");
            }

            using (Stream dll = zip.CreateEntry("lib/net462/Zava.Plugins.dll").Open())
            {
                dll.Write(Encoding.UTF8.GetBytes("not a real assembly"));
            }
        }

        List<string> temps = [];
        try
        {
            ExtractedPackage package = PackageScanner.ExtractPackage(nupkg, null, temps);

            string directory = Assert.Single(temps);
            Assert.DoesNotContain("vela-tools", directory, StringComparison.Ordinal);
            Assert.StartsWith("vela-pkg-", Path.GetFileName(directory), StringComparison.Ordinal);
            Assert.Equal(directory, Path.GetDirectoryName(package.PrimaryAssemblyPath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            }
        }
        finally
        {
            PackageScanner.CleanupTempDirectories(temps);
            File.Delete(nupkg);
        }
    }
}
