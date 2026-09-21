using System;
using System.IO;
using Microsoft.Identity.Client;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Cross-platform interactive token cache (BUG-7 fix): the cache file lives under the config dir so it is scoped
/// to <c>$VELA_CONFIG_DIR</c> (isolated in tests) and a single <c>logout</c> / <c>--fresh-login</c> removes it.
/// </summary>
[Collection("ConfigDir")]
public class InteractiveTokenCacheTests
{
    [Fact]
    public void Clear_deletes_the_cache_file_and_tolerates_absence()
    {
        InTempConfigDir(dir =>
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, InteractiveTokenCache.CacheFileName);

            InteractiveTokenCache.Clear(); // absent -> best-effort, no throw

            File.WriteAllText(cache, "cache-bytes");
            Assert.True(File.Exists(cache));

            InteractiveTokenCache.Clear();
            Assert.False(File.Exists(cache));
        });
    }

    [Fact]
    public void RegisterOn_wires_a_persistent_cache_without_throwing()
    {
        InTempConfigDir(dir =>
        {
            Directory.CreateDirectory(dir);

            IPublicClientApplication app = PublicClientApplicationBuilder
                .Create(CommandLineOptions.DefaultInteractiveClientId)
                .WithAuthority("https://login.microsoftonline.com/common")
                .WithDefaultRedirectUri()
                .Build();

            // Must set up the file-backed cache on the current OS without throwing (Windows DPAPI file, or a
            // user-only plain file on macOS/Linux). No token is acquired, so nothing is written yet.
            InteractiveTokenCache.RegisterOn(app);
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
}
