using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>Covers <c>vela logout</c>: it clears the token cache + remembered URL and validates its args.</summary>
public class LogoutCommandTests
{
    [Fact]
    public void Run_ClearsTokenCacheAndRememberedUrl()
    {
        InTempConfigDir(dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(UserConfig.TokenCachePath, "cache-bytes");
            UserConfig.SetLastDataverseUrl("https://zava.crm.dynamics.com/");

            int exit = LogoutCommand.Run([]);

            Assert.Equal(ExitSuccess, exit);
            Assert.False(File.Exists(UserConfig.TokenCachePath));
            Assert.Null(UserConfig.GetLastDataverseUrl());
        });
    }

    [Fact]
    public void Run_Help_Succeeds()
    {
        Assert.Equal(ExitSuccess, LogoutCommand.Run(["--help"]));
    }

    [Fact]
    public void Run_RejectsUnknownOption()
    {
        Assert.Equal(ExitInvalidArguments, LogoutCommand.Run(["--wat"]));
    }

    private static void InTempConfigDir(Action<string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-cfg-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("VELA_CONFIG_DIR");
        Environment.SetEnvironmentVariable("VELA_CONFIG_DIR", dir);
        try
        {
            action(dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VELA_CONFIG_DIR", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
