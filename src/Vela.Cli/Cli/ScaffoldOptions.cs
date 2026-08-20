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
    bool ManagedIdentity = false)
{
    // Solution name doubles as a folder + file name, so keep it to filename-safe characters.
    private static readonly Regex SolutionNamePattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    // Prefix becomes C# namespaces and assembly names: a dotted identifier (e.g. Zava or Zava.CRM).
    private static readonly Regex PrefixPattern = new("^[A-Za-z][A-Za-z0-9]*(\\.[A-Za-z][A-Za-z0-9]*)*$", RegexOptions.Compiled);

    // Offline fallback only. `vela init` normally resolves the latest published Vela.Plugins version from
    // nuget.org (see ResolveLatestFrameworkVersion); this is used when that lookup fails or `--core-version`
    // is not passed and the machine is offline. Keep it a real, published version.
    public const string DefaultCoreVersion = "0.1.1";

    /// <summary>True when the value is a valid solution name (folder / .sln-file safe).</summary>
    public static bool IsValidSolutionName(string value) =>
        !string.IsNullOrWhiteSpace(value) && SolutionNamePattern.IsMatch(value);

    /// <summary>True when the value is a valid namespace / assembly-name prefix (dotted identifier).</summary>
    public static bool IsValidPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value) && PrefixPattern.IsMatch(value);

    public static bool TryParse(string[] args, out ScaffoldOptions options, out int exitCode)
    {
        options = default!;
        exitCode = ExitSuccess;

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
        bool force = HasFlag(args, "--force");

        // Managed-identity signing only applies to the Integrations project; if it is excluded, the flag is a no-op.
        bool managedIdentity = HasFlag(args, "--managed-identity") || HasFlag(args, "--mi");
        if (interactive && includeIntegrations && !managedIdentity)
        {
            managedIdentity = PromptYesNo("Enable managed identity for the Integrations project (adds the required signing step)? [y/N]:", defaultYes: false);
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
            ManagedIdentity: managedIdentity);
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
    private static readonly string[] ValueOptions =
        ["--name", "--prefix", "--output", "-o", "--core-version"];

    // Returns the first positional argument (a token not starting with '-' and not the value of a
    // value-taking option), or null if there is none.
    private static string? FindPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            if (current.StartsWith('-'))
            {
                if (i + 1 < args.Length && ValueOptions.Contains(current, StringComparer.OrdinalIgnoreCase))
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
