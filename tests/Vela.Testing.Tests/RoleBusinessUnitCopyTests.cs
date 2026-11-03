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

        // These tests check the stored rows, so they read as SYSTEM (ctx.SystemService): through the user's service
        // the role table needs the Read privilege on role, like any table.
        private static IOrganizationService System(InMemoryOrganizationService store) =>
            new PluginContextBuilder<Entity>().WithOrganizationService(store).Factory.CreateOrganizationService(null);

        private static EntityCollection Where(InMemoryOrganizationService svc, string table, string column, Guid value) =>
            System(svc).RetrieveMultiple(new QueryExpression(table)
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
            Entity copy = System(svc).Retrieve("role", held, new ColumnSet(true));
            Assert.Equal(ChildBu, copy.GetAttributeValue<EntityReference>("businessunitid").Id);
            Assert.Equal(SalespersonId, copy.GetAttributeValue<EntityReference>("parentrootroleid").Id);
        }

        [Fact]
        public void A_team_holds_the_copy_of_its_own_business_unit()
        {
            InMemoryOrganizationService svc = Build();

            Guid held = Assert.Single(Where(svc, "teamroles", "teamid", ChildTeam).Entities).GetAttributeValue<Guid>("roleid");

            Entity copy = System(svc).Retrieve("role", held, new ColumnSet(true));
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

            EntityCollection result = System(Build()).RetrieveMultiple(
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

            Assert.Single(System(Build()).RetrieveMultiple(query).Entities);
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

        // System Administrator's roletemplateid on a live org (verified 2026-11); its roleid differs per org.
        private static readonly Guid SystemAdministratorTemplate = new Guid("627090ff-40a3-4053-8790-584edc5be201");

        [Fact]
        public void A_system_role_carries_its_template_id_on_every_business_unit_copy()
        {
            Guid adminId = new Guid("50100000-0000-0000-0000-000000000a88");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithSecurity(s => s
                    .BusinessUnit(RootBu)
                    .BusinessUnit(ChildBu, RootBu)
                    .Role("System Administrator", adminId, SystemAdministratorTemplate, ("account", "Organization", "Read"))
                    .Role("Salesperson", SalespersonId, r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(ChildUser, ChildBu, "System Administrator"));

            Entity[] admin = Where(svc, "role", "parentrootroleid", adminId).Entities.ToArray();
            Assert.Equal(2, admin.Length); // root + child BU copies
            Assert.All(admin, c => Assert.Equal(SystemAdministratorTemplate, c.GetAttributeValue<EntityReference>("roletemplateid").Id));

            // A custom role has no template.
            Assert.All(Where(svc, "role", "parentrootroleid", SalespersonId).Entities, c => Assert.False(c.Contains("roletemplateid")));

            // The membership is found by template id from any business unit, as a plugin checks a system role.
            QueryExpression query = new QueryExpression("role") { ColumnSet = new ColumnSet(false) };
            query.Criteria.AddCondition("roletemplateid", ConditionOperator.Equal, SystemAdministratorTemplate);
            query.AddLink("systemuserroles", "roleid", "roleid").LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, ChildUser);
            Assert.Single(System(svc).RetrieveMultiple(query).Entities);
        }

        [Fact]
        public void A_template_id_is_declared_with_the_builder_overload_too_and_cleared_by_a_redeclaration()
        {
            Guid adminId = new Guid("50100000-0000-0000-0000-000000000a88");
            InMemoryOrganizationService withTemplate = new InMemoryOrganizationService()
                .WithSecurity(s => s.Role("System Administrator", adminId, SystemAdministratorTemplate, r => { }));
            InMemoryOrganizationService redeclared = new InMemoryOrganizationService()
                .WithSecurity(s => s
                    .Role("System Administrator", adminId, SystemAdministratorTemplate, r => { })
                    .Role("System Administrator", adminId, r => { }));

            Assert.Equal(
                SystemAdministratorTemplate,
                System(withTemplate).Retrieve("role", adminId, new ColumnSet(true)).GetAttributeValue<EntityReference>("roletemplateid").Id);
            Assert.False(System(redeclared).Retrieve("role", adminId, new ColumnSet(true)).Contains("roletemplateid"));
        }

        [Fact]
        public void An_empty_template_id_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => new SecurityConfiguration()
                .Role("System Administrator", Guid.NewGuid(), Guid.Empty, ("account", "Organization", "Read")));
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
