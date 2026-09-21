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

    /// <summary>Drift was detected by a read-only check (currently <c>vela models --verify</c>): the command ran
    /// successfully but found the local model out of sync with the live schema. A distinct code (5) lets CI tell a
    /// "your model is stale" result apart from a genuine command failure (4) or a connection problem (6).</summary>
    public const int ExitDriftDetected = 5;

    public const int ExitConnectionFailure = 6;
}
