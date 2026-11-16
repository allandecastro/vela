using System.Collections.Generic;
using System.Linq;

namespace Vela.Cli;

/// <summary>
/// Builds a human-readable resume report when an <c>apply</c> run fails partway through.
/// Dataverse has no cross-entity transaction, so a mid-run failure leaves some operations applied
/// and others not. This report tells the operator exactly what landed, what failed and what is still pending.
/// Pure and side-effect free so it can be unit tested.
/// </summary>
internal static class ApplyResumeReport
{
    public static IReadOnlyList<string> Build(
        string failedOperation,
        string failureMessage,
        IReadOnlyList<string> completed,
        IReadOnlyList<string> remaining) =>
        Build([failedOperation], failureMessage, completed, remaining);

    /// <summary>
    /// Builds the report for one or more failed operations. A batched tier runs with continue-on-error, so several
    /// owners can fail in the same run while later ones still land: each operation is listed under exactly one of
    /// completed / failed / not applied.
    /// </summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<string> failedOperations,
        string failureMessage,
        IReadOnlyList<string> completed,
        IReadOnlyList<string> remaining)
    {
        List<string> lines = ["apply failed mid-run; Dataverse may be in a partially-updated state."];
        if (failedOperations.Count == 1)
        {
            lines.Add($"  failed at: {failedOperations[0]}");
        }
        else
        {
            lines.Add($"  failed ({failedOperations.Count}):");
            lines.AddRange(failedOperations.Select(op => $"    ! {op}"));
        }

        lines.Add($"  reason: {failureMessage}");
        AddSection(lines, "completed", "+", completed);
        AddSection(lines, "not applied", "-", remaining);
        lines.Add("  operations are idempotent (upserts and already-applied deletes are safe to repeat): fix the cause and re-run 'apply' (add --auto-approve in CI) to resume.");
        return lines;
    }

    private static void AddSection(List<string> lines, string title, string bullet, IReadOnlyList<string> operations)
    {
        lines.Add($"  {title} ({operations.Count}):");
        if (operations.Count == 0)
        {
            lines.Add("    (none)");
            return;
        }

        lines.AddRange(operations.Select(op => $"    {bullet} {op}"));
    }
}

/// <summary>
/// Tracks the outcome of every planned apply operation, so the resume report lists exactly what landed. The
/// sequential phases (publish, web resources) advance a cursor; a batched phase records each owner's own outcome,
/// because a continue-on-error batch can fail an early owner and still apply the later ones.
/// </summary>
internal sealed class ApplyProgress(IReadOnlyList<string> planned)
{
    private readonly HashSet<int> _completed = [];
    private readonly SortedSet<int> _failed = [];

    public IReadOnlyList<string> Planned { get; } = planned;

    /// <summary>The index of the next planned operation of the sequential flow.</summary>
    public int Cursor { get; private set; }

    /// <summary>Marks the operation at the cursor as completed and advances.</summary>
    public void CompleteNext()
    {
        _completed.Add(Cursor);
        Cursor++;
    }

    public void Complete(int index) => _completed.Add(index);

    public void Fail(int index) => _failed.Add(index);

    /// <summary>Moves the cursor past a batched phase whose owners were recorded individually.</summary>
    public void MoveTo(int index) => Cursor = index;

    public bool IsCompleted(int index) => _completed.Contains(index) && !_failed.Contains(index);

    /// <summary>
    /// The resume report for a failure. When no operation was recorded as failed (a synchronous throw), the operation
    /// at the cursor is the one that failed.
    /// </summary>
    public IReadOnlyList<string> BuildReport(string failureMessage)
    {
        (List<string> completed, List<string> failed, List<string> remaining) = Outcomes();
        return ApplyResumeReport.Build(failed.Count > 0 ? failed : ["(unknown operation)"], failureMessage, completed, remaining);
    }

    /// <summary>
    /// Every planned operation under exactly one of completed, failed and not applied. When no operation was recorded as
    /// failed (a synchronous throw), the operation at the cursor is the one that failed.
    /// </summary>
    public (List<string> Completed, List<string> Failed, List<string> NotApplied) Outcomes()
    {
        IEnumerable<int> failedIndexes = _failed.Count > 0 ? _failed : Cursor < Planned.Count ? [Cursor] : [];
        HashSet<int> failed = [.. failedIndexes];
        List<string> completed = [];
        List<string> remaining = [];
        for (int i = 0; i < Planned.Count; i++)
        {
            if (failed.Contains(i))
            {
                continue;
            }

            (_completed.Contains(i) ? completed : remaining).Add(Planned[i]);
        }

        return (completed, failed.OrderBy(i => i).Select(i => Planned[i]).ToList(), remaining);
    }
}

/// <summary>
/// What an apply run did, filled while it runs so a failure can still report it (the apply-failed JSON): the progress of
/// <see cref="DataverseRegistration.ApplyDiff"/> and the step that runs after it (adding components to the solution).
/// </summary>
internal sealed class ApplyRun
{
    public ApplyProgress? Progress { get; set; }

    /// <summary>The operation running after ApplyDiff completed, if any (it is the one that failed).</summary>
    public string? Pending { get; set; }

    public ApplyFailure ToFailure(string error)
    {
        if (Progress == null)
        {
            return new ApplyFailure(error, [], Pending == null ? [] : [Pending], []);
        }

        (List<string> completed, List<string> failed, List<string> notApplied) = Progress.Outcomes();
        if (Pending != null)
        {
            failed.Add(Pending);
        }

        return new ApplyFailure(error, [.. completed], [.. failed], [.. notApplied]);
    }
}
