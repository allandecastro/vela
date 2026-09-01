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
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase) { "--help", "-h" };

    public static int Run(string[] args)
    {
        if (IsHelpRequested(args))
        {
            PrintUsage();
            return ExitSuccess;
        }

        if (!TryValidateKnownOptions(args, ValueOptions, FlagOptions, out int exitCode))
        {
            return exitCode;
        }

        UserConfig.ClearTokenCache();
        UserConfig.Clear();
        Console.WriteLine("Signed out: removed the cached interactive-login token and the remembered Dataverse URL.");
        return ExitSuccess;
    }
}
