using System;
using System.Collections.Generic;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// <c>vela logout</c>: removes the persisted interactive-login token cache and the remembered Dataverse URL, so the
/// next <c>--interactive</c> run starts a fresh browser login. Touches only local files - never calls Dataverse.
/// </summary>
internal static class LogoutCommand
{
    // logout takes no options; only help is accepted so an unknown flag fails fast like the other commands.
    private static readonly HashSet<string> ValueOptions = CliCommands.ValueOptions("logout");
    private static readonly HashSet<string> FlagOptions = CliCommands.FlagOptions("logout");

    public static int Run(string[] args)
    {
        if (IsHelpRequested(args))
        {
            CliCommands.PrintCommandHelp("logout");
            return ExitSuccess;
        }

        if (!TryTokenize(args, ValueOptions, FlagOptions, out args, out int exitCode))
        {
            return exitCode;
        }

        InteractiveTokenCache.Clear(); // the current cross-platform MSAL cache file
        UserConfig.ClearTokenCache();  // the legacy Dataverse-SDK token.dat (pre-fix caches)
        UserConfig.Clear();
        Console.WriteLine("Signed out: removed the cached interactive-login token and the remembered Dataverse URL.");
        return ExitSuccess;
    }
}
