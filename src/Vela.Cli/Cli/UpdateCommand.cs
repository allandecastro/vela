using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;

namespace Vela.Cli;

/// <summary>
/// <c>vela update</c>: updates the installed global tool by shelling out to
/// <c>dotnet tool update --global Vela.Cli</c> (optionally pinned with <c>--version</c>). On Windows the running
/// executable is locked, so an in-place self-update cannot complete while <c>vela</c> is running; in that case the
/// command explains how to run it from a fresh shell instead.
/// </summary>
internal static class UpdateCommand
{
    internal const string PackageId = "Vela.Cli";

    // The `dotnet` arguments for the update, optionally pinned to a version. Pure, so it stays unit-testable.
    internal static string[] BuildDotnetArgs(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? ["tool", "update", "--global", PackageId]
            : ["tool", "update", "--global", PackageId, "--version", version!];

    public static int Run(string[] args)
    {
        if (IsHelpRequested(args))
        {
            CliCommands.PrintCommandHelp("update");
            return ExitSuccess;
        }

        // Only --version is accepted: an unknown option fails instead of being ignored.
        if (!TryTokenize(args, CliCommands.ValueOptions("update"), CliCommands.FlagOptions("update"), out args, out int optExit))
        {
            return optExit;
        }

        string? version = TryReadOption(args, "--version");
        string[] dotnetArgs = BuildDotnetArgs(version);
        Console.WriteLine($"Updating {ToolInfo.ProductName} via 'dotnet {string.Join(" ", dotnetArgs)}' ...");

        int code;
        try
        {
            ProcessStartInfo psi = new("dotnet") { UseShellExecute = false };
            foreach (string argument in dotnetArgs)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return FailManual("Could not start 'dotnet'.");
            }

            process.WaitForExit();
            code = process.ExitCode;
        }
        catch (Exception ex)
        {
            return FailManual($"Could not run 'dotnet tool update': {ex.Message}");
        }

        if (code != 0)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.Error.WriteLine("The update could not complete while vela is running: Windows locks the running executable.");
                Console.Error.WriteLine($"Run it from a new shell instead: dotnet tool update --global {PackageId}");
            }

            return ExitError;
        }

        Console.WriteLine("Update complete. Run 'vela version' to confirm.");
        return ExitSuccess;
    }

    private static int FailManual(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine($"Update manually: dotnet tool update --global {PackageId}");
        return ExitError;
    }
}
