using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vela.Cli;

/// <summary>
/// Small persisted user preferences under <c>~/.vela/config.json</c> (or <c>$VELA_CONFIG_DIR</c>), so a repeat
/// interactive run does not have to re-type things it already knows - currently the last Dataverse URL. This is
/// preference data, not secrets: the URL is not sensitive, so a plain JSON file is fine. Reads/writes are
/// best-effort; a corrupt or unreadable file is treated as "no preference" rather than failing the command.
/// </summary>
internal static class UserConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The config directory: <c>$VELA_CONFIG_DIR</c> if set, else <c>~/.vela</c>.</summary>
    public static string ConfigDir
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("VELA_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                return overrideDir!;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".vela");
        }
    }

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>The MSAL token cache file the Dataverse SDK persists interactive-login tokens to (OAuth only).</summary>
    public static string TokenCachePath => Path.Combine(ConfigDir, "token.dat");

    /// <summary>Ensures the config directory exists so the SDK can write the token cache into it.</summary>
    public static void EnsureConfigDir()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
        }
        catch
        {
            // Best-effort: the SDK also creates the path; a failure here surfaces later as a connection error.
        }
    }

    /// <summary>Deletes the persisted interactive-login token cache (used by <c>vela logout</c>). Best-effort.</summary>
    public static void ClearTokenCache()
    {
        try
        {
            if (File.Exists(TokenCachePath))
            {
                File.Delete(TokenCachePath);
            }
        }
        catch
        {
            // Best-effort: nothing to do if it cannot be removed.
        }
    }

    /// <summary>The last Dataverse URL used interactively, or null when none is remembered / the file is unreadable.</summary>
    public static string? GetLastDataverseUrl() => Read()?.LastDataverseUrl;

    /// <summary>Remembers the last Dataverse URL (best-effort; a write failure is swallowed).</summary>
    public static void SetLastDataverseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        ConfigData data = Read() ?? new ConfigData();
        if (string.Equals(data.LastDataverseUrl, url, StringComparison.Ordinal))
        {
            return; // no change, avoid a needless write
        }

        data.LastDataverseUrl = url;
        Write(data);
    }

    /// <summary>The cached update-check state: when nuget.org was last queried and the latest CLI version it reported.</summary>
    public static (DateTimeOffset? LastCheckUtc, string? LatestVersion) GetUpdateCheckState()
    {
        ConfigData? data = Read();
        return (data?.LastUpdateCheckUtc, data?.LatestKnownCliVersion);
    }

    /// <summary>Records an update check (timestamp + the latest CLI version seen). Best-effort.</summary>
    public static void SetUpdateCheckState(DateTimeOffset checkedUtc, string? latestVersion)
    {
        ConfigData data = Read() ?? new ConfigData();
        data.LastUpdateCheckUtc = checkedUtc;
        data.LatestKnownCliVersion = latestVersion;
        Write(data);
    }

    /// <summary>Clears all persisted preferences (deletes config.json). Best-effort.</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Delete(ConfigPath);
            }
        }
        catch
        {
            // Best-effort: nothing to do if it cannot be removed.
        }
    }

    private static ConfigData? Read()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(ConfigPath), JsonOptions);
        }
        catch
        {
            return null; // corrupt / unreadable -> treat as no preference
        }
    }

    private static void Write(ConfigData data)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch
        {
            // Best-effort: a failure to remember is never fatal to the command.
        }
    }

    private sealed class ConfigData
    {
        [JsonPropertyName("lastDataverseUrl")]
        public string? LastDataverseUrl { get; set; }

        [JsonPropertyName("lastUpdateCheckUtc")]
        public DateTimeOffset? LastUpdateCheckUtc { get; set; }

        [JsonPropertyName("latestKnownCliVersion")]
        public string? LatestKnownCliVersion { get; set; }
    }
}
