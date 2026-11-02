using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// <see cref="RoleReader"/> against an in-memory org: the roles of a solution (solutioncomponent componenttype 20),
/// merged with explicit names, each with its real root id and its privileges mapped to every table they cover.
/// </summary>
public sealed class RoleReaderTests
{
    private static readonly Guid PrvReadAccount = Guid.NewGuid();
    private static readonly Guid PrvReadContact = Guid.NewGuid();
    private static readonly Guid PrvExportToExcel = Guid.NewGuid();

    [Fact]
    public void Solution_roles_are_read_with_their_root_id_and_privileges()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid sales = org.AddRole("Sales Rep", (PrvReadAccount, 8), (PrvReadContact, 1));
        Guid other = org.AddRole("Not In Solution", (PrvReadContact, 8));
        org.AddComponent(solution, sales, componentType: 20);
        org.AddComponent(solution, Guid.NewGuid(), componentType: 1); // a table component, not a role

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, [], "ZavaCore");

        RoleModel role = Assert.Single(roles);
        Assert.Equal("Sales Rep", role.Name);
        Assert.Equal(sales, role.Id);
        Assert.DoesNotContain(roles, r => r.Id == other);
        // prvReadAccount covers account and customeraddress; a depth mask of 8 is Organization, 1 is User.
        Assert.Equal(
            new[] { ("account", "Organization", "Read"), ("contact", "User", "Read"), ("customeraddress", "Organization", "Read") },
            role.Grants.Select(g => (g.Entity, g.AccessLevel, g.Privilege)).OrderBy(g => g.Entity, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_role_named_explicitly_and_present_in_the_solution_is_generated_once()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid sales = org.AddRole("Sales Rep", (PrvReadContact, 1));
        org.AddComponent(solution, sales, componentType: 20);

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, ["Sales Rep"], "ZavaCore");

        Assert.Equal(sales, Assert.Single(roles).Id);
    }

    [Fact]
    public void Explicit_roles_and_solution_roles_are_merged_and_sorted_by_name()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid zed = org.AddRole("Zed", (PrvReadContact, 1));
        org.AddRole("Alpha", (PrvReadAccount, 1));
        org.AddComponent(solution, zed, componentType: 20);

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, ["Alpha"], "ZavaCore");

        Assert.Equal(new[] { "Alpha", "Zed" }, roles.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_privilege_no_table_carries_is_left_out()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid sales = org.AddRole("Sales Rep", (PrvExportToExcel, 8), (PrvReadContact, 1));
        org.AddComponent(solution, sales, componentType: 20);

        RoleModel role = Assert.Single(RoleReader.ReadRoles(org, [], "ZavaCore"));

        Assert.Equal("contact", Assert.Single(role.Grants).Entity);
    }

    [Fact]
    public void A_system_roles_template_id_is_read_and_a_custom_role_has_none()
    {
        // System Administrator's roletemplateid on a live org (verified 2026-11); its roleid differs per org.
        Guid template = new("627090ff-40a3-4053-8790-584edc5be201");
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid admin = org.AddRole("System Administrator", (PrvReadAccount, 8));
        org.SetTemplate(admin, template);
        Guid sales = org.AddRole("Sales Rep", (PrvReadContact, 1));
        org.AddComponent(solution, sales, componentType: 20);

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, ["System Administrator"], "ZavaCore");

        Assert.Equal(template, roles.Single(r => r.Id == admin).TemplateId);
        Assert.Null(roles.Single(r => r.Id == sales).TemplateId);
    }

    [Fact]
    public void An_unknown_solution_fails_with_its_name()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => RoleReader.ReadRoles(new RoleOrg(), [], "Missing"));

        Assert.Contains("'Missing'", error.Message);
    }

    [Fact]
    public void A_solution_without_roles_and_no_explicit_role_is_an_error()
    {
        RoleOrg org = new();
        org.AddSolution("Empty");

        Assert.Throws<InvalidOperationException>(() => RoleReader.ReadRoles(org, [], "Empty"));
    }

    // The tables Dataverse returns for the read side: solution, solutioncomponent, role and roleprivileges rows, and
    // the table metadata whose Privileges collections map each privilege id to its tables.
    private sealed class RoleOrg : IOrganizationService
    {
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
                Table("contact", (PrvReadContact, PrivilegeType.Read)),
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
}
