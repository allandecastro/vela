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

    /// <summary>A mid-run <c>apply</c> failure: Dataverse has no cross-entity transaction, so some operations may have
    /// landed before the failure. A distinct code (7) lets a scripted/CI caller tell "the org may be half-updated,
    /// re-run apply to reconcile" apart from a bad-args/strict failure (4) or a connection problem (6). The failure
    /// path also emits a machine-readable result (mode <c>apply-failed</c>) for <c>--json</c>/<c>--out</c> callers.</summary>
    public const int ExitPartialApply = 7;

    /// <summary><c>vela plan --detailed-exitcode</c> only: the plan succeeded and found changes to apply (including a
    /// package republish or a solution-membership fix). Without the flag a successful plan always exits 0. Like
    /// Terraform's <c>-detailed-exitcode</c>, but with its own value because 2 already means invalid arguments.</summary>
    public const int ExitChangesPending = 8;
}
