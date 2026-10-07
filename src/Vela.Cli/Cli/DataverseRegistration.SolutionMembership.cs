using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// A Vela-owned component that exists in Dataverse but is not part of the <c>--solution</c> being applied to.
/// </summary>
public sealed record SolutionMembershipAction(string Kind, string Name, int ComponentType, Guid ComponentId);

/// <summary>
/// Reconciles <c>--solution</c> membership. A create or update made by <c>apply</c> carries the
/// <c>SolutionUniqueName</c> request parameter, so it lands in the solution, but a component that did not change
/// (deployed before, without <c>--solution</c> or into another solution) was never added: the solution then exports
/// without it. Plan lists each such component as an <c>add-to-solution</c> operation and apply adds it with
/// <c>AddSolutionComponent</c>.
/// </summary>
internal static partial class DataverseRegistration
{
    // Fixed solution component types (Microsoft.Crm.Sdk.Messages componenttype option set).
    internal const int PluginAssemblyComponentType = 91;
    internal const int SdkMessageProcessingStepComponentType = 92;

    // Table-based components (customapi, its parameters, pluginpackage) have an organization-specific component type,
    // read from solutioncomponentdefinition by table name.
    private static readonly string[] TableComponentEntities = ["pluginpackage", "customapi", "customapirequestparameter", "customapiresponseproperty"];

    /// <summary>
    /// The Vela-owned components (plugin package, plugin assembly, steps, Custom APIs and their parameters of the
    /// managed plugin types) that exist but are not in <paramref name="solutionUniqueName"/>. A component kind whose
    /// component type cannot be resolved is skipped and reported in <paramref name="info"/>.
    /// </summary>
    internal static IReadOnlyList<SolutionMembershipAction> ComputeSolutionMembershipActions(
        IOrganizationService service,
        string solutionUniqueName,
        string[] assemblies,
        PluginPackagePayload[] packages,
        IReadOnlyCollection<string>? unmanagedPluginTypes,
        List<string> info)
    {
        Dictionary<string, int> tableTypes = ResolveTableComponentTypes(service);
        List<SolutionMembershipAction> candidates = [];
        HashSet<string> unmanaged = new(unmanagedPluginTypes ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> unresolved = new(StringComparer.Ordinal);

        void AddTable(string entity, string kind, string name, Guid id)
        {
            if (tableTypes.TryGetValue(entity, out int componentType))
            {
                candidates.Add(new SolutionMembershipAction(kind, name, componentType, id));
            }
            else
            {
                unresolved.Add(entity);
            }
        }

        if (packages.Length > 0)
        {
            string prefix = ResolveSolutionCustomizationPrefix(service, solutionUniqueName);
            foreach (PluginPackagePayload package in packages)
            {
                string name = NormalizePackageNameWithPrefix(package.PackageName, prefix);
                if (GetPluginPackage(service, name) is { } existing)
                {
                    AddTable("pluginpackage", "package", name, existing.Id);
                }
            }
        }

        foreach (string assemblyName in assemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Entity? assembly = GetPluginAssembly(service, assemblyName);
            if (assembly == null)
            {
                continue;
            }

            candidates.Add(new SolutionMembershipAction("assembly", assemblyName, PluginAssemblyComponentType, assembly.Id));
            Guid[] typeIds = GetPluginTypes(service, assembly.Id)
                .Where(t => !unmanaged.Contains(t.Value))
                .Select(t => t.Key)
                .ToArray();
            if (typeIds.Length == 0)
            {
                continue;
            }

            // The platform-managed main-operation step of a Custom API is not a component Vela registers.
            QueryExpression steps = new("sdkmessageprocessingstep") { ColumnSet = new ColumnSet("name", "stage") };
            steps.Criteria.AddCondition("eventhandler", ConditionOperator.In, typeIds.Cast<object>().ToArray());
            foreach (Entity step in service.RetrieveAll(steps)
                .Where(s => (s.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0) != SdkConstants.PipelineStage.MainOperation))
            {
                candidates.Add(new SolutionMembershipAction("step", step.GetAttributeValue<string>("name") ?? step.Id.ToString(), SdkMessageProcessingStepComponentType, step.Id));
            }

            QueryExpression apis = new("customapi") { ColumnSet = new ColumnSet("uniquename") };
            apis.Criteria.AddCondition("plugintypeid", ConditionOperator.In, typeIds.Cast<object>().ToArray());
            List<Entity> apiRows = service.RetrieveAll(apis);
            foreach (Entity api in apiRows)
            {
                AddTable("customapi", "custom-api", api.GetAttributeValue<string>("uniquename") ?? api.Id.ToString(), api.Id);
            }

            if (apiRows.Count > 0)
            {
                object[] apiIds = apiRows.Select(a => (object)a.Id).ToArray();
                foreach ((string entity, string kind) in new[] { ("customapirequestparameter", "custom-api-parameter"), ("customapiresponseproperty", "custom-api-response-property") })
                {
                    QueryExpression children = new(entity) { ColumnSet = new ColumnSet("uniquename", "name") };
                    children.Criteria.AddCondition("customapiid", ConditionOperator.In, apiIds);
                    foreach (Entity child in service.RetrieveAll(children))
                    {
                        string name = child.GetAttributeValue<string>("uniquename") ?? child.GetAttributeValue<string>("name") ?? child.Id.ToString();
                        AddTable(entity, kind, name, child.Id);
                    }
                }
            }
        }

        foreach (string entity in unresolved.OrderBy(e => e, StringComparer.Ordinal))
        {
            info.Add($"Solution membership of '{entity}' components could not be checked: no solutioncomponentdefinition for it.");
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        HashSet<Guid> members = GetSolutionMemberIds(service, solutionUniqueName, candidates.Select(c => c.ComponentId).ToArray());
        return candidates.Where(c => !members.Contains(c.ComponentId)).ToArray();
    }

    /// <summary>Adds each component to the solution (without its required components).</summary>
    internal static void AddComponentsToSolution(IOrganizationService service, string solutionUniqueName, IReadOnlyList<SolutionMembershipAction> actions, bool verbose)
    {
        foreach (SolutionMembershipAction action in actions)
        {
            LogOperation(action.Kind, "add-to-solution", action.Name, verbose: verbose);
            service.Execute(new AddSolutionComponentRequest
            {
                ComponentType = action.ComponentType,
                ComponentId = action.ComponentId,
                SolutionUniqueName = solutionUniqueName,
                AddRequiredComponents = false,
            });
        }
    }

    private static Dictionary<string, int> ResolveTableComponentTypes(IOrganizationService service)
    {
        QueryExpression query = new("solutioncomponentdefinition") { ColumnSet = new ColumnSet("primaryentityname", "solutioncomponenttype") };
        query.Criteria.AddCondition("primaryentityname", ConditionOperator.In, TableComponentEntities.Cast<object>().ToArray());
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (Entity row in service.RetrieveAll(query))
        {
            string? entity = row.GetAttributeValue<string>("primaryentityname");
            object? raw = row.Contains("solutioncomponenttype") ? row["solutioncomponenttype"] : null;
            int? type = raw switch
            {
                int i => i,
                OptionSetValue o => o.Value,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(entity) && type.HasValue)
            {
                map[entity!] = type.Value;
            }
        }

        return map;
    }

    private static HashSet<Guid> GetSolutionMemberIds(IOrganizationService service, string solutionUniqueName, Guid[] objectIds)
    {
        QueryExpression query = new("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        query.Criteria.AddCondition("objectid", ConditionOperator.In, objectIds.Cast<object>().ToArray());
        LinkEntity solution = query.AddLink("solution", "solutionid", "solutionid");
        solution.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);
        return service.RetrieveAll(query)
            .Select(row => row.GetAttributeValue<Guid?>("objectid") ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }
}
