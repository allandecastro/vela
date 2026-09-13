namespace Vela.Cli;

/// <summary>
/// Process exit codes returned by the CLI. Part of the tool's external contract.
/// </summary>
internal static class CliExitCodes
{
    public const int ExitSuccess = 0;
    public const int ExitInvalidArguments = 2;
    public const int ExitFileNotFound = 3;

    /// <summary>General command failure (an unhandled exception, a strict-mode failure, a shelled-out tool failing,
    /// ...). The value (4) is unchanged from the former <c>ExitParseError</c>; only the name was clarified, since
    /// it is the shared catch-all across commands, not specific to parsing.</summary>
    public const int ExitError = 4;

    public const int ExitConnectionFailure = 6;
}
