using System;
using System.Net.Http;
using System.Text.Json;

namespace Vela.Cli;

/// <summary>
/// Best-effort "a newer Vela.Cli is available" notice printed to stderr after a command runs. A global .NET tool
/// cannot update itself, so this only points at <c>dotnet tool update -g Vela.Cli</c>. The nuget.org check is
/// throttled to once per <see cref="CheckInterval"/> (the result is cached in the user config), never blocks or
/// fails a command, and is silent for machine output (<c>--json</c>), a dev build, or when
/// <c>VELA_NO_UPDATE_CHECK</c> is set.
/// </summary>
internal static class UpdateNotifier
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const string PackageId = "vela.cli";

    /// <summary>
    /// Prints an upgrade notice to stderr when a newer stable version exists. Best-effort: any failure (offline,
    /// parse, IO) is swallowed so the command's own result is never affected.
    /// </summary>
    public static void MaybeNotify(string currentVersion, bool isJsonOutput)
    {
        try
        {
            if (isJsonOutput
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELA_NO_UPDATE_CHECK"))
                || !Version.TryParse(currentVersion, out Version? current)
                || current == new Version(0, 0, 0))
            {
                return; // machine output, opted out, or a local/dev build with no real version
            }

            string? latest = GetLatestVersion();
            if (latest != null && ShouldNotify(current, latest))
            {
                Console.Error.WriteLine(FormatMessage(currentVersion, latest));
            }
        }
        catch
        {
            // Never let an update check affect the command.
        }
    }

    /// <summary>True when <paramref name="latest"/> parses to a strictly greater version than <paramref name="current"/>.</summary>
    internal static bool ShouldNotify(Version current, string? latest) =>
        Version.TryParse(latest, out Version? parsed) && parsed > current;

    /// <summary>The upgrade notice text (kept pure for testing).</summary>
    internal static string FormatMessage(string currentVersion, string latestVersion) =>
        $"A newer version of {ToolInfo.ToolName} is available: {latestVersion} (you have {currentVersion}). " +
        $"Update with: dotnet tool update -g Vela.Cli";

    // Returns the latest known stable CLI version, using the cached value unless the throttle window has elapsed,
    // in which case nuget.org is queried once and the cache refreshed. Returns null when nothing is known.
    private static string? GetLatestVersion()
    {
        (DateTimeOffset? lastCheck, string? cached) = UserConfig.GetUpdateCheckState();
        if (lastCheck.HasValue && DateTimeOffset.UtcNow - lastCheck.Value < CheckInterval)
        {
            return cached; // within the throttle window: use the cached value, no network call
        }

        string? latest = ResolveLatestStableFromNuGet();
        // Record the check even when the fetch failed (latest == null keeps the previous cached value out of the
        // way) so we don't hammer nuget.org on every command when offline.
        UserConfig.SetUpdateCheckState(DateTimeOffset.UtcNow, latest ?? cached);
        return latest ?? cached;
    }

    private static string? ResolveLatestStableFromNuGet()
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
            string json = http.GetStringAsync($"https://api.nuget.org/v3-flatcontainer/{PackageId}/index.json")
                .GetAwaiter().GetResult();
            using JsonDocument doc = JsonDocument.Parse(json);

            Version? best = null;
            string? bestStr = null;
            foreach (JsonElement element in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                string? candidate = element.GetString();
                if (string.IsNullOrEmpty(candidate) || candidate.Contains('-'))
                {
                    continue; // skip prerelease
                }

                if (Version.TryParse(candidate, out Version? parsed) && (best is null || parsed > best))
                {
                    best = parsed;
                    bestStr = candidate;
                }
            }

            return bestStr;
        }
        catch
        {
            return null;
        }
    }
}
