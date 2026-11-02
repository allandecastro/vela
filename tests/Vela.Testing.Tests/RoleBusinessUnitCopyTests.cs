using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // The shape of the classes `vela models --entities role --intersect` generates for role and its systemuserroles
    // intersect (trimmed to the columns used here), so membership can be checked with the typed query API.
    [EntityLogicalName("role")]
    public sealed class BuRole : Entity
    {
        public BuRole() : base("role") { }

        [AttributeLogicalName("roleid")]
        public Guid? RoleId => GetAttributeValue<Guid?>("roleid");

        [AttributeLogicalName("parentrootroleid")]
        public EntityReference ParentRootRoleId => GetAttributeValue<EntityReference>("parentrootroleid");
    }

    [EntityLogicalName("systemuserroles")]
    public sealed class BuSystemUserRoles : Entity
    {
        public BuSystemUserRoles() : base("systemuserroles") { }

        [AttributeLogicalName("roleid")]
        public Guid? RoleId => GetAttributeValue<Guid?>("roleid");

        [AttributeLogicalName("systemuserid")]
        public Guid? SystemUserId => GetAttributeValue<Guid?>("systemuserid");
    }

    // Dataverse keeps one copy of a role per business unit (verified live, 2026-11): the root BU's copy has
    // roleid == parentrootroleid, every other copy points at it through parentrootroleid, and systemuserroles /
    // teamroles hold the copy of the principal's BU. A generated Roles.XId is the root id.
    public sealed class RoleBusinessUnitCopyTests
    {
        private static readonly Guid RootBu = new Guid("b0000000-0000-0000-0000-000000000a01");
        private static readonly Guid ChildBu = new Guid("b0000000-0000-0000-0000-000000000a02");
        private static readonly Guid GrandchildBu = new Guid("b0000000-0000-0000-0000-000000000a03");
        private static readonly Guid RootUser = new Guid("a0000000-0000-0000-0000-000000000a11");
        private static readonly Guid ChildUser = new Guid("a0000000-0000-0000-0000-000000000a12");
        private static readonly Guid TeamMember = new Guid("a0000000-0000-0000-0000-000000000a13");
        private static readonly Guid ChildTeam = new Guid("7ea00000-0000-0000-0000-000000000a21");
        private static readonly Guid SalespersonId = new Guid("50100000-0000-0000-0000-000000000a77");

        private static InMemoryOrganizationService Build() => new InMemoryOrganizationService()
            .WithSecurity(s => s
                .BusinessUnit(RootBu)
                .BusinessUnit(ChildBu, RootBu)
                .BusinessUnit(GrandchildBu, ChildBu)
                .Role("Salesperson", SalespersonId, r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .Role("Nothing", r => { })
                .User(RootUser, RootBu, "Salesperson")
                .User(ChildUser, ChildBu, "Salesperson")
                .User(TeamMember, GrandchildBu, "Nothing")
                .Team(ChildTeam, GrandchildBu, "Salesperson")
                .Member(TeamMember, ChildTeam));

        private static EntityCollection Where(InMemoryOrganizationService svc, string table, string column, Guid value) =>
            svc.RetrieveMultiple(new QueryExpression(table)
            {
                ColumnSet = new ColumnSet(true),
                Criteria = { Conditions = { new ConditionExpression(column, ConditionOperator.Equal, value) } },
            });

        private static Guid UserRoleId(InMemoryOrganizationService svc, Guid user) =>
            Assert.Single(Where(svc, "systemuserroles", "systemuserid", user).Entities).GetAttributeValue<Guid>("roleid");

        [Fact]
        public void Each_business_unit_has_a_copy_of_the_role_pointing_at_the_root()
        {
            InMemoryOrganizationService svc = Build();

            Entity[] copies = Where(svc, "role", "parentrootroleid", SalespersonId).Entities.ToArray();

            Assert.Equal(3, copies.Length);
            Assert.All(copies, c => Assert.Equal("Salesperson", c.GetAttributeValue<string>("name")));
            Assert.Equal(
                new[] { ChildBu, GrandchildBu, RootBu }.OrderBy(id => id),
                copies.Select(c => c.GetAttributeValue<EntityReference>("businessunitid").Id).OrderBy(id => id));

            Entity root = Assert.Single(copies, c => c.GetAttributeValue<EntityReference>("businessunitid").Id == RootBu);
            Assert.Equal(SalespersonId, root.Id);
            Assert.Equal(SalespersonId, root.GetAttributeValue<Guid>("roleid"));
            Assert.All(copies.Where(c => c.Id != SalespersonId), c => Assert.Equal(c.Id, c.GetAttributeValue<Guid>("roleid")));
            Assert.Equal(2, copies.Count(c => c.Id != SalespersonId));
        }

        [Fact]
        public void A_root_business_unit_user_holds_the_root_id()
        {
            Assert.Equal(SalespersonId, UserRoleId(Build(), RootUser));
        }

        [Fact]
        public void A_child_business_unit_user_holds_that_business_units_copy()
        {
            InMemoryOrganizationService svc = Build();

            Guid held = UserRoleId(svc, ChildUser);

            Assert.NotEqual(SalespersonId, held);
            Entity copy = svc.Retrieve("role", held, new ColumnSet(true));
            Assert.Equal(ChildBu, copy.GetAttributeValue<EntityReference>("businessunitid").Id);
            Assert.Equal(SalespersonId, copy.GetAttributeValue<EntityReference>("parentrootroleid").Id);
        }

        [Fact]
        public void A_team_holds_the_copy_of_its_own_business_unit()
        {
            InMemoryOrganizationService svc = Build();

            Guid held = Assert.Single(Where(svc, "teamroles", "teamid", ChildTeam).Entities).GetAttributeValue<Guid>("roleid");

            Entity copy = svc.Retrieve("role", held, new ColumnSet(true));
            Assert.Equal(GrandchildBu, copy.GetAttributeValue<EntityReference>("businessunitid").Id);
            Assert.Equal(SalespersonId, copy.GetAttributeValue<EntityReference>("parentrootroleid").Id);
        }

        [Fact]
        public void Copy_ids_are_stable_across_runs()
        {
            Assert.Equal(UserRoleId(Build(), ChildUser), UserRoleId(Build(), ChildUser));
        }

        [Theory]
        [InlineData("a0000000-0000-0000-0000-000000000a11")] // root BU
        [InlineData("a0000000-0000-0000-0000-000000000a12")] // child BU
        public void A_typed_join_on_parentrootroleid_finds_direct_membership_in_any_business_unit(string user)
        {
            Guid userId = new Guid(user);

            EntityCollection result = Build().RetrieveMultiple(
                Query.For<BuRole>()
                    .Where(r => r.ParentRootRoleId, ConditionOperator.Equal, SalespersonId)
                    .Link<BuSystemUserRoles>(r => r.RoleId, ur => ur.RoleId,
                        configure: l => l.Where(ur => ur.SystemUserId, ConditionOperator.Equal, userId))
                    .Top(1));

            Assert.Single(result.Entities);
        }

        [Fact]
        public void A_join_on_parentrootroleid_finds_membership_through_a_team()
        {
            QueryExpression query = new QueryExpression("role")
            {
                ColumnSet = new ColumnSet(false),
                Criteria = { Conditions = { new ConditionExpression("parentrootroleid", ConditionOperator.Equal, SalespersonId) } },
            };
            LinkEntity teamRoles = query.AddLink("teamroles", "roleid", "roleid");
            LinkEntity membership = teamRoles.AddLink("teammembership", "teamid", "teamid");
            membership.LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, TeamMember);

            Assert.Single(Build().RetrieveMultiple(query).Entities);
        }

        [Fact]
        public void Matching_systemuserroles_on_the_root_id_misses_a_child_business_unit_user()
        {
            QueryExpression query = new QueryExpression("systemuserroles") { ColumnSet = new ColumnSet(false) };
            query.Criteria.AddCondition("systemuserid", ConditionOperator.Equal, ChildUser);
            query.Criteria.AddCondition("roleid", ConditionOperator.Equal, SalespersonId);

            // As on Dataverse: the row holds the child BU's copy, not the root id.
            Assert.Empty(Build().RetrieveMultiple(query).Entities);
        }

        [Fact]
        public void Privileges_are_evaluated_from_the_declared_role_in_every_business_unit()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(ChildUser)
                .WithSecurity(s => s
                    .BusinessUnit(RootBu)
                    .BusinessUnit(ChildBu, RootBu)
                    .Role("Salesperson", SalespersonId, r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(ChildUser, ChildBu, "Salesperson"))
                .Seed(new Entity("account", account)
                {
                    ["ownerid"] = new EntityReference("systemuser", RootUser),
                    ["owningbusinessunit"] = new EntityReference("businessunit", RootBu),
                });

            Assert.Equal(account, svc.Retrieve("account", account, new ColumnSet(false)).Id);
        }

        [Fact]
        public void Without_a_single_root_business_unit_every_principal_holds_the_root_id()
        {
            // No hierarchy declared and principals in two unrelated BUs: the root can't be identified, so the harness
            // keeps one role row (the root) and seeds the root id for everyone.
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithSecurity(s => s
                    .Role("Salesperson", SalespersonId, r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(RootUser, RootBu, "Salesperson")
                    .User(ChildUser, ChildBu, "Salesperson"));

            Assert.Equal(SalespersonId, UserRoleId(svc, RootUser));
            Assert.Equal(SalespersonId, UserRoleId(svc, ChildUser));
            Entity role = Assert.Single(Where(svc, "role", "parentrootroleid", SalespersonId).Entities);
            Assert.Equal(SalespersonId, role.Id);
        }
    }
}
