using System;

namespace Vela.Cli;

/// <summary>The outcome of the <c>apply</c> confirmation gate.</summary>
internal enum ApplyDecision
{
    /// <summary>Proceed with the apply.</summary>
    Apply,

    /// <summary>The user was asked and declined.</summary>
    Cancel,

    /// <summary>No TTY to confirm on and no --auto-approve: refuse (fail closed).</summary>
    RefuseNonInteractive,
}

/// <summary>
/// Pure decision for the <c>apply</c> confirmation gate, extracted from the command flow so it can be unit
/// tested without a live connection. <paramref name="readConfirmation"/> is only invoked in the interactive
/// case (not auto-approved, stdin is a TTY); it performs the prompt and returns the typed answer.
/// </summary>
internal static class ApplyConfirmation
{
    public static ApplyDecision Decide(bool autoApprove, bool inputRedirected, Func<string?> readConfirmation)
    {
        if (autoApprove)
        {
            return ApplyDecision.Apply;
        }

        if (inputRedirected)
        {
            return ApplyDecision.RefuseNonInteractive;
        }

        string? answer = readConfirmation();
        return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            ? ApplyDecision.Apply
            : ApplyDecision.Cancel;
    }
}
