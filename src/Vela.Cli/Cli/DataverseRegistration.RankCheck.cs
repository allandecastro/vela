using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Execution-order (rank) collision detection at plan/apply time. Honours the "look at every step, not just
/// the ones being deployed" rule: it reads the steps already registered in the target environment on the
/// touched messages and combines them with the local desired steps before checking for rank clashes, so a
/// collision with a step owned by another assembly (or a previous deployment) is still surfaced.
/// </summary>
internal static partial class DataverseRegistration
{
    /// <summary>
    /// Projects the enabled steps of a locally-scanned <see cref="RegistrationState"/> onto <see cref="RankStep"/>
    /// coordinates. Disabled steps are dropped because a disabled step never executes and so can never collide.
    /// </summary>
    internal static List<RankStep> ExtractLocalRankSteps(RegistrationState local)
    {
        List<RankStep> steps = [];
        foreach (RegistrationItem item in local.Items)
        {
            if (!string.Equals(item.Kind, "step", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGetBool(item.Definition, "enabled", defaultValue: true))
            {
                continue;
            }

            string message = TryGetString(item.Definition, "message") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            string entity = TryGetString(item.Definition, "entityLogicalName") ?? string.Empty;
            int stage = TryGetInt(item.Definition, "stage");
            int mode = TryGetInt(item.Definition, "mode");
            int rank = TryGetInt(item.Definition, "executionOrder", defaultValue: 1);
            string label = TryGetString(item.Definition, "name") ?? item.Type;
            steps.Add(new RankStep(message, entity, stage, mode, rank, label));
        }

        return steps;
    }

    /// <summary>
    /// Reads every <em>active</em> step registered on the given messages in the environment, excluding those
    /// handled by <paramref name="excludePluginTypeIds"/> (the plugin types being deployed — their live steps
    /// are represented by the local desired set instead, so counting both would be a false positive).
    /// </summary>
    internal static List<RankStep> GetEnvironmentRankSteps(IOrganizationService service, IReadOnlyCollection<string> messageNames, ISet<Guid> excludePluginTypeIds)
    {
        List<RankStep> result = [];
        if (messageNames.Count == 0)
        {
            return result;
        }

        QueryExpression query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "eventhandler", "name", "stage", "mode", "rank")
        };
        // Active only (statecode 0): a disabled step never runs, so it can never collide.
        query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

        LinkEntity messageLink = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.Inner);
        messageLink.EntityAlias = "msg";
        messageLink.Columns = new ColumnSet("name");
        messageLink.LinkCriteria.AddCondition("name", ConditionOperator.In, messageNames.Cast<object>().ToArray());

        LinkEntity filterLink = query.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
        filterLink.EntityAlias = "filter";
        filterLink.Columns = new ColumnSet("primaryobjecttypecode");

        foreach (Entity step in service.RetrieveAll(query))
        {
            Guid handlerId = step.GetAttributeValue<EntityReference>("eventhandler")?.Id ?? Guid.Empty;
            if (excludePluginTypeIds.Contains(handlerId))
            {
                continue;
            }

            string message = GetAliasedString(step, "msg.name") ?? string.Empty;
            string entity = GetAliasedString(step, "filter.primaryobjecttypecode") ?? string.Empty;
            if (DefinitionNormalizer.IsGlobalMessage(message))
            {
                // Global messages carry no primary entity; neutralize so it groups with the local side (which
                // does the same in DefinitionNormalizer.BuildStepShape).
                entity = string.Empty;
            }

            int stage = step.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0;
            int mode = step.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0;
            int rank = step.GetAttributeValue<int?>("rank") ?? 1;
            string label = step.GetAttributeValue<string>("name") ?? "(unnamed step)";
            result.Add(new RankStep(message, entity, stage, mode, rank, label));
        }

        return result;
    }

    /// <summary>
    /// Produces the execution-order collision warnings for a deployment: reads the environment's other steps on
    /// the touched messages, unions them with <paramref name="localDesiredSteps"/>, restricts to the coordinates
    /// the deployment actually lands on (so unrelated third-party clashes stay silent), and runs the pure
    /// <see cref="RankCollisionDetector"/>. Returns an empty list when there is nothing local to deploy.
    /// </summary>
    internal static IReadOnlyList<string> BuildRankCollisionDiagnostics(IOrganizationService service, IReadOnlyList<RankStep> localDesiredSteps, ISet<Guid> ourPluginTypeIds)
    {
        if (localDesiredSteps.Count == 0)
        {
            return [];
        }

        HashSet<string> messages = localDesiredSteps
            .Select(s => s.Message)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<RankStep> envSteps = GetEnvironmentRankSteps(service, messages, ourPluginTypeIds);

        // Only report collisions on coordinates the deployment touches; a step lands there when a local desired
        // step occupies that (message, entity, stage, mode).
        HashSet<(string, string, int, int)> touched = localDesiredSteps
            .Select(s => (s.Message, s.EntityLogicalName, s.Stage, s.Mode))
            .ToHashSet();

        IEnumerable<RankStep> combined = localDesiredSteps
            .Concat(envSteps)
            .Where(s => touched.Contains((s.Message, s.EntityLogicalName, s.Stage, s.Mode)));

        return RankCollisionDetector.Detect(combined);
    }
}
