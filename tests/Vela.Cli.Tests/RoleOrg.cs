using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Cli.Tests;

// The tables Dataverse returns for the read side: solution, solutioncomponent, role and roleprivileges rows, and
// the table metadata whose Privileges collections map each privilege id to its tables.
internal sealed class RoleOrg : IOrganizationService
{
    public static readonly Guid PrvReadAccount = Guid.NewGuid();
    public static readonly Guid PrvReadContact = Guid.NewGuid();
    public static readonly Guid PrvWriteContact = Guid.NewGuid();
    public static readonly Guid PrvExportToExcel = Guid.NewGuid();

    private readonly List<Entity> _rows = [];

    public Guid AddSolution(string uniqueName)
    {
        Entity solution = new("solution", Guid.NewGuid()) { ["uniquename"] = uniqueName };
        _rows.Add(solution);
        return solution.Id;
    }

    public Guid AddRole(string name, params (Guid Privilege, int Depth)[] privileges)
    {
        Entity role = new("role", Guid.NewGuid()) { ["name"] = name };
        role["parentrootroleid"] = new EntityReference("role", role.Id);
        _rows.Add(role);
        foreach ((Guid privilege, int depth) in privileges)
        {
            _rows.Add(new Entity("roleprivileges", Guid.NewGuid())
            {
                ["roleid"] = role.Id,
                ["privilegeid"] = privilege,
                ["privilegedepthmask"] = depth,
            });
        }

        return role.Id;
    }

    public void SetTemplate(Guid roleId, Guid templateId) =>
        _rows.Single(r => r.LogicalName == "role" && r.Id == roleId)["roletemplateid"] = new EntityReference("roletemplate", templateId);

    public void AddComponent(Guid solution, Guid objectId, int componentType) =>
        _rows.Add(new Entity("solutioncomponent", Guid.NewGuid())
        {
            ["solutionid"] = solution,
            ["objectid"] = objectId,
            ["componenttype"] = componentType,
        });

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        QueryExpression expression = (QueryExpression)query;
        IEnumerable<Entity> rows = _rows.Where(r => r.LogicalName == expression.EntityName);
        foreach (ConditionExpression condition in expression.Criteria.Conditions)
        {
            object expected = condition.Values[0];
            rows = rows.Where(r => Equals(Comparable(r.GetAttributeValue<object>(condition.AttributeName)), expected));
        }

        return new EntityCollection(rows.ToList());
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
        _rows.Single(r => r.LogicalName == entityName && r.Id == id);

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        Assert.IsType<RetrieveAllEntitiesRequest>(request);
        EntityMetadata[] metadata =
        [
            Table("account", (PrvReadAccount, PrivilegeType.Read)),
            Table("customeraddress", (PrvReadAccount, PrivilegeType.Read)),
            Table("contact", (PrvReadContact, PrivilegeType.Read), (PrvWriteContact, PrivilegeType.Write)),
        ];
        return new RetrieveAllEntitiesResponse { Results = { ["EntityMetadata"] = metadata } };
    }

    private static object? Comparable(object? value) => value is EntityReference reference ? reference.Id : value;

    private static EntityMetadata Table(string logicalName, params (Guid Id, PrivilegeType Type)[] privileges)
    {
        EntityMetadata table = new() { LogicalName = logicalName };
        SecurityPrivilegeMetadata[] items = privileges.Select(p =>
        {
            // The SDK seals these metadata types (no public constructor or setters), as Dataverse fills them.
            SecurityPrivilegeMetadata privilege = (SecurityPrivilegeMetadata)Activator.CreateInstance(typeof(SecurityPrivilegeMetadata), nonPublic: true)!;
            Set(privilege, nameof(SecurityPrivilegeMetadata.PrivilegeId), p.Id);
            Set(privilege, nameof(SecurityPrivilegeMetadata.PrivilegeType), p.Type);
            Set(privilege, nameof(SecurityPrivilegeMetadata.Name), $"prv{p.Type}{logicalName}");
            return privilege;
        }).ToArray();
        Set(table, nameof(EntityMetadata.Privileges), items);
        return table;
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    public Guid Create(Entity entity) => throw new NotSupportedException();

    public void Update(Entity entity) => throw new NotSupportedException();

    public void Delete(string entityName, Guid id) => throw new NotSupportedException();

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        throw new NotSupportedException();

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        throw new NotSupportedException();
}
