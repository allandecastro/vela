using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Reads security roles (their root role id and granted table privileges) from the live org for the
/// <c>vela models --roles</c> / <c>--solution-roles</c> scope. Split out of <see cref="ModelsCommand"/> so the
/// role-reading responsibility stands on its own.
/// </summary>
internal static class RoleReader
{
    internal static IReadOnlyList<RoleModel> ReadRoles(IOrganizationService service, string[] roleNames, string? solution)
    {
        // Resolve to unique root roles (id -> name), merging explicit names and the solution's role components so a
        // role listed both ways is generated once.
        Dictionary<Guid, string> roots = new Dictionary<Guid, string>();

        List<string> missing = new List<string>();
        foreach (string name in roleNames)
        {
            Entity? root = ReadRootRole(service, name);
            if (root == null)
            {
                missing.Add(name);
                continue;
            }

            roots[root.Id] = root.GetAttributeValue<string>("name") ?? name;
        }

        // Generate whatever resolved rather than aborting the whole run on the first missing role; a typo in one
        // name should not throw away the others. Only a completely empty result (no explicit role resolved and no
        // solution roles) is an error.
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"Warning: security role(s) not found and skipped: {string.Join(", ", missing)}.");
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            foreach (Entity role in RolesInSolution(service, solution!))
            {
                roots[role.Id] = role.GetAttributeValue<string>("name") ?? role.Id.ToString();
            }
        }

        if (roots.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the requested security role(s) were found in the connected environment: {string.Join(", ", roleNames)}.");
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<(string Entity, PrivilegeType Type)>> privileges = BuildPrivilegeMap(service);
        return roots
            .OrderBy(r => r.Value, StringComparer.Ordinal)
            .Select(r => new RoleModel(r.Value, r.Key, RolePrivilegeMapper.ToGrants(ReadRolePrivileges(service, r.Key), privileges)))
            .ToList();
    }

    // Every security role in a solution (solutioncomponent componenttype 20). Solution components reference the
    // root role, so the object ids are the stable root ids.
    private static IEnumerable<Entity> RolesInSolution(IOrganizationService service, string uniqueName)
    {
        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity solutionRecord = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionRecord.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 20); // 20 = Role
        List<Guid> roleIds = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .Distinct()
            .ToList();

        foreach (Guid roleId in roleIds)
        {
            yield return service.Retrieve("role", roleId, new ColumnSet("roleid", "name", "parentrootroleid"));
        }
    }

    // The root (template) role for a name. Roles are duplicated per business unit; the root is the one whose
    // parentrootroleid points at itself, and its id is the stable id plugin logic uses.
    internal static Entity? ReadRootRole(IOrganizationService service, string name)
    {
        QueryExpression query = new QueryExpression("role") { ColumnSet = new ColumnSet("roleid", "name", "parentrootroleid") };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        List<Entity> matches = service.RetrieveAll(query).ToList();

        // Null (not found) instead of throwing: the caller collects and reports the missing names but still
        // generates the roles that did resolve.
        return matches.FirstOrDefault(r => r.GetAttributeValue<EntityReference>("parentrootroleid")?.Id == r.Id)
            ?? matches.FirstOrDefault();
    }

    private static IEnumerable<(Guid PrivilegeId, int DepthMask)> ReadRolePrivileges(IOrganizationService service, Guid roleId)
    {
        QueryExpression query = new QueryExpression("roleprivileges") { ColumnSet = new ColumnSet("privilegeid", "privilegedepthmask") };
        query.Criteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
        return service.RetrieveAll(query)
            .Select(e => (e.GetAttributeValue<Guid>("privilegeid"), e.GetAttributeValue<int>("privilegedepthmask")))
            .ToList();
    }

    // privilegeId -> every (table, privilege type) it appears on, from every table's Privileges metadata. A privilege
    // can be shared by several tables (the activity tables share prvReadActivity, customeraddress shares
    // prvReadAccount), so each table is kept. Miscellaneous (non-table) privileges aren't in any table's collection,
    // so they're naturally excluded from generation.
    private static IReadOnlyDictionary<Guid, IReadOnlyList<(string Entity, PrivilegeType Type)>> BuildPrivilegeMap(IOrganizationService service)
    {
        RetrieveAllEntitiesResponse response = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity | EntityFilters.Privileges, RetrieveAsIfPublished = true });

        Dictionary<Guid, List<(string Entity, PrivilegeType Type)>> map = new Dictionary<Guid, List<(string Entity, PrivilegeType Type)>>();
        foreach (EntityMetadata entity in response.EntityMetadata)
        {
            if (entity.Privileges == null || string.IsNullOrWhiteSpace(entity.LogicalName))
            {
                continue;
            }

            foreach (SecurityPrivilegeMetadata privilege in entity.Privileges)
            {
                if (!map.TryGetValue(privilege.PrivilegeId, out List<(string Entity, PrivilegeType Type)>? tables))
                {
                    tables = new List<(string Entity, PrivilegeType Type)>();
                    map[privilege.PrivilegeId] = tables;
                }

                tables.Add((entity.LogicalName, privilege.PrivilegeType));
            }
        }

        return map.ToDictionary(x => x.Key, x => (IReadOnlyList<(string Entity, PrivilegeType Type)>)x.Value);
    }
}
