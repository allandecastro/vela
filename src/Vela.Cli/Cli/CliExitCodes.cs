namespace Vela.Cli;

/// <summary>
/// Process exit codes returned by the CLI. Part of the tool's external contract.
/// </summary>
internal static class CliExitCodes
{
    public const int ExitSuccess = 0;
    public const int ExitInvalidArguments = 2;
    public const int ExitFileNotFound = 3;
    public const int ExitParseError = 4;
    public const int ExitConnectionFailure = 6;
}
