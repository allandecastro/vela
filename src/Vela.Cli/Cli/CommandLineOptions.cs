using System;
using System.Collections.Generic;
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
        "--auth-client-id", "--authority",
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
        "--package-assembly", "--environment", "--out",
    };

    private static readonly HashSet<string> SharedFlagOptions = new(ConnectionFlagOptions, StringComparer.OrdinalIgnoreCase)
    {
        "--interactive", "-i", "--execute", "--strict", "--verbose", "-v", "--help", "-h",
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

            exitCode = Fail($"Unknown option '{token}'. Run 'vela <command> --help' to see the available options.", ExitInvalidArguments);
            return false;
        }

        exitCode = ExitSuccess;
        return true;
    }

    public static bool TryParseSharedOptions(string[] args, out SharedOptions options, out int exitCode)
    {
        options = default!;
        exitCode = ExitSuccess;

        // Fail fast on a misspelled/unknown option before prompting or reading anything else.
        if (!TryValidateKnownOptions(args, out exitCode))
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

        List<string> packagePaths = [];
        packagePaths.AddRange(ReadMultiOption(args, "--package"));
        packagePaths.AddRange(ReadMultiOption(args, "-p"));

        string? packagesDir = TryReadOption(args, "--packages-dir");
        if (!string.IsNullOrWhiteSpace(packagesDir))
        {
            string fullDir = Path.GetFullPath(packagesDir);
            if (!Directory.Exists(fullDir))
            {
                exitCode = Fail($"Packages directory not found: {fullDir}", ExitFileNotFound, includeUsage: false);
                return false;
            }

            packagePaths.AddRange(Directory.GetFiles(fullDir, "*.nupkg", SearchOption.TopDirectoryOnly));
        }

        if (packagePaths.Count == 0)
        {
            string? positional = TryReadFirstPositional(args, SharedValueOptions);
            if (!string.IsNullOrWhiteSpace(positional))
            {
                packagePaths.Add(positional);
            }
        }

        // With a web resources root, running without any plugin package is valid (web-resource-only deploy).
        if (packagePaths.Count == 0 && string.IsNullOrWhiteSpace(webResourcesRoot))
        {
            if (interactive)
            {
                string? packagePrompt = Prompt("Package .nupkg path (or leave empty to use a folder):");
                if (!string.IsNullOrWhiteSpace(packagePrompt))
                {
                    packagePaths.Add(packagePrompt);
                }
                else
                {
                    string? folderPrompt = Prompt("Packages folder path:");
                    if (!string.IsNullOrWhiteSpace(folderPrompt))
                    {
                        packagesDir = folderPrompt;
                        string fullDir = Path.GetFullPath(packagesDir);
                        if (!Directory.Exists(fullDir))
                        {
                            exitCode = Fail($"Packages directory not found: {fullDir}", ExitFileNotFound, includeUsage: false);
                            return false;
                        }

                        packagePaths.AddRange(Directory.GetFiles(fullDir, "*.nupkg", SearchOption.TopDirectoryOnly));
                    }
                }
            }

            if (packagePaths.Count == 0)
            {
                exitCode = Fail("Missing package input. Use --package <file.nupkg> (repeatable) or --packages-dir <folder>.", ExitInvalidArguments);
                return false;
            }
        }

        string[] resolvedPackages = packagePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string packagePath in resolvedPackages)
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

        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out exitCode))
        {
            return false;
        }

        options = new SharedOptions(
            PackagePaths: resolvedPackages,
            PackageAssembly: TryReadOption(args, "--package-assembly") ?? (interactive ? Prompt("Package assembly name (optional, example: Zava.Plugins.Package.dll):", allowEmpty: true) : null),
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: TryReadOption(args, "--solution") ?? TryReadOption(args, "--solution-unique-name"),
            Environment: TryReadOption(args, "--environment") ?? url,
            OutPath: TryReadOption(args, "--out"),
            Execute: args.Any(x => string.Equals(x, "--execute", StringComparison.OrdinalIgnoreCase)),
            Interactive: interactive,
            Strict: args.Any(x => string.Equals(x, "--strict", StringComparison.OrdinalIgnoreCase)),
            Verbose: args.Any(x => string.Equals(x, "--verbose", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "-v", StringComparison.OrdinalIgnoreCase)),
            WebResourcesRoot: webResourcesRoot);
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
                string? authority = TryReadOption(args, "--authority");
                if (!string.IsNullOrWhiteSpace(authority))
                {
                    // A custom authority (sovereign cloud) can't be expressed in an OAuth connection string, so
                    // fall back to the token-callback path (this one does not persist a token cache).
                    oauthUrl = url!;
                    oauthToken = AcquireTokenInteractive(url!, TryReadOption(args, "--auth-client-id"), authority);
                }
                else
                {
                    // Preferred: let the Dataverse SDK run the browser login and persist/reuse the token via
                    // TokenCacheStorePath, so a repeat run is silent. --fresh-login forces a new prompt.
                    bool freshLogin = HasFlag(args, "--fresh-login");
                    if (freshLogin)
                    {
                        UserConfig.ClearTokenCache();
                    }

                    UserConfig.EnsureConfigDir();
                    connectionString = BuildOAuthConnectionString(url!, TryReadOption(args, "--auth-client-id"), UserConfig.TokenCachePath, freshLogin);
                }
            }
            else if (string.IsNullOrWhiteSpace(url) ||
                string.IsNullOrWhiteSpace(tenantId) ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret))
            {
                exitCode = Fail(
                    "Missing Dataverse auth. Provide --connection-string OR --url + --tenant-id + --client-id + --client-secret OR use --interactive for browser login.",
                    ExitInvalidArguments);
                return false;
            }
            else
            {
                connectionString = BuildClientSecretConnectionString(url, tenantId, clientId, clientSecret);
            }
        }

        return true;
    }

    public static string BuildClientSecretConnectionString(string url, string tenantId, string clientId, string clientSecret)
    {
        return $"AuthType=ClientSecret;Url={url};TenantId={tenantId};ClientId={clientId};ClientSecret={clientSecret};RequireNewInstance=true";
    }

    // The Microsoft-provided sample public client (AppId + RedirectUri) for Dataverse interactive OAuth flows,
    // documented for development use. It is the same client the previous custom MSAL flow used.
    public const string DefaultInteractiveClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    private const string DefaultInteractiveRedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

    /// <summary>
    /// Builds the interactive OAuth connection string. The Dataverse SDK opens the browser on first use and, via
    /// <c>TokenCacheStorePath</c>, persists the MSAL token (DPAPI-encrypted on Windows) so later runs reuse it
    /// silently (<c>LoginPrompt=Auto</c>). <paramref name="freshLogin"/> forces a new prompt (<c>Always</c>).
    /// </summary>
    public static string BuildOAuthConnectionString(string url, string? appId, string tokenCachePath, bool freshLogin)
    {
        string clientId = string.IsNullOrWhiteSpace(appId) ? DefaultInteractiveClientId : appId!;
        string loginPrompt = freshLogin ? "Always" : "Auto";
        return $"AuthType=OAuth;Url={url};AppId={clientId};RedirectUri={DefaultInteractiveRedirectUri};" +
               $"TokenCacheStorePath={tokenCachePath};LoginPrompt={loginPrompt};RequireNewInstance=true";
    }

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

    // Interactive browser login. The MSAL public client id and authority default to the well-known
    // multi-tenant values but are overridable for sovereign clouds / custom app registrations.
    public static string AcquireTokenInteractive(string url, string? authClientId, string? authority)
    {
        // Default MSAL public client used for interactive browser login.
        const string defaultInteractiveClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        const string defaultInteractiveAuthority = "https://login.microsoftonline.com/common";

        string publicClientId = string.IsNullOrWhiteSpace(authClientId) ? defaultInteractiveClientId : authClientId;
        string resolvedAuthority = string.IsNullOrWhiteSpace(authority) ? defaultInteractiveAuthority : authority;
        string scope = url.TrimEnd('/') + "/.default";

        var app = Microsoft.Identity.Client.PublicClientApplicationBuilder
            .Create(publicClientId)
            .WithAuthority(resolvedAuthority)
            .WithDefaultRedirectUri()
            .Build();

        // Try silent auth first (cached token from a previous login)
        var accounts = app.GetAccountsAsync().GetAwaiter().GetResult();
        if (accounts.Any())
        {
            try
            {
                var silent = app.AcquireTokenSilent([scope], accounts.First())
                    .ExecuteAsync().GetAwaiter().GetResult();
                return silent.AccessToken;
            }
            catch (Microsoft.Identity.Client.MsalUiRequiredException) { }
        }

        // Open browser for interactive login
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
        catch
        {
            // Fall through to the file-name default if the project XML cannot be read.
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
