using System.Reflection;

namespace Vela.Cli;

/// <summary>
/// Branding and version metadata for the CLI. Version is read from the assembly so it tracks the
/// <c>&lt;Version&gt;</c> in the project file without a second source of truth.
/// </summary>
internal static class ToolInfo
{
    public const string ToolName = "vela";
    public const string ProductName = "Vela CLI";
    public const string Author = "allan.de.castro";

    /// <summary>The tool version (informational version, with any build-metadata suffix trimmed).</summary>
    public static string Version
    {
        get
        {
            Assembly assembly = typeof(ToolInfo).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string version = informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";

            int plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }
}
