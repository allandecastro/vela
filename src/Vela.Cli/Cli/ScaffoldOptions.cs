using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Parsed options for the <c>vela init</c> scaffolding command. Unlike the other commands this one
/// does not touch Dataverse, so it has its own parser rather than reusing <see cref="CommandLineOptions.TryParseSharedOptions"/>.
/// </summary>
/// <param name="SolutionName">Name of the generated solution (also the root folder and .sln file name).</param>
/// <param name="Prefix">Namespace / assembly-name prefix for the generated projects.</param>
/// <param name="OutputDirectory">Directory the solution folder is created under.</param>
/// <param name="IncludeConsole">When true, generate the ConsoleApp project.</param>
/// <param name="IncludeIntegrations">When true, generate the Integrations project.</param>
/// <param name="IncludeTests">When true, generate the Plugins.Tests project (Vela.Testing).</param>
/// <param name="CoreVersion">Version of the <c>Vela.Plugins</c> package the projects reference.</param>
/// <param name="Force">When true, overwrite an existing target directory.</param>
/// <param name="Interactive">When true, missing values are prompted for.</param>
/// <param name="ManagedIdentity">When true, mark the Integrations package as managed-identity-enabled and add the build-time package-signing step it requires.</param>
/// <param name="IncludeWebResources">When true, scaffold the opt-in <c>{prefix}.WebResources</c> TypeScript project.</param>
/// <param name="WebResourceNamespace">The dotted JS global name (esbuild <c>globalName</c>) the bundle exposes; the consumer's publisher prefix, never "Vela".</param>
/// <param name="WebResourcesFolder">The build output folder (tsup <c>outDir</c>) and default <c>vela apply --webresources</c> target.</param>
/// <param name="Json">Whether to emit machine-readable JSON output instead of the human-readable summary.</param>
internal sealed record ScaffoldOptions(
    string SolutionName,
    string Prefix,
    string OutputDirectory,
    bool IncludeConsole,
    bool IncludeIntegrations,
    bool IncludeTests,
    string CoreVersion,
    bool Force,
    bool Interactive,
    bool ManagedIdentity = false,
    bool IncludeWebResources = false,
    string WebResourceNamespace = "",
    string WebResourcesFolder = "dist",
    bool Json = false)
{
    // Solution name doubles as a folder + file name, so keep it to filename-safe characters. The first character
    // must be alphanumeric so the name can never be a path-traversal segment (".", "..", ".hidden"): the solution
    // name is combined into the output path and a --force regeneration deletes managed sub-folders under it, so a
    // traversal name could delete directories outside the target tree.
    private static readonly Regex SolutionNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    // Prefix becomes C# namespaces and assembly names: a dotted identifier (e.g. Zava or Zava.CRM).
    private static readonly Regex PrefixPattern = new("^[A-Za-z][A-Za-z0-9]*(\\.[A-Za-z][A-Za-z0-9]*)*$", RegexOptions.Compiled);

    // The JS global name is a dotted identifier (esbuild globalName), e.g. `adc` or `adc.sales`.
    private static readonly Regex GlobalNamePattern = new("^[A-Za-z_$][A-Za-z0-9_$]*(\\.[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.Compiled);

    // --core-version is interpolated verbatim into the generated .csproj PackageReference, so it must be a real
    // NuGet version (major.minor[.patch[.revision]] with optional -prerelease / +metadata). This both blocks XML
    // injection (a value with a quote) and catches typos that would silently fail `dotnet restore`.
    private static readonly Regex CoreVersionPattern = new("^[0-9]+\\.[0-9]+(\\.[0-9]+){0,2}(-[0-9A-Za-z][0-9A-Za-z.-]*)?(\\+[0-9A-Za-z][0-9A-Za-z.-]*)?$", RegexOptions.Compiled);

    // A web-resources output folder is a simple relative directory name (no separators or traversal). The first
    // character must be alphanumeric so the segment can never be "." or ".." (traversal).
    private static readonly Regex FolderPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>True when the value is a valid JS global name (dotted identifier).</summary>
    public static bool IsValidGlobalName(string value) =>
        !string.IsNullOrWhiteSpace(value) && GlobalNamePattern.IsMatch(value);

    /// <summary>The default JS global name suggested at init: the prefix with dots removed, lower-cased.</summary>
    public static string SuggestGlobalName(string prefix) => ScaffoldTemplates.ApiPrefix(prefix);

    // Offline fallback only. `vela init` normally resolves the latest published Vela.Plugins version from
    // nuget.org (see ResolveLatestFrameworkVersion); this is used when that lookup fails or `--core-version`
    // is not passed and the machine is offline. Keep it a real, published version - and at least the version
    // whose API the scaffolded code needs (the typed message contexts landed in 0.3.0; the typed-requests
    // surface - Vela.Messages.TypedRequest - landed in 0.8.0; the async-only Custom API step surface the
    // generated On<Message>() extensions can emit - OnAsyncOnly / IAsyncOnlyModeSelector - landed in 0.9.0).
    public const string DefaultCoreVersion = "0.18.0";

    // Windows reserved device names: unusable as a folder / file name on Windows even with a valid character set.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when the value is a valid solution name (folder / .sln-file safe).</summary>
    public static bool IsValidSolutionName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && SolutionNamePattern.IsMatch(value)
        && !ReservedDeviceNames.Contains(value)
        // A Windows device name followed by an extension is also reserved (e.g. CON.txt).
        && !ReservedDeviceNames.Contains(value.Split('.')[0]);

    /// <summary>True when the value is a valid namespace / assembly-name prefix (dotted identifier).</summary>
    public static bool IsValidPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value) && PrefixPattern.IsMatch(value);

    public static bool TryParse(string[] args, out ScaffoldOptions options, out int exitCode)
    {
        options = default!;
        exitCode = ExitSuccess;

        // Fail fast on a misspelled/unknown option (e.g. --prefx) before prompting or scaffolding anything.
        if (!CommandLineOptions.TryTokenize(args, ValueOptions, FlagOptions, out args, out exitCode))
        {
            return false;
        }

        bool interactive = args.Any(x =>
            string.Equals(x, "--interactive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x, "-i", StringComparison.OrdinalIgnoreCase));

        // Resolve the name from --name, else the first positional token. FindPositional skips only the
        // values of value-taking options, so boolean flags (e.g. `init --force MySolution`) do not
        // swallow the name - unlike the shared TryReadFirstPositional heuristic.
        string? name = TryReadOption(args, "--name") ?? FindPositional(args);

        if (string.IsNullOrWhiteSpace(name) && interactive)
        {
            name = Prompt("Solution name (example: ZavaSales):");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            exitCode = Fail("Missing solution name. Use --name <SolutionName> or --interactive.", ExitInvalidArguments);
            return false;
        }

        if (!SolutionNamePattern.IsMatch(name))
        {
            exitCode = Fail($"Invalid solution name '{name}'. Use letters, digits, '.', '-' or '_'.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        string? prefix = TryReadOption(args, "--prefix");
        if (string.IsNullOrWhiteSpace(prefix) && interactive)
        {
            prefix = Prompt($"Project namespace prefix (default: {SuggestPrefix(name)}):", allowEmpty: true);
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = SuggestPrefix(name);
        }

        if (!PrefixPattern.IsMatch(prefix))
        {
            exitCode = Fail($"Invalid prefix '{prefix}'. Use a dotted identifier such as 'Zava' or 'Zava.CRM'.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        // The generated projects are named {prefix}.Plugins / .Shared / ... and reference the framework packages
        // Vela.Plugins / Vela.Testing / Vela.Messages. NuGet package ids are case-insensitive, so a prefix of
        // "Vela" makes the generated "Vela.Plugins" project share its id with the Vela.Plugins package it
        // references - a self-referential cycle NuGet rejects with "NU1108: Cycle detected", so the solution
        // never restores. Reject it up front with an actionable message.
        if (string.Equals(prefix, "Vela", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = Fail(
                "Prefix 'Vela' collides with the Vela framework packages: the generated 'Vela.Plugins' project would have " +
                "the same (case-insensitive) id as the 'Vela.Plugins' package it references, which NuGet rejects (NU1108: " +
                "Cycle detected). Choose a different prefix, such as your Dataverse publisher prefix.",
                ExitInvalidArguments, includeUsage: false);
            return false;
        }

        string? output = TryReadOption(args, "--output") ?? TryReadOption(args, "-o");
        if (string.IsNullOrWhiteSpace(output) && interactive)
        {
            output = Prompt("Output directory (default: current directory):", allowEmpty: true);
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            output = ".";
        }

        bool includeConsole = !HasFlag(args, "--no-console");
        bool includeIntegrations = !HasFlag(args, "--no-integrations");
        bool includeTests = !HasFlag(args, "--no-tests");

        if (interactive)
        {
            if (!HasFlag(args, "--no-integrations"))
            {
                includeIntegrations = PromptYesNo("Generate an Integrations project? [Y/n]:", defaultYes: true);
            }

            if (!HasFlag(args, "--no-tests"))
            {
                includeTests = PromptYesNo("Generate a unit-test project (Vela.Testing)? [Y/n]:", defaultYes: true);
            }

            if (!HasFlag(args, "--no-console"))
            {
                includeConsole = PromptYesNo("Generate a ConsoleApp project? [Y/n]:", defaultYes: true);
            }
        }

        string coreVersion = TryReadOption(args, "--core-version") ?? DefaultCoreVersion;
        if (!CoreVersionPattern.IsMatch(coreVersion))
        {
            exitCode = Fail($"Invalid --core-version '{coreVersion}'. Use a NuGet version such as '0.8.0' or '1.2.3-preview'.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        bool force = HasFlag(args, "--force");

        // Managed-identity signing only applies to the Integrations project; if it is excluded, the flag is a no-op.
        bool managedIdentity = HasFlag(args, "--managed-identity") || HasFlag(args, "--mi");
        if (interactive && includeIntegrations && !managedIdentity)
        {
            managedIdentity = PromptYesNo("Enable managed identity for the Integrations project (adds the required signing step)? [y/N]:", defaultYes: false);
        }

        // The web-resources (TypeScript) project is opt-in: off unless --web-resources or the interactive prompt.
        bool includeWebResources = HasFlag(args, "--web-resources") || HasFlag(args, "--webresources");
        if (interactive && !includeWebResources)
        {
            includeWebResources = PromptYesNo("Scaffold a web resources (TypeScript) project? [y/N]:", defaultYes: false);
        }

        string webResourceNamespace = TryReadOption(args, "--web-resources-namespace") ?? string.Empty;
        string webResourcesFolder = TryReadOption(args, "--web-resources-folder") ?? "dist";
        if (includeWebResources)
        {
            if (interactive && string.IsNullOrWhiteSpace(webResourceNamespace))
            {
                webResourceNamespace = Prompt($"Web resources namespace (global name, default: {SuggestGlobalName(prefix)}):", allowEmpty: true) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(webResourceNamespace))
            {
                webResourceNamespace = SuggestGlobalName(prefix);
            }

            if (!GlobalNamePattern.IsMatch(webResourceNamespace))
            {
                exitCode = Fail($"Invalid web resources namespace '{webResourceNamespace}'. Use a dotted identifier such as 'adc' or 'adc.sales'.", ExitInvalidArguments, includeUsage: false);
                return false;
            }

            if (interactive && string.Equals(webResourcesFolder, "dist", StringComparison.Ordinal) && !HasFlag(args, "--web-resources-folder"))
            {
                string? answer = Prompt("Web resources output folder (default: dist):", allowEmpty: true);
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    webResourcesFolder = answer.Trim();
                }
            }

            if (!FolderPattern.IsMatch(webResourcesFolder))
            {
                exitCode = Fail($"Invalid web resources folder '{webResourcesFolder}'. Use a simple directory name (letters, digits, '.', '-' or '_').", ExitInvalidArguments, includeUsage: false);
                return false;
            }
        }

        options = new ScaffoldOptions(
            SolutionName: name,
            Prefix: prefix,
            OutputDirectory: output,
            IncludeConsole: includeConsole,
            IncludeIntegrations: includeIntegrations,
            IncludeTests: includeTests,
            CoreVersion: coreVersion,
            Force: force,
            Interactive: interactive,
            ManagedIdentity: managedIdentity,
            IncludeWebResources: includeWebResources,
            WebResourceNamespace: webResourceNamespace,
            WebResourcesFolder: webResourcesFolder,
            Json: HasFlag(args, "--json"));
        return true;
    }

    // Derives a valid dotted-identifier prefix from a free-form solution name by dropping invalid
    // characters, collapsing runs of dots, and ensuring every dot-separated segment starts with a
    // letter. The output always satisfies PrefixPattern so it is a safe default.
    public static string SuggestPrefix(string solutionName)
    {
        StringBuilder builder = new();
        bool lastWasDot = false;
        foreach (char c in solutionName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastWasDot = false;
            }
            else if (c == '.' && !lastWasDot)
            {
                // Collapse consecutive dots so no empty namespace segment is produced.
                builder.Append('.');
                lastWasDot = true;
            }
        }

        string cleaned = builder.ToString().Trim('.');
        if (cleaned.Length == 0)
        {
            return "App";
        }

        // PrefixPattern requires each dotted segment to start with a letter, so a segment beginning
        // with a digit (e.g. from "Sales.2024") is prefixed with "App" rather than only fixing the
        // first character of the whole string.
        string[] segments = cleaned.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            if (!char.IsLetter(segments[i][0]))
            {
                segments[i] = "App" + segments[i];
            }
        }

        return string.Join(".", segments);
    }

    // Options that consume the following token as their value; everything else is treated as boolean.
    // Value options (consume the next token) and boolean flags for `vela init`. Used both to skip value-option
    // values when reading the positional name and as the unknown-option allowlist. Keep in sync with TryParse.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
        { "--name", "--prefix", "--output", "-o", "--core-version", "--web-resources-namespace", "--web-resources-folder" };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--force", "--json", "--managed-identity", "--mi", "--no-console", "--no-integrations", "--no-tests",
        "--web-resources", "--webresources", "--interactive", "-i", "--help", "-h",
    };

    // Returns the first positional argument (a token not starting with '-' and not the value of a
    // value-taking option), or null if there is none.
    private static string? FindPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            if (current.StartsWith('-'))
            {
                if (i + 1 < args.Length && ValueOptions.Contains(current))
                {
                    i++;
                }

                continue;
            }

            return current;
        }

        return null;
    }

    private static bool PromptYesNo(string label, bool defaultYes)
    {
        string? answer = Prompt(label, allowEmpty: true);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return defaultYes;
        }

        return answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    // Resolve the latest published stable Vela.Plugins version from nuget.org, so `vela init` scaffolds against
    // the current framework without the CLI needing a new release per framework version. Returns null on any
    // failure (offline, timeout, parse), so the caller falls back to DefaultCoreVersion. Not called during
    // arg parsing (kept out of the unit-tested path); the init command calls it before generating.
    internal static string? ResolveLatestFrameworkVersion()
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
            string json = http.GetStringAsync("https://api.nuget.org/v3-flatcontainer/vela.plugins/index.json")
                .GetAwaiter().GetResult();
            using JsonDocument doc = JsonDocument.Parse(json);
            Version? best = null;
            string? bestStr = null;
            foreach (JsonElement element in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                string? candidate = element.GetString();
                if (string.IsNullOrEmpty(candidate) || candidate.Contains('-'))
                {
                    continue; // skip prerelease versions
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
