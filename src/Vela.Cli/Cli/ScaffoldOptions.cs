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
/// does not touch Dataverse, so it has its own parser rather than reusing <see cref="CommandLineOptions.TryParseSharedOptions(string[], out SharedOptions, out int)"/>.
/// </summary>
/// <param name="SolutionName">Name of the generated solution (also the root folder and .sln file name).</param>
/// <param name="Prefix">Namespace / assembly-name prefix for the generated projects.</param>
/// <param name="OutputDirectory">Directory the solution folder is created under.</param>
/// <param name="IncludeConsole">When true, generate the ConsoleApp project.</param>
/// <param name="IncludeIntegrations">When true, generate the Integrations project: with its demo code under
/// <c>--samples</c>, empty otherwise (<c>--integrations</c> or <c>--managed-identity</c>).</param>
/// <param name="IncludeTests">When true, generate the Plugins.Tests project (Vela.Testing).</param>
/// <param name="CoreVersion">Version of the <c>Vela.Plugins</c> package the projects reference.</param>
/// <param name="Force">When true, overwrite an existing target directory.</param>
/// <param name="Interactive">When true, missing values are prompted for.</param>
/// <param name="ManagedIdentity">When true, mark the Integrations package as managed-identity-enabled and add the build-time package-signing step it requires.</param>
/// <param name="IncludeWebResources">When true, scaffold the opt-in <c>{prefix}.WebResources</c> TypeScript project.</param>
/// <param name="WebResourceNamespace">The dotted JS global name (esbuild <c>globalName</c>) the bundle exposes; the consumer's publisher prefix, never "Vela".</param>
/// <param name="WebResourcesFolder">The build output folder (tsup <c>outDir</c>) and default <c>vela apply --webresources</c> target.</param>
/// <param name="Json">Whether to emit machine-readable JSON output instead of the human-readable summary.</param>
/// <param name="IncludeSamples">When true (<c>--samples</c>), emit the demo models, services, plugins, Custom APIs and the
/// demo-only ConsoleApp project, plus the Integrations project with its demos. Off by default: <c>vela init</c> scaffolds a minimal solution.</param>
/// <param name="PublisherPrefix">The Dataverse publisher customization prefix passed with <c>--publisher-prefix</c>, or
/// null to derive it from <paramref name="Prefix"/>. When set it is written to the generated projects as
/// <c>VelaPublisherPrefix</c>, which <c>vela add customapi</c> reads.</param>
/// <param name="CoreVersionPinned">True when <c>--core-version</c> was passed (in either form), so the caller does not
/// replace it with the latest version on nuget.org.</param>
/// <param name="Verbose">When true (<c>--verbose</c>), a failure prints the full exception.</param>
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
    bool Json = false,
    bool IncludeSamples = false,
    string? PublisherPrefix = null,
    bool CoreVersionPinned = false,
    bool Verbose = false)
{
    /// <summary>
    /// The publisher prefix the generated Custom API unique names use: <see cref="PublisherPrefix"/> when given,
    /// else the one derived from <see cref="Prefix"/>.
    /// </summary>
    public string EffectivePublisherPrefix => PublisherPrefix ?? ScaffoldTemplates.ApiPrefix(Prefix);

    // A Dataverse publisher customization prefix: 2-8 characters, starts with a letter, alphanumeric, and not the
    // reserved "mscrm" (Dataverse rejects it).
    private static readonly Regex PublisherPrefixPattern = new("^[a-z][a-z0-9]{1,7}$", RegexOptions.Compiled);

    /// <summary>True when the value is a valid Dataverse publisher customization prefix (already lower-cased).</summary>
    public static bool IsValidPublisherPrefix(string value) =>
        !string.IsNullOrEmpty(value)
        && PublisherPrefixPattern.IsMatch(value)
        && !value.StartsWith("mscrm", StringComparison.Ordinal);

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
    public const string DefaultCoreVersion = "0.36.0";

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
        string? name = TryReadOption(args, "--name") ?? FindPositional(args, ValueOptions);

        if (string.IsNullOrWhiteSpace(name) && interactive)
        {
            name = Prompt("Solution name (example: ZavaSales):");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            exitCode = Fail("Missing solution name. Use --name <SolutionName> or --interactive.", ExitInvalidArguments);
            return false;
        }

        if (!IsValidSolutionName(name))
        {
            exitCode = Fail($"Invalid solution name '{name}'. Use letters, digits, '.', '-' or '_', and avoid Windows reserved names (CON, PRN, AUX, NUL, COM1-9, LPT1-9).", ExitInvalidArguments, includeUsage: false);
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
            output = Prompt("Parent directory, the solution folder is created inside it (default: current directory):", allowEmpty: true);
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            output = ".";
        }

        // `vela init` is minimal by default: the Shared + Plugins projects (and an empty Plugins.Tests project unless
        // --no-tests) with no sample code, so nothing is deployed until you add your own plugin. The structural files
        // (.sln, csproj, Directory.*.props, README, .gitignore) are written, so the solution builds, tests and plans
        // unedited. --samples opts in to the demo models/services/plugins/Custom APIs and the demo-only Integrations
        // and ConsoleApp projects. --minimal / --no-demos are kept as accepted aliases of the default.
        bool samplesFlag = HasFlag(args, "--samples");
        bool minimalFlag = HasFlag(args, "--minimal") || HasFlag(args, "--no-demos");
        if (samplesFlag && minimalFlag)
        {
            exitCode = Fail("Use either --samples or --minimal, not both (minimal is the default).", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        bool includeSamples = samplesFlag;
        if (interactive && !samplesFlag && !minimalFlag)
        {
            includeSamples = PromptYesNo("Include the sample code (demo models, services, plugins, Custom APIs, Integrations and ConsoleApp projects)? [y/N]:", defaultYes: false);
        }

        // The Integrations project comes with --samples (with its demo plugins and Custom APIs), or empty with
        // --integrations. --managed-identity only shapes that project, so it asks for it too. Asking for the project
        // and excluding it at once is refused rather than resolved silently either way.
        bool integrationsFlag = HasFlag(args, "--integrations");
        bool managedIdentityFlag = HasFlag(args, "--managed-identity") || HasFlag(args, "--mi");
        bool noIntegrations = HasFlag(args, "--no-integrations");
        if (noIntegrations && (integrationsFlag || managedIdentityFlag))
        {
            exitCode = Fail(
                integrationsFlag
                    ? "Use either --integrations or --no-integrations, not both."
                    : "--managed-identity applies to the Integrations project, which --no-integrations excludes; drop one of them.",
                ExitInvalidArguments,
                includeUsage: false);
            return false;
        }

        // An explicit --no-console/--no-integrations/--no-tests always wins; the ConsoleApp project needs --samples.
        bool includeConsole = includeSamples && !HasFlag(args, "--no-console");
        bool includeIntegrations = (includeSamples || integrationsFlag || managedIdentityFlag) && !noIntegrations;
        bool includeTests = !HasFlag(args, "--no-tests");

        if (interactive)
        {
            if (includeSamples && !noIntegrations && !integrationsFlag && !managedIdentityFlag)
            {
                includeIntegrations = PromptYesNo("Generate an Integrations project? [Y/n]:", defaultYes: true);
            }

            if (!HasFlag(args, "--no-tests"))
            {
                includeTests = PromptYesNo("Generate a unit-test project (Vela.Testing)? [Y/n]:", defaultYes: true);
            }

            if (includeSamples && !HasFlag(args, "--no-console"))
            {
                includeConsole = PromptYesNo("Generate a ConsoleApp project? [Y/n]:", defaultYes: true);
            }
        }

        // The tokenizer has already expanded `--core-version=X` to `--core-version X`, so this sees both forms.
        string? pinnedCoreVersion = TryReadOption(args, "--core-version");
        string coreVersion = pinnedCoreVersion ?? DefaultCoreVersion;
        if (!CoreVersionPattern.IsMatch(coreVersion))
        {
            exitCode = Fail($"Invalid --core-version '{coreVersion}'. Use a NuGet version such as '0.8.0' or '1.2.3-preview'.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        // The publisher customization prefix of the target solution is distinct from the .NET namespace prefix. It
        // names the Custom APIs, so an explicit value is validated (2-8 chars, letter first, alphanumeric) and recorded
        // in the generated projects for `vela add customapi`. Without it the prefix is derived from --prefix; that
        // guess is only needed for the sample Custom APIs, and is refused when it is not a valid publisher prefix.
        string? publisherPrefix = TryReadOption(args, "--publisher-prefix")?.Trim().ToLowerInvariant();
        if (publisherPrefix is not null && !IsValidPublisherPrefix(publisherPrefix))
        {
            exitCode = Fail($"Invalid --publisher-prefix '{publisherPrefix}'. A Dataverse publisher prefix is 2-8 characters, starts with a letter, is alphanumeric and does not start with 'mscrm' (e.g. 'zava').", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        if (publisherPrefix is null && includeSamples && interactive && !IsValidPublisherPrefix(ScaffoldTemplates.ApiPrefix(prefix)))
        {
            publisherPrefix = Prompt($"Publisher prefix of your solution ('{ScaffoldTemplates.ApiPrefix(prefix)}' is not valid; 2-8 characters, letter first):", allowEmpty: true)?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(publisherPrefix))
            {
                publisherPrefix = null;
            }
            else if (!IsValidPublisherPrefix(publisherPrefix))
            {
                exitCode = Fail($"Invalid publisher prefix '{publisherPrefix}'. Use 2-8 characters: a letter, then letters or digits (e.g. 'zava').", ExitInvalidArguments, includeUsage: false);
                return false;
            }
        }

        if (publisherPrefix is null && includeSamples && !IsValidPublisherPrefix(ScaffoldTemplates.ApiPrefix(prefix)))
        {
            exitCode = Fail($"The sample Custom APIs need a publisher prefix, and '{ScaffoldTemplates.ApiPrefix(prefix)}' (derived from --prefix '{prefix}') is not a valid one (2-8 characters, letter first, alphanumeric). Pass --publisher-prefix <prefix> with your solution publisher's prefix.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        bool force = HasFlag(args, "--force");

        // Managed-identity signing only applies to the Integrations project, which the flag includes (see above).
        bool managedIdentity = managedIdentityFlag;
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
        else
        {
            // Say so rather than silently ignore an option that only shapes the (excluded) web-resources project.
            // stderr, so it never pollutes the --json report on stdout.
            foreach (string ignored in new[] { "--web-resources-namespace", "--web-resources-folder" }.Where(o => HasFlag(args, o)))
            {
                Console.Error.WriteLine($"Warning: {ignored} applies to the web resources project, which is not scaffolded (pass --web-resources); ignoring it.");
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
            Json: HasFlag(args, "--json"),
            IncludeSamples: includeSamples,
            PublisherPrefix: publisherPrefix,
            CoreVersionPinned: pinnedCoreVersion is not null,
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"));
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
        { "--name", "--prefix", "--output", "-o", "--core-version", "--publisher-prefix", "--web-resources-namespace", "--web-resources-folder" };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--force", "--json", "--managed-identity", "--mi", "--integrations", "--no-console", "--no-integrations", "--no-tests",
        "--web-resources", "--webresources", "--interactive", "-i", "--help", "-h", "--minimal", "--no-demos", "--samples", "--verbose", "-v",
    };

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
