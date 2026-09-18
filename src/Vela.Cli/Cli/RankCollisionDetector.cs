using System;
using System.Collections.Generic;
using System.Linq;

namespace Vela.Cli;

/// <summary>
/// A plugin step reduced to the coordinates that decide its pipeline execution order: message, primary
/// entity, stage, mode and rank. <see cref="Label"/> is the human-readable step name used in warnings.
/// </summary>
internal readonly record struct RankStep(string Message, string EntityLogicalName, int Stage, int Mode, int Rank, string Label);

/// <summary>
/// Pure detection of execution-order (rank) collisions. Dataverse runs the steps registered on the same
/// message + primary entity + stage + mode in ascending <c>rank</c>; two steps that share a rank there
/// execute in an order Dataverse does not define. This finds those clashes so <c>plan</c>/<c>apply</c> can
/// warn about them. It is deliberately I/O-free: the caller supplies the full step set to examine (both the
/// steps being deployed and the ones already registered in the environment), so it can be unit-tested offline.
/// </summary>
internal static class RankCollisionDetector
{
    /// <summary>
    /// Returns one warning per colliding (coordinate, rank) group, ordered stably (by message, entity, stage,
    /// mode, then rank) so output is deterministic. Steps identical in every coordinate <em>and</em> label are
    /// treated as the same registration and collapsed, so supplying a step twice never fabricates a collision.
    /// Callers are expected to have already excluded disabled steps, which never execute.
    /// </summary>
    internal static IReadOnlyList<string> Detect(IEnumerable<RankStep> steps)
    {
        List<string> warnings = [];

        var groups = steps
            .Distinct()
            .GroupBy(s => (s.Message, s.EntityLogicalName, s.Stage, s.Mode))
            .OrderBy(g => g.Key.Message, StringComparer.Ordinal)
            .ThenBy(g => g.Key.EntityLogicalName, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Stage)
            .ThenBy(g => g.Key.Mode);

        foreach (var group in groups)
        {
            foreach (IGrouping<int, RankStep> rankGroup in group.GroupBy(s => s.Rank).Where(r => r.Count() > 1).OrderBy(r => r.Key))
            {
                string[] labels = rankGroup
                    .Select(s => s.Label)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                warnings.Add(FormatWarning(group.Key.Message, group.Key.EntityLogicalName, group.Key.Stage, group.Key.Mode, rankGroup.Key, labels));
            }
        }

        return warnings;
    }

    private static string FormatWarning(string message, string entity, int stage, int mode, int rank, string[] labels)
    {
        string lifecycle = DefinitionNormalizer.BuildStepLifecycleLabel(stage, mode);
        string scope = string.IsNullOrWhiteSpace(entity) ? message : $"{message} of {entity}";
        return $"Execution-order collision: {labels.Length} steps share rank {rank} on {scope} at {lifecycle}. " +
            $"Their execution order is undefined; give them distinct WithExecutionOrder(...) values. Steps: {string.Join("; ", labels)}.";
    }
}
