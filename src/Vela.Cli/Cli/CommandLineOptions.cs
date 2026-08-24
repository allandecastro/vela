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
    public static bool TryParseSharedOptions(string[] args, out SharedOptions options, out int exitCode)
    {
        options = default!;
        exitCode = ExitSuccess;
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
            string? positional = TryReadFirstPositional(args);
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
            url = string.IsNullOrWhiteSpace(url) ? Prompt("Dataverse URL (https://org.crm.dynamics.com):") : url;

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
                oauthUrl = url!;
                oauthToken = AcquireTokenInteractive(url!, TryReadOption(args, "--auth-client-id"), TryReadOption(args, "--authority"));
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

    public static string? TryReadFirstPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("-", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
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
