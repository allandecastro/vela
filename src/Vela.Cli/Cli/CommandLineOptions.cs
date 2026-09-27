using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Command-line argument parsing and authentication option resolution for all CLI commands.
/// </summary>
internal static class CommandLineOptions
{
    // Connection options every Dataverse-authenticating command reads (the connected commands via
    // TryResolveConnection/ResolveClientSecret, and `vela models`). Kept separate so each command's allowlist
    // composes them without duplicating the list.
    internal static readonly HashSet<string> ConnectionValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--connection-string", "--url", "--tenant-id", "--client-id", "--client-secret",
        "--auth-client-id", "--authority", "--timeout",
    };

    internal static readonly HashSet<string> ConnectionFlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--client-secret-stdin", "--fresh-login",
    };

    // Every option the shared parser (TryParseSharedOptions) reads: the package/scope options plus the shared
    // connection options. Kept as an allowlist so an unknown/misspelled option (e.g. --packages-dur) fails fast
    // with a clear message instead of being silently ignored - a dropped --packages-dir would otherwise leave the
    // run with no package input and only surface as a confusing "Missing package input" much later. Value options
    // consume the next token; flags stand alone. Keep these in sync when adding a shared option.
    private static readonly HashSet<string> SharedValueOptions = new(ConnectionValueOptions, StringComparer.OrdinalIgnoreCase)
    {
        "--webresources", "--package", "-p", "--packages-dir", "--solution", "--solution-unique-name",
        "--package-assembly", "--environment", "--out", "--batch-size",
    };

    private static readonly HashSet<string> SharedFlagOptions = new(ConnectionFlagOptions, StringComparer.OrdinalIgnoreCase)
    {
        "--interactive", "-i", "--auto-approve", "--yes", "--strict", "--verbose", "-v", "--json", "--help", "-h",
    };

    // The tokens that request usage on any command; handled (print usage, exit success) before the option guard.
    private static readonly HashSet<string> HelpTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "-h", "-?", "/?",
    };

    /// <summary>True when any argument requests help (<c>--help</c>/<c>-h</c>/<c>-?</c>/<c>/?</c>).</summary>
    public static bool IsHelpRequested(string[] args) => args.Any(HelpTokens.Contains);

    /// <summary>Validates the shared (plan/apply/inspect) options against the shared allowlist.</summary>
    public static bool TryValidateKnownOptions(string[] args, out int exitCode) =>
        TryValidateKnownOptions(args, SharedValueOptions, SharedFlagOptions, out exitCode);

    /// <summary>
    /// Rejects any dash-prefixed token that is not a known option (a value option or a flag), so a typo is
    /// reported up front rather than silently ignored. The value of a value option (the following token) is
    /// skipped; non-dash tokens (positionals), a lone <c>-</c>, and a bare <c>--</c> separator are left alone.
    /// </summary>
    public static bool TryValidateKnownOptions(string[] args, HashSet<string> valueOptions, HashSet<string> flagOptions, out int exitCode)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token.Length < 2 || token[0] != '-' || token == "--")
            {
                continue; // positional, lone '-', or the bare '--' separator
            }

            if (valueOptions.Contains(token))
            {
                // Skip the following token as this option's value, UNLESS it is itself a known option: then this
                // value option is missing its value and the next known option must be validated on its own rather
                // than silently swallowed (A10). An unknown dash-looking token stays allowed as a value (e.g. a name
                // or a connection string that starts with '-').
                if (i + 1 < args.Length && !IsKnownOption(args[i + 1], valueOptions, flagOptions))
                {
                    i++;
                }

                continue;
            }

            if (flagOptions.Contains(token))
            {
                continue;
            }

            // Don't dump the generic top-level usage here (it is not this command's usage and only adds noise); the
            // message already points at the right per-command help (B5).
            exitCode = Fail($"Unknown option '{token}'. Run 'vela <command> --help' to see the available options.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        exitCode = ExitSuccess;
        return true;
    }

    /// <summary>
    /// The shared front-door tokenizer for a command's arguments. It (1) expands the <c>--opt=value</c> inline form
    /// of a known value option into two tokens (<c>--opt</c> <c>value</c>) so every reader sees the same shape, and
    /// (2) validates: an unknown dash option fails, and a required value option missing its value (last token, or
    /// followed by another known option) fails with "requires a value". Value options listed in
    /// <paramref name="optionalValueOptions"/> may appear bare (e.g. <c>--messages</c> = all). An unknown
    /// dash-looking token is still accepted as a value (a name / connection string may start with <c>-</c>).
    /// The <paramref name="normalized"/> array (with <c>=</c> expanded) is what the caller should read from.
    /// </summary>
    public static bool TryTokenize(
        string[] args,
        HashSet<string> valueOptions,
        HashSet<string> flagOptions,
        HashSet<string> optionalValueOptions,
        out string[] normalized,
        out int exitCode)
    {
        normalized = ExpandInlineValues(args, valueOptions, flagOptions);
        exitCode = ExitSuccess;

        for (int i = 0; i < normalized.Length; i++)
        {
            string token = normalized[i];
            if (token.Length < 2 || token[0] != '-' || token == "--")
            {
                continue; // positional, lone '-', or the bare '--' separator
            }

            if (valueOptions.Contains(token))
            {
                bool hasValue = i + 1 < normalized.Length && !IsKnownOption(normalized[i + 1], valueOptions, flagOptions);
                if (hasValue)
                {
                    i++; // consume the value
                    continue;
                }

                // No value present. That is fine for an optional-value option (bare = a mode, e.g. --messages);
                // otherwise it is an error rather than a silent mis-bind to the following option (B1 / A9).
                if (optionalValueOptions.Contains(token))
                {
                    continue;
                }

                exitCode = Fail($"Option '{token}' requires a value.", ExitInvalidArguments, includeUsage: false);
                return false;
            }

            if (flagOptions.Contains(token))
            {
                continue;
            }

            exitCode = Fail($"Unknown option '{token}'. Run 'vela <command> --help' to see the available options.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        return true;
    }

    /// <summary>Overload with no optional-value options (every value option requires a value).</summary>
    public static bool TryTokenize(string[] args, HashSet<string> valueOptions, HashSet<string> flagOptions, out string[] normalized, out int exitCode) =>
        TryTokenize(args, valueOptions, flagOptions, EmptyOptionSet, out normalized, out exitCode);

    private static readonly HashSet<string> EmptyOptionSet = new(StringComparer.OrdinalIgnoreCase);

    // Expands a "--opt=value" token into "--opt", "value" when --opt is a known value option, so all the readers
    // (which match "--opt" then take the next token) transparently support the inline form (B2). A flag written as
    // "--flag=..." is left intact (it will be reported as an unknown option, since a flag takes no value). Tokens
    // after a bare "--" are passed through unchanged.
    private static string[] ExpandInlineValues(string[] args, HashSet<string> valueOptions, HashSet<string> flagOptions)
    {
        List<string> result = new List<string>(args.Length + 4);
        bool afterSeparator = false;
        foreach (string arg in args)
        {
            if (afterSeparator)
            {
                result.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                afterSeparator = true;
                result.Add(arg);
                continue;
            }

            int eq = arg.IndexOf('=');
            if (eq > 0 && arg[0] == '-')
            {
                string name = arg.Substring(0, eq);
                if (valueOptions.Contains(name))
                {
                    result.Add(name);
                    result.Add(arg.Substring(eq + 1));
                    continue;
                }
            }

            result.Add(arg);
        }

        return result.ToArray();
    }

    public static bool TryParseSharedOptions(string[] args, out SharedOptions options, out int exitCode)
    {
        options = default!;
        exitCode = ExitSuccess;

        // Fail fast on a misspelled/unknown option before prompting or reading anything else.
        if (!TryTokenize(args, SharedValueOptions, SharedFlagOptions, out args, out exitCode))
        {
            return false;
        }

        bool interactive = args.Any(x => string.Equals(x, "--interactive", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "-i", StringComparison.OrdinalIgnoreCase));

        // A web resources root makes plugin packages optional: you can deploy web resources on their own.
        string? webResourcesRoot = TryReadOption(args, "--webresources");
        if (!string.IsNullOrWhiteSpace(webResourcesRoot))
        {
            webResourcesRoot = Path.GetFullPath(webResourcesRoot);
            if (!Directory.Exists(webResourcesRoot))
            {
                exitCode = Fail($"Web resources folder not found: {webResourcesRoot}", ExitFileNotFound, includeUsage: false);
                return false;
            }

            // Web resources reconcile against a solution (component type 61): without one the current state can't be
            // read, so the plan would show every file as added every run and could never reconcile deletions or
            // validate the publisher prefix. Require the scope explicitly.
            if (string.IsNullOrWhiteSpace(TryReadOption(args, "--solution") ?? TryReadOption(args, "--solution-unique-name")))
            {
                exitCode = Fail("--webresources requires --solution <uniquename> (web resources are reconciled within a solution).", ExitInvalidArguments, includeUsage: false);
                return false;
            }
        }

        // Resolve --package/-p/--packages-dir to validated, de-duplicated full paths (shared with `vela models`).
        if (!TryResolvePackagePaths(args, out List<string> packagePaths, out exitCode))
        {
            return false;
        }

        // With no package option, fall back to a positional package path, then interactive prompts. A web resources
        // root makes this optional (a web-resource-only deploy is valid), so only that path may finish empty.
        if (packagePaths.Count == 0)
        {
            List<string> fallback = [];
            string? positional = TryReadFirstPositional(args, SharedValueOptions);
            if (!string.IsNullOrWhiteSpace(positional))
            {
                fallback.Add(positional);
            }

            if (fallback.Count == 0 && string.IsNullOrWhiteSpace(webResourcesRoot) && interactive)
            {
                string? packagePrompt = Prompt("Package .nupkg path (or leave empty to use a folder):");
                if (!string.IsNullOrWhiteSpace(packagePrompt))
                {
                    fallback.Add(packagePrompt);
                }
                else
                {
                    string? folderPrompt = Prompt("Packages folder path:");
                    if (!string.IsNullOrWhiteSpace(folderPrompt))
                    {
                        string fullDir = Path.GetFullPath(folderPrompt);
                        if (!Directory.Exists(fullDir))
                        {
                            exitCode = Fail($"Packages directory not found: {fullDir}", ExitFileNotFound, includeUsage: false);
                            return false;
                        }

                        fallback.AddRange(Directory.GetFiles(fullDir, "*.nupkg", SearchOption.TopDirectoryOnly));
                    }
                }
            }

            if (fallback.Count > 0 && !TryResolvePackagePaths(args, fallback, out packagePaths, out exitCode))
            {
                return false;
            }

            if (packagePaths.Count == 0 && string.IsNullOrWhiteSpace(webResourcesRoot))
            {
                exitCode = Fail("Missing package input. Use --package <file.nupkg> (repeatable) or --packages-dir <folder>.", ExitInvalidArguments);
                return false;
            }
        }

        string[] resolvedPackages = packagePaths.ToArray();

        if (!TryReadTimeoutMinutes(args, out int? timeoutMinutes, out exitCode))
        {
            return false;
        }

        if (!TryReadBatchChunkSize(args, out int? batchChunkSize, out exitCode))
        {
            return false;
        }

        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out exitCode))
        {
            return false;
        }

        options = new SharedOptions(
            PackagePaths: resolvedPackages,
            // Only prompt for the optional package assembly on a real terminal. Under redirected/piped stdin
            // (CI, agents, tests) a blocking Console.ReadLine() on an open-but-empty pipe hangs plan/apply/inspect
            // forever, so scripted usage skips the prompt and leaves this null (it is optional).
            PackageAssembly: TryReadOption(args, "--package-assembly") ?? (interactive && !Console.IsInputRedirected ? Prompt("Package assembly name (optional, example: Zava.Plugins.Package.dll):", allowEmpty: true) : null),
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: TryReadOption(args, "--solution") ?? TryReadOption(args, "--solution-unique-name"),
            Environment: TryReadOption(args, "--environment") ?? url,
            OutPath: TryReadOption(args, "--out"),
            AutoApprove: args.Any(x => string.Equals(x, "--auto-approve", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, "--yes", StringComparison.OrdinalIgnoreCase)),
            Interactive: interactive,
            Strict: args.Any(x => string.Equals(x, "--strict", StringComparison.OrdinalIgnoreCase)),
            Verbose: args.Any(x => string.Equals(x, "--verbose", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "-v", StringComparison.OrdinalIgnoreCase)),
            WebResourcesRoot: webResourcesRoot,
            Json: args.Any(x => string.Equals(x, "--json", StringComparison.OrdinalIgnoreCase)),
            TimeoutMinutes: timeoutMinutes,
            BatchChunkSize: batchChunkSize);
        return true;
    }

    /// <summary>
    /// Resolves the plugin-package inputs to full, de-duplicated, validated <c>.nupkg</c> paths: <c>--package</c>
    /// (repeatable), its <c>-p</c> short alias, and <c>--packages-dir</c> (a folder of <c>.nupkg</c>). Shared by the
    /// plan/apply/inspect parser and <c>vela models</c> so the package-path convention lives in one place. Returns
    /// true with a (possibly empty) list; false plus an exit code on a missing directory or package path.
    /// </summary>
    public static bool TryResolvePackagePaths(string[] args, out List<string> packagePaths, out int exitCode) =>
        TryResolvePackagePaths(args, null, out packagePaths, out exitCode);

    /// <summary>
    /// The <see cref="TryResolvePackagePaths(string[], out List{string}, out int)"/> overload that also folds in the
    /// caller-collected <paramref name="seed"/> paths (a positional token or an interactive prompt from the shared
    /// parser), validating them the same way as the option-supplied ones.
    /// </summary>
    public static bool TryResolvePackagePaths(string[] args, IEnumerable<string>? seed, out List<string> packagePaths, out int exitCode)
    {
        exitCode = ExitSuccess;
        List<string> paths = seed is null ? [] : new List<string>(seed);
        paths.AddRange(ReadMultiOption(args, "--package"));
        paths.AddRange(ReadMultiOption(args, "-p")); // -p is the shared short alias for --package (as in plan/apply)

        string? packagesDir = TryReadOption(args, "--packages-dir");
        if (!string.IsNullOrWhiteSpace(packagesDir))
        {
            string fullDir = Path.GetFullPath(packagesDir);
            if (!Directory.Exists(fullDir))
            {
                packagePaths = [];
                exitCode = Fail($"Packages directory not found: {fullDir}", ExitFileNotFound, includeUsage: false);
                return false;
            }

            paths.AddRange(Directory.GetFiles(fullDir, "*.nupkg", SearchOption.TopDirectoryOnly));
        }

        packagePaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string packagePath in packagePaths)
        {
            if (!File.Exists(packagePath))
            {
                exitCode = Fail($"Package not found: {packagePath}", ExitFileNotFound, includeUsage: false);
                return false;
            }

            if (!packagePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = Fail($"Package must be a .nupkg file: {packagePath}", ExitInvalidArguments, includeUsage: false);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads and validates the optional <c>--timeout</c> connection option (a whole number of minutes, 1..60).
    /// Shared by the plan/apply/inspect parser and by <c>vela models</c> so both honour the same connection
    /// timeout. Returns true with <paramref name="timeoutMinutes"/> null when <c>--timeout</c> is absent, and
    /// false with an <see cref="CliExitCodes.ExitInvalidArguments"/> code when it is present but not a whole
    /// number of minutes in range.
    /// </summary>
    public static bool TryReadTimeoutMinutes(string[] args, out int? timeoutMinutes, out int exitCode)
    {
        timeoutMinutes = null;
        exitCode = ExitSuccess;

        string? raw = TryReadOption(args, "--timeout");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes < 1 || minutes > 60)
        {
            exitCode = Fail("--timeout must be a whole number of minutes between 1 and 60.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        timeoutMinutes = minutes;
        return true;
    }

    /// <summary>
    /// Reads the optional <c>--batch-size</c> apply option: the ExecuteMultiple chunk size. Defaults to
    /// <see cref="DataverseRegistration.DefaultBatchChunkSize"/> when absent; must be 1..1000 (Dataverse caps
    /// ExecuteMultiple at 1000 requests per batch).
    /// </summary>
    public static bool TryReadBatchChunkSize(string[] args, out int? batchChunkSize, out int exitCode)
    {
        batchChunkSize = null;
        exitCode = ExitSuccess;

        string? raw = TryReadOption(args, "--batch-size");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 1 || size > 1000)
        {
            exitCode = Fail("--batch-size must be a whole number between 1 and 1000.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        batchChunkSize = size;
        return true;
    }

    /// <summary>
    /// Resolves Dataverse authentication from the shared connection flags (connection string, client-secret, or
    /// interactive browser OAuth). Shared by every connected command; carries no package/scope concerns.
    /// </summary>
    public static bool TryResolveConnection(string[] args, bool interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode)
    {
        exitCode = ExitSuccess;
        oauthUrl = null;
        oauthToken = null;

        connectionString = TryReadOption(args, "--connection-string")
            ?? Environment.GetEnvironmentVariable("DATAVERSE_CONNECTION_STRING");
        url = TryReadOption(args, "--url");
        string? tenantId = TryReadOption(args, "--tenant-id");
        string? clientId = TryReadOption(args, "--client-id");
        string? clientSecret = ResolveClientSecret(args);

        if (interactive && string.IsNullOrWhiteSpace(connectionString))
        {
            // Default the prompt to the last URL used interactively, so a repeat run just presses Enter.
            url = string.IsNullOrWhiteSpace(url)
                ? Prompt("Dataverse URL (https://org.crm.dynamics.com):", defaultValue: UserConfig.GetLastDataverseUrl())
                : url;

            // Remember it for next time (best-effort; the URL is not a secret).
            UserConfig.SetLastDataverseUrl(url);

            // If client-id / client-secret were not supplied, use browser-based OAuth
            bool useOAuth = string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientSecret);
            if (!useOAuth)
            {
                tenantId = string.IsNullOrWhiteSpace(tenantId) ? Prompt("Tenant ID:") : tenantId;
                clientId = string.IsNullOrWhiteSpace(clientId) ? Prompt("Client ID (App Registration):") : clientId;
                clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? Prompt("Client Secret:", secret: true) : clientSecret;
            }

            if (useOAuth)
            {
                // Leave connectionString null; store URL + token in SharedOptions for the token-callback constructor
            }
            else if (string.IsNullOrWhiteSpace(url))
            {
                exitCode = Fail("Dataverse URL is required for client-secret authentication.", ExitInvalidArguments);
                return false;
            }
            else
            {
                string? inputError = ValidateClientSecretConnectionInputs(url, tenantId, clientId, clientSecret);
                if (inputError != null)
                {
                    exitCode = Fail(inputError, ExitInvalidArguments);
                    return false;
                }

                connectionString = BuildClientSecretConnectionString(url, tenantId!, clientId!, clientSecret!);
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Check whether we're in an OAuth interactive path: no connection string and useOAuth was true
            bool oauthInteractive = interactive && string.IsNullOrWhiteSpace(url) == false
                && string.IsNullOrWhiteSpace(TryReadOption(args, "--client-id"))
                && string.IsNullOrWhiteSpace(TryReadOption(args, "--client-secret"))
                && string.IsNullOrWhiteSpace(TryReadOption(args, "--connection-string"))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATAVERSE_CONNECTION_STRING"));

            if (oauthInteractive)
            {
                // Run the browser login through our own MSAL public client and connect via the token callback.
                // The token is persisted by a cross-platform cache (see InteractiveTokenCache), so a repeat run is
                // silent on macOS/Linux too - not only on Windows. A custom --authority (sovereign cloud) flows
                // through the same path. --fresh-login forgets the cached account and forces a new prompt.
                oauthUrl = url!;
                oauthToken = AcquireTokenInteractive(
                    url!,
                    TryReadOption(args, "--auth-client-id"),
                    TryReadOption(args, "--authority"),
                    HasFlag(args, "--fresh-login"));
            }
            else if (!string.IsNullOrWhiteSpace(url)
                && string.IsNullOrWhiteSpace(tenantId)
                && string.IsNullOrWhiteSpace(clientId)
                && string.IsNullOrWhiteSpace(clientSecret))
            {
                // No explicit credentials and not --interactive: acquire a token transparently from the ambient
                // Azure credential (environment, managed identity / workload identity in CI, and 'az login'
                // locally) via DefaultAzureCredential. No browser and no flag - a bare `vela apply --url ...`
                // just works when the user is already `az login`-ed or running under a managed identity.
                string? token = TryAcquireAmbientToken(url, out string? ambientError);
                if (token == null)
                {
                    exitCode = Fail(ambientError!, ExitConnectionFailure);
                    return false;
                }

                oauthUrl = url;
                oauthToken = token;
            }
            else if (string.IsNullOrWhiteSpace(url) ||
                string.IsNullOrWhiteSpace(tenantId) ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret))
            {
                exitCode = Fail(
                    "Missing Dataverse auth. Provide --connection-string OR --url + --tenant-id + --client-id + --client-secret OR use --interactive for browser login. " +
                    "With only --url, Vela uses your ambient Azure credential (az login / managed identity).",
                    ExitInvalidArguments);
                return false;
            }
            else
            {
                string? inputError = ValidateClientSecretConnectionInputs(url, tenantId, clientId, clientSecret);
                if (inputError != null)
                {
                    exitCode = Fail(inputError, ExitInvalidArguments);
                    return false;
                }

                connectionString = BuildClientSecretConnectionString(url, tenantId, clientId, clientSecret);
            }
        }

        return true;
    }

    public static string BuildClientSecretConnectionString(string url, string tenantId, string clientId, string clientSecret)
    {
        return $"AuthType=ClientSecret;Url={url};TenantId={tenantId};ClientId={clientId};ClientSecret={clientSecret};RequireNewInstance=true";
    }

    // Guards the values interpolated into the client-secret connection string. The URL must be an absolute https
    // endpoint, and no value may contain ';' - the connection string is ';'-delimited, so an embedded ';' would
    // inject or corrupt parameters. '=' is intentionally NOT rejected: a value is everything after the first '=' in
    // its segment, so an '=' inside a value (e.g. base64 secret padding) is harmless. Returns an error message, or
    // null when the inputs are safe. The message never echoes a secret value. (#307)
    public static string? ValidateClientSecretConnectionInputs(string? url, string? tenantId, string? clientId, string? clientSecret)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"--url must be an absolute https URL (for example https://org.crm.dynamics.com); got '{url}'.";
        }

        foreach ((string name, string? value) in new[] { ("--url", url), ("--tenant-id", tenantId), ("--client-id", clientId), ("--client-secret", clientSecret) })
        {
            if (value != null && value.Contains(';'))
            {
                return $"The {name} value must not contain ';' - it would corrupt the Dataverse connection string.";
            }
        }

        return null;
    }

    // The Microsoft-provided sample public client for Dataverse interactive OAuth flows, documented for
    // development use. It has http://localhost registered as a loopback redirect (MSAL's system-browser flow
    // rejects any non-loopback redirect URI).
    public const string DefaultInteractiveClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    // Resolves the client secret, preferring the DATAVERSE_CLIENT_SECRET env var or stdin over the command line.
    // A secret passed via --client-secret is honored for back-compat but warned about, since it leaks into
    // shell history and process listings.
    public static string? ResolveClientSecret(string[] args)
    {
        string? fromArg = TryReadOption(args, "--client-secret");
        if (fromArg != null)
        {
            Console.Error.WriteLine("Warning: --client-secret on the command line is exposed to shell history and process listings. Prefer the DATAVERSE_CLIENT_SECRET environment variable or --client-secret-stdin.");
            return fromArg;
        }

        if (args.Any(x => string.Equals(x, "--client-secret-stdin", StringComparison.OrdinalIgnoreCase)))
        {
            // Only read when stdin is actually piped. On an interactive terminal, ReadLine() would block on a
            // typed secret that the terminal echoes in clear text — refuse and tell the caller to pipe it.
            if (!Console.IsInputRedirected)
            {
                Console.Error.WriteLine("--client-secret-stdin requires the secret to be piped in (e.g. \"$SECRET\" | vela ...); stdin is an interactive terminal.");
                return null;
            }

            Console.Error.WriteLine("Reading client secret from stdin...");
            return Console.ReadLine();
        }

        return Environment.GetEnvironmentVariable("DATAVERSE_CLIENT_SECRET");
    }

    // Transparent, headless auth: acquire a Dataverse access token from the ambient Azure credential chain via
    // DefaultAzureCredential - environment variables, workload identity / managed identity (CI, Azure-hosted),
    // and a local `az login` session. No browser and no flag: a bare `vela apply --url ...` works when the user
    // is already `az login`-ed or running under a managed identity. Returns null with a message on failure.
    public static string? TryAcquireAmbientToken(string url, out string? error)
    {
        string scope = url.TrimEnd('/') + "/.default";
        try
        {
            var credential = new Azure.Identity.DefaultAzureCredential();
            Azure.Core.AccessToken token = credential.GetToken(new Azure.Core.TokenRequestContext([scope]));
            error = null;
            return token.Token;
        }
        catch (Exception ex)
        {
            error =
                "Could not acquire a Dataverse token from the ambient Azure credential (az login / managed " +
                "identity / environment). Run 'az login', or pass --connection-string, or " +
                "--url + --tenant-id + --client-id + --client-secret, or --interactive for a browser login. " +
                $"Details: {ex.Message}";
            return null;
        }
    }

    // Interactive browser login. The MSAL public client id and authority default to the well-known
    // multi-tenant values but are overridable for sovereign clouds / custom app registrations. The token is
    // persisted by a cross-platform cache (InteractiveTokenCache), so a later run reuses it silently on every OS
    // instead of re-opening the browser. freshLogin forgets the cached account and forces a new prompt.
    public static string AcquireTokenInteractive(string url, string? authClientId, string? authority, bool freshLogin = false)
    {
        const string defaultInteractiveAuthority = "https://login.microsoftonline.com/common";

        string publicClientId = string.IsNullOrWhiteSpace(authClientId) ? DefaultInteractiveClientId : authClientId!;
        string resolvedAuthority = string.IsNullOrWhiteSpace(authority) ? defaultInteractiveAuthority : authority!;
        string scope = url.TrimEnd('/') + "/.default";

        var app = Microsoft.Identity.Client.PublicClientApplicationBuilder
            .Create(publicClientId)
            .WithAuthority(resolvedAuthority)
            .WithDefaultRedirectUri()
            .Build();

        // Persist the token cache across runs (DPAPI file on Windows, user-only file on macOS/Linux). This is
        // what makes a repeat run silent on macOS/Linux, which the Dataverse SDK's Windows-only cache did not.
        InteractiveTokenCache.RegisterOn(app);

        var accounts = app.GetAccountsAsync().GetAwaiter().GetResult().ToList();
        if (freshLogin)
        {
            // --fresh-login: drop the cached account(s) so the browser prompts for a new sign-in.
            foreach (var account in accounts)
            {
                try
                {
                    app.RemoveAsync(account).GetAwaiter().GetResult();
                }
                catch
                {
                    // Best-effort: a stale account that cannot be removed still gets overridden by the prompt below.
                }
            }

            accounts.Clear();
        }

        // Try silent auth first (cached token from a previous login).
        if (accounts.Count > 0)
        {
            try
            {
                var silent = app.AcquireTokenSilent([scope], accounts[0])
                    .ExecuteAsync().GetAwaiter().GetResult();
                return silent.AccessToken;
            }
            catch (Microsoft.Identity.Client.MsalUiRequiredException) { }
        }

        // Open browser for interactive login.
        Console.Error.WriteLine("Opening browser for Microsoft login...");
        var result = app.AcquireTokenInteractive([scope])
            .WithPrompt(Microsoft.Identity.Client.Prompt.SelectAccount)
            .ExecuteAsync().GetAwaiter().GetResult();

        return result.AccessToken;
    }

    // A dash-prefixed token that is an option (excludes a lone "-" and the bare "--" separator).
    private static bool IsOption(string token) =>
        token.Length >= 2 && token[0] == '-' && token != "--";

    // A dash-prefixed token that is a KNOWN option in either set (so it is definitely not a value). Unknown
    // dash-looking tokens are deliberately allowed as option values (e.g. a name or connection string), so they
    // are not treated as options here.
    private static bool IsKnownOption(string token, HashSet<string> valueOptions, HashSet<string> flagOptions) =>
        IsOption(token) && (valueOptions.Contains(token) || flagOptions.Contains(token));

    public static string? TryReadOption(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a value option's value, but only when the next token is a real value (not another dash-prefixed option
    /// and not absent). Unlike <see cref="TryReadOption"/>, a bare <c>--messages</c> (last token, or followed by
    /// another option) returns null. Combined with <see cref="HasFlag"/> this lets a caller distinguish
    /// "flag present with no value" from "flag present with a value" (used for <c>--messages</c> all-vs-targeted).
    /// </summary>
    public static string? TryReadOptionValue(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                string next = args[i + 1];
                return next.StartsWith("-", StringComparison.Ordinal) ? null : next;
            }
        }

        return null;
    }

    public static List<string> ReadMultiOption(string[] args, string optionName)
    {
        List<string> values = [];
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[i + 1]);
                i++;
            }
        }

        return values;
    }

    public static string? TryReadFirstPositional(string[] args, HashSet<string>? valueOptions = null)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("-", StringComparison.Ordinal))
            {
                // Skip the following token only when this option actually takes a value. With a known value-option
                // set we consult it, so a boolean flag (e.g. --strict) never swallows the positional after it (A11);
                // without one we fall back to the legacy heuristic (next token is not itself an option).
                bool skipNext = valueOptions != null
                    ? valueOptions.Contains(args[i])
                    : (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal));
                if (skipNext && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            return args[i];
        }

        return null;
    }

    /// <summary>
    /// Returns the first positional argument: the first token that does not start with <c>-</c> and is not the value
    /// of a value-taking option in <paramref name="valueOptions"/>. Returns null when there is none. Unlike
    /// <see cref="TryReadFirstPositional"/>, a value option always consumes the following token (so a name that itself
    /// starts with <c>-</c> is still skipped as that option's value). Shared by <c>vela add</c> and <c>vela scaffold</c>.
    /// </summary>
    public static string? FindPositional(string[] args, HashSet<string> valueOptions)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            if (current.StartsWith('-'))
            {
                if (i + 1 < args.Length && valueOptions.Contains(current))
                {
                    i++;
                }

                continue;
            }

            return current;
        }

        return null;
    }

    /// <summary>
    /// Returns the single top-level <c>.csproj</c> in <paramref name="directory"/>, or null when there is none or
    /// more than one. <paramref name="candidates"/> exposes every match so a caller can tailor a "none"/"multiple"
    /// message. Shared by <c>vela add</c> and <c>vela models</c>.
    /// </summary>
    public static string? SingleCsproj(string directory, out string[] candidates)
    {
        candidates = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        return candidates.Length == 1 ? candidates[0] : null;
    }

    /// <summary>True when <paramref name="flag"/> appears anywhere in <paramref name="args"/> (case-insensitive).</summary>
    public static bool HasFlag(string[] args, string flag) =>
        args.Any(x => string.Equals(x, flag, StringComparison.OrdinalIgnoreCase));

    // Role suffixes stripped to recover a solution's base namespace prefix (e.g. "Zava.Plugins" -> "Zava").
    private static readonly string[] RoleSuffixes = [".Plugins", ".Integrations", ".Tests", ".Shared"];

    /// <summary>
    /// Reads a project's root namespace: RootNamespace, else AssemblyName, else the file name. Shared by
    /// <c>vela add</c> and <c>vela models</c> so the namespace convention lives in one place.
    /// </summary>
    public static string ReadRootNamespace(string projectFile)
    {
        try
        {
            XDocument document = XDocument.Load(projectFile);
            string? rootNamespace = document.Descendants("RootNamespace").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(rootNamespace))
            {
                return rootNamespace!.Trim();
            }

            string? assemblyName = document.Descendants("AssemblyName").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                return assemblyName!.Trim();
            }
        }
        catch (Exception ex)
        {
            // Fall through to the file-name default if the project XML cannot be read, but surface why rather than
            // swallowing it silently (a malformed .csproj otherwise yields a surprising namespace with no clue).
            Console.Error.WriteLine($"Warning: could not read RootNamespace/AssemblyName from '{projectFile}' ({ex.Message}); using the file name.");
        }

        return Path.GetFileNameWithoutExtension(projectFile);
    }

    /// <summary>Strips a known role suffix (.Plugins/.Integrations/.Tests/.Shared) to recover the base prefix.</summary>
    public static string StripRoleSuffix(string rootNamespace)
    {
        foreach (string suffix in RoleSuffixes)
        {
            if (rootNamespace.EndsWith(suffix, StringComparison.Ordinal))
            {
                return rootNamespace.Substring(0, rootNamespace.Length - suffix.Length);
            }
        }

        return rootNamespace;
    }
}
