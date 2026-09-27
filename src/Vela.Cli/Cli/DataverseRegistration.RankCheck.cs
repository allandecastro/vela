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
    internal static List<RankStep> GetEnvironmentRankSteps(IOrganizationService service, IReadOnlyCollection<string> messageNames, ISet<Guid> excludePluginTypeIds, IReadOnlyCollection<string> touchedEntities, bool hasGlobalCoordinate)
    {
        List<RankStep> result = [];
        if (messageNames.Count == 0)
        {
            return result;
        }

        void AppendRows(QueryExpression q)
        {
            foreach (Entity step in service.RetrieveAll(q))
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
                int rank = NormalizeRegistrationRank(step.GetAttributeValue<int?>("rank") ?? 1);
                string label = step.GetAttributeValue<string>("name") ?? "(unnamed step)";
                result.Add(new RankStep(message, entity, stage, mode, rank, label));
            }
        }

        QueryExpression BaseQuery()
        {
            QueryExpression q = new QueryExpression("sdkmessageprocessingstep")
            {
                ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "eventhandler", "name", "stage", "mode", "rank")
            };
            // Active only (statecode 0): a disabled step never runs, so it can never collide.
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            LinkEntity msg = q.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.Inner);
            msg.EntityAlias = "msg";
            msg.Columns = new ColumnSet("name");
            msg.LinkCriteria.AddCondition("name", ConditionOperator.In, messageNames.Cast<object>().ToArray());
            return q;
        }

        QueryExpression query = BaseQuery();

        // When every touched coordinate is entity-bound, only steps on those same entities can ever land on a
        // touched (message, entity, stage, mode) coordinate, so an Inner join filtered by primaryobjecttypecode
        // reads far fewer rows on a big org (BuildRankCollisionDiagnostics filters to the touched coordinates
        // anyway). A global coordinate (entity-less message / custom API) has no primaryobjecttypecode to match, so
        // in that case keep the unrestricted LeftOuter link for correctness.
        bool scopedToEntities = !hasGlobalCoordinate && touchedEntities.Count > 0;
        LinkEntity filterLink;
        if (scopedToEntities)
        {
            filterLink = query.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.Inner);
            filterLink.LinkCriteria.AddCondition("primaryobjecttypecode", ConditionOperator.In, touchedEntities.Cast<object>().ToArray());
        }
        else
        {
            filterLink = query.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
        }

        filterLink.EntityAlias = "filter";
        filterLink.Columns = new ColumnSet("primaryobjecttypecode");

        AppendRows(query);

        // The entity-scoped Inner join above drops filter-less ("global") steps - a step registered with no
        // sdkmessagefilter fires for EVERY entity on its message, so it collides with the touched entity-bound steps
        // even though it has no primaryobjecttypecode. Read those separately so the collision is not missed (#305).
        // The LeftOuter path already includes them, so this supplement runs only in the entity-scoped path.
        if (scopedToEntities)
        {
            QueryExpression globalQuery = BaseQuery();
            globalQuery.Criteria.AddCondition("sdkmessagefilterid", ConditionOperator.Null);
            AppendRows(globalQuery);
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

        // The distinct entities the deployment lands on, and whether any touched coordinate is global (entity-less
        // message / custom API). Together these let GetEnvironmentRankSteps scope the read to the touched entities
        // when it is loss-free to do so (no global coordinate present).
        HashSet<string> touchedEntities = localDesiredSteps
            .Select(s => s.EntityLogicalName)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasGlobalCoordinate = localDesiredSteps
            .Any(s => string.IsNullOrWhiteSpace(s.EntityLogicalName) || DefinitionNormalizer.IsGlobalMessage(s.Message));

        List<RankStep> envSteps = GetEnvironmentRankSteps(service, messages, ourPluginTypeIds, touchedEntities, hasGlobalCoordinate);

        // Only report collisions on coordinates the deployment touches; a step lands there when a local desired
        // step occupies that (message, entity, stage, mode).
        HashSet<(string, string, int, int)> touched = localDesiredSteps
            .Select(s => (s.Message, s.EntityLogicalName, s.Stage, s.Mode))
            .ToHashSet();

        // A filter-less "global" step (empty entity on an entity-scoped message) fires for EVERY entity on that
        // message, so it collides with entity-bound steps sharing (message, stage, mode) even though its own
        // coordinate differs. The detector groups by exact coordinate, so project each global step onto the touched
        // entity coordinates it covers, so it is compared against the entity-bound steps there (#305).
        Dictionary<(string, int, int), List<string>> touchedEntitiesByCoord = localDesiredSteps
            .Where(s => !string.IsNullOrWhiteSpace(s.EntityLogicalName) && !DefinitionNormalizer.IsGlobalMessage(s.Message))
            .GroupBy(s => (s.Message, s.Stage, s.Mode))
            .ToDictionary(g => g.Key, g => g.Select(s => s.EntityLogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        List<RankStep> forDetection = [];
        foreach (RankStep s in localDesiredSteps.Concat(envSteps))
        {
            bool isGlobalStep = string.IsNullOrWhiteSpace(s.EntityLogicalName) && !DefinitionNormalizer.IsGlobalMessage(s.Message);
            if (isGlobalStep && touchedEntitiesByCoord.TryGetValue((s.Message, s.Stage, s.Mode), out List<string>? entities))
            {
                foreach (string entity in entities)
                {
                    forDetection.Add(s with { EntityLogicalName = entity });
                }
            }

            // Keep the step on its own coordinate when the deployment touches it (entity-bound steps, and
            // global-vs-global collisions on an entity-less coordinate the deployment actually lands on).
            if (touched.Contains((s.Message, s.EntityLogicalName, s.Stage, s.Mode)))
            {
                forDetection.Add(s);
            }
        }

        return RankCollisionDetector.Detect(forDetection);
    }
}
