using System.Collections.Generic;

namespace Vela.Cli;

/// <summary>
/// Builds a human-readable resume report when an <c>apply</c> run fails partway through.
/// Dataverse has no cross-entity transaction, so a mid-run failure leaves some operations applied
/// and others not. This report tells the operator exactly what landed and what is still pending.
/// Pure and side-effect free so it can be unit tested.
/// </summary>
internal static class ApplyResumeReport
{
    public static IReadOnlyList<string> Build(
        string failedOperation,
        string failureMessage,
        IReadOnlyList<string> completed,
        IReadOnlyList<string> remaining)
    {
        List<string> lines =
        [
            "apply failed mid-run; Dataverse may be in a partially-updated state.",
            $"  failed at: {failedOperation}",
            $"  reason: {failureMessage}",
            $"  completed ({completed.Count}):"
        ];

        if (completed.Count == 0)
        {
            lines.Add("    (none)");
        }
        else
        {
            foreach (string op in completed)
            {
                lines.Add($"    + {op}");
            }
        }

        lines.Add($"  not applied ({remaining.Count}):");
        if (remaining.Count == 0)
        {
            lines.Add("    (none)");
        }
        else
        {
            foreach (string op in remaining)
            {
                lines.Add($"    - {op}");
            }
        }

        lines.Add("  operations are idempotent (upserts and already-applied deletes are safe to repeat): fix the cause and re-run 'apply --execute' to resume.");
        return lines;
    }
}
