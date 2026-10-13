using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela solution show &lt;uniquename&gt;</c>: a read-only command that connects to Dataverse and prints
/// the target solution's publisher customization prefix. It lets scaffolding be aligned to the solution up front -
/// feed it to <c>vela init --publisher-prefix</c> or <c>vela add customapi/test --prefix</c> - instead of discovering a
/// prefix mismatch only at <c>apply</c>. (#321)
/// </summary>
internal static class SolutionCommand
{
    private static readonly HashSet<string> ValueOptions = CliCommands.ValueOptions("solution");

    private static readonly HashSet<string> FlagOptions = CliCommands.FlagOptions("solution");

    public static int Run(string[] args)
    {
        if ((args.Length > 0 && args[0] is "help") || CommandLineOptions.IsHelpRequested(args))
        {
            PrintUsage();
            return ExitSuccess;
        }

        // The only subcommand today is `show`.
        string? sub = args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal) ? args[0] : null;
        if (!string.Equals(sub, "show", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Usage: vela solution show <uniquename> [connection options] [--json].", ExitInvalidArguments, includeUsage: false);
        }

        string[] rest = args.Skip(1).ToArray();
        if (!CommandLineOptions.TryTokenize(rest, ValueOptions, FlagOptions, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out rest, out int optExit))
        {
            return optExit;
        }

        string? uniqueName = TryReadFirstPositional(rest);
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            return Fail("Specify the solution unique name: vela solution show <uniquename>.", ExitInvalidArguments, includeUsage: false);
        }

        bool interactive = HasFlag(rest, "--interactive") || HasFlag(rest, "-i");
        bool json = HasFlag(rest, "--json");
        bool verbose = HasFlag(rest, "--verbose") || HasFlag(rest, "-v");

        if (!TryResolveConnection(rest, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        if (!TryReadTimeoutMinutes(rest, out int? timeoutMinutes, out int timeoutExit))
        {
            return timeoutExit;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: uniqueName,
            Environment: url,
            OutPath: null,
            AutoApprove: false,
            Interactive: interactive,
            Strict: false,
            Verbose: verbose,
            TimeoutMinutes: timeoutMinutes);

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;
            string prefix = DataverseRegistration.ResolveSolutionCustomizationPrefix(service, uniqueName!);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { solution = uniqueName, publisherPrefix = prefix }));
            }
            else
            {
                Console.WriteLine(prefix);
            }

            return ExitSuccess;
        }
        catch (DataverseConnectionException ex)
        {
            return FailWithException(ex, ExitConnectionFailure, verbose);
        }
        catch (Exception ex)
        {
            return Fail($"Could not read the publisher prefix for solution '{uniqueName}': {ex.Message}", ExitError, includeUsage: false);
        }
    }

    private static void PrintUsage() => CliCommands.PrintCommandHelp("solution");
}
