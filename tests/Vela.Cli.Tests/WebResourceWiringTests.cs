using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>Argument wiring for the web-resource deploy kind on plan/apply (--webresources).</summary>
public class WebResourceWiringTests
{
    [Fact]
    public void WebResources_MissingFolder_Fails()
    {
        string missing = Path.Combine(Path.GetTempPath(), "vela-nope-" + Guid.NewGuid().ToString("N"));

        bool ok = CommandLineOptions.TryParseSharedOptions(
            ["--webresources", missing, "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"],
            out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitFileNotFound, exitCode);
    }

    [Fact]
    public void WebResources_WithoutSolution_Fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-wr-nosol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            bool ok = CommandLineOptions.TryParseSharedOptions(
                ["--webresources", dir, "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"],
                out _, out int exitCode);

            Assert.False(ok);
            Assert.Equal(ExitInvalidArguments, exitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WebResources_WithoutPackage_IsAllowed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-wr-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A web-resources root makes plugin packages optional: no "Missing package input" failure.
            bool ok = CommandLineOptions.TryParseSharedOptions(
                ["--webresources", dir, "--solution", "MySol", "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"],
                out SharedOptions options, out int exitCode);

            Assert.True(ok);
            Assert.Equal(ExitSuccess, exitCode);
            Assert.Equal(Path.GetFullPath(dir), options.WebResourcesRoot);
            Assert.Empty(options.PackagePaths);
            Assert.Equal("MySol", options.SolutionUniqueName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
