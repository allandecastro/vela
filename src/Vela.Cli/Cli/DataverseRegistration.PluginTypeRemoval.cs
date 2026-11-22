using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Plugin types that a package update removes. Dataverse refuses, atomically, to remove a plugin type that still has
/// steps, even disabled ones (verified live: 0x80040265 "Unable to delete '&lt;type&gt;' plugintype due to N step(s)"),
/// so the steps must be gone before the publish. Vela deletes its own steps first (the delete phase runs before the
/// publish); a step Vela does not delete stops the run in the preview, before any write. That the update then removes a
/// type left with no step is assumed, not verified live. A renamed or dropped assembly is a different case, refused
/// before this check runs (FindPackageAssemblySetChanges).
/// </summary>
internal static partial class DataverseRegistration
{
    /// <summary>
    /// The read-only pre-flight for the package update: the plugin types it removes (a live type of the run's
    /// assemblies that the local scan no longer has), and, for each, the live steps the run does not delete before the
    /// publish. Each such type is one blocking problem naming its steps.
    /// A Custom API's own main-operation step goes with the Custom API, so it does not block when the run deletes or
    /// detaches that Custom API. Runs only when a plugin package is updated.
    /// </summary>
    internal static IReadOnlyList<string> FindPluginTypeRemovalBlockers(IOrganizationService service, RegistrationState local, RegistrationState current, RegistrationDiff diff, bool packageUpdated)
    {
        if (!packageUpdated)
        {
            return [];
        }

        Dictionary<Guid, string> removedTypes = [];
        if (local.PluginTypeNames != null)
        {
            HashSet<string> localTypes = new(local.PluginTypeNames, StringComparer.Ordinal);
            foreach (string assembly in current.SourceAssemblies)
            {
                if (GetPluginAssembly(service, assembly) is not { } row)
                {
                    continue;
                }

                foreach ((Guid id, string typeName) in GetPluginTypes(service, row.Id).Where(t => !localTypes.Contains(t.Value)))
                {
                    removedTypes[id] = typeName;
                }
            }
        }

        if (removedTypes.Count == 0)
        {
            return [];
        }

        HashSet<Guid> deletedSteps = new(diff.Removed.Where(DiffEngine.IsStep).Select(i => i.LiveId).OfType<Guid>());
        HashSet<string> releasedApis = new(
            diff.Removed.Where(i => i.Kind == "custom-api").Select(i => TryGetString(i.Definition, "uniqueName") ?? i.Type)
                .Concat(diff.Changed.Where(c => c.Kind == "custom-api" && IsCustomApiImplementationChange(c)).Select(c => TryGetString(c.Before, "uniqueName") ?? c.Type)),
            StringComparer.OrdinalIgnoreCase);

        List<Entity> steps = service.RetrieveAllInChunks(removedTypes.Keys.ToArray(), chunk =>
        {
            QueryExpression query = new QueryExpression("sdkmessageprocessingstep") { ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "eventhandler", "name", "stage") };
            query.Criteria.AddCondition("eventhandler", ConditionOperator.In, chunk);
            LinkEntity message = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.LeftOuter);
            message.EntityAlias = "msg";
            message.Columns = new ColumnSet("name");
            return query;
        });

        return steps
            .Where(step => !deletedSteps.Contains(step.Id))
            .Where(step => !((step.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0) == SdkConstants.PipelineStage.MainOperation
                && releasedApis.Contains(GetAliasedString(step, "msg.name") ?? string.Empty)))
            .GroupBy(step => step.GetAttributeValue<EntityReference>("eventhandler")?.Id ?? Guid.Empty)
            .Where(group => removedTypes.ContainsKey(group.Key))
            .OrderBy(group => removedTypes[group.Key], StringComparer.Ordinal)
            .Select(group =>
            {
                string[] names = group.Select(s => s.GetAttributeValue<string>("name") is { Length: > 0 } n ? $"'{n}'" : s.Id.ToString())
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray();
                return $"The plugin package update removes plugin type '{removedTypes[group.Key]}', but {names.Length} step(s) that Vela " +
                    $"does not manage are registered on it: {string.Join(", ", names)}. Dataverse refuses to delete a plugin type that " +
                    "has steps, even disabled ones (0x80040265). Delete those steps or move them to another plugin type, then run again.";
            })
            .ToArray();
    }
}
