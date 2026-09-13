using System;
using System.IO;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>Covers the persisted user preferences (<c>~/.vela/config.json</c>) and the prompt-default plumbing.</summary>
[Xunit.Collection("ConfigDir")]
public class UserConfigTests
{
    [Fact]
    public void SetThenGet_RoundTripsTheLastUrl()
    {
        InTempConfigDir(dir =>
        {
            Assert.Null(UserConfig.GetLastDataverseUrl());

            UserConfig.SetLastDataverseUrl("https://zava.crm.dynamics.com/");

            Assert.Equal("https://zava.crm.dynamics.com/", UserConfig.GetLastDataverseUrl());
            Assert.True(File.Exists(Path.Combine(dir, "config.json")));
        });
    }

    [Fact]
    public void Clear_RemovesTheRememberedUrl()
    {
        InTempConfigDir(_ =>
        {
            UserConfig.SetLastDataverseUrl("https://zava.crm.dynamics.com/");
            UserConfig.Clear();

            Assert.Null(UserConfig.GetLastDataverseUrl());
        });
    }

    [Fact]
    public void Get_ReturnsNull_OnCorruptFile()
    {
        InTempConfigDir(dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ not valid json");

            Assert.Null(UserConfig.GetLastDataverseUrl());
        });
    }

    [Fact]
    public void SetLastDataverseUrl_IgnoresBlank()
    {
        InTempConfigDir(_ =>
        {
            UserConfig.SetLastDataverseUrl("   ");
            Assert.Null(UserConfig.GetLastDataverseUrl());
        });
    }

    [Fact]
    public void Prompt_EmptyInput_AcceptsTheDefault()
    {
        // Tests run with redirected stdin; an empty line accepts the remembered default.
        WithStdin("\n", () =>
            Assert.Equal("https://remembered.crm.dynamics.com/",
                OutputWriter.Prompt("Dataverse URL:", defaultValue: "https://remembered.crm.dynamics.com/")));
    }

    [Fact]
    public void Prompt_NonEmptyInput_OverridesTheDefault()
    {
        WithStdin("https://typed.crm.dynamics.com/\n", () =>
            Assert.Equal("https://typed.crm.dynamics.com/",
                OutputWriter.Prompt("Dataverse URL:", defaultValue: "https://remembered.crm.dynamics.com/")));
    }

    [Fact]
    public void TokenCachePath_IsUnderConfigDir()
    {
        InTempConfigDir(dir =>
            Assert.Equal(Path.Combine(dir, "token.dat"), UserConfig.TokenCachePath));
    }

    [Fact]
    public void ClearTokenCache_RemovesTheTokenFile_AndToleratesAbsence()
    {
        InTempConfigDir(dir =>
        {
            UserConfig.ClearTokenCache(); // no file yet -> best-effort, no throw

            Directory.CreateDirectory(dir);
            File.WriteAllText(UserConfig.TokenCachePath, "cache-bytes");
            Assert.True(File.Exists(UserConfig.TokenCachePath));

            UserConfig.ClearTokenCache();
            Assert.False(File.Exists(UserConfig.TokenCachePath));
        });
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

    private static void WithStdin(string input, Action action)
    {
        TextReader original = Console.In;
        Console.SetIn(new StringReader(input));
        try
        {
            action();
        }
        finally
        {
            Console.SetIn(original);
        }
    }
}
