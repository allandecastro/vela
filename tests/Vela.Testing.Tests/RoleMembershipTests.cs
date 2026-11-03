using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Role membership is materialized into the platform's tables (role / systemuserroles / teamroles /
    // teammembership) so a plugin can resolve a user's roles the way it does against Dataverse. role is read under the
    // same rules as any table (prvReadRole); the intersect tables have no read check.
    public sealed class RoleMembershipTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000701");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000702");
        private static readonly Guid SalesTeam = new Guid("7ea11000-0000-0000-0000-000000000703");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000007b1");
        private static readonly Guid SalesRoleId = new Guid("50100000-0000-0000-0000-000000000777");

        private static EntityCollection Query(InMemoryOrganizationService svc, string table, string column, Guid value) =>
            svc.RetrieveMultiple(new QueryExpression(table)
            {
                ColumnSet = new ColumnSet(true),
                Criteria = { Conditions = { new ConditionExpression(column, ConditionOperator.Equal, value) } },
            });

        [Fact]
        public void A_users_direct_roles_are_queryable_via_systemuserroles()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", SalesRoleId, r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "Salesperson"));

            Entity row = Assert.Single(Query(svc, "systemuserroles", "systemuserid", Alice).Entities);
            Assert.Equal(SalesRoleId, row.GetAttributeValue<Guid>("roleid")); // the explicit (generated) role id
        }

        [Fact]
        public void The_role_table_exposes_the_id_and_name()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", SalesRoleId, r => r.Grant("role", AccessLevel.BusinessUnit, Privilege.Read))
                    .User(Alice, SalesBu, "Salesperson"));

            Entity role = Assert.Single(Query(svc, "role", "roleid", SalesRoleId).Entities);
            Assert.Equal("Salesperson", role.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Intersect_tables_are_readable_even_without_any_privilege()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("empty", r => { }) // Alice holds a role but no privileges at all
                    .User(Alice, SalesBu, "empty"))
                .Seed(new Entity("account", account) { ["ownerid"] = new EntityReference("systemuser", Bob), ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu) });

            // Alice can't read the account (no privilege), but can still read her role membership.
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) }));
            Assert.Single(Query(svc, "systemuserroles", "systemuserid", Alice).Entities);
        }

        [Fact]
        public void Team_roles_and_team_membership_are_materialized()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", SalesRoleId, r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu)
                    .Team(SalesTeam, SalesBu, "Salesperson")
                    .Member(Alice, SalesTeam));

            Assert.Equal(SalesRoleId, Assert.Single(Query(svc, "teamroles", "teamid", SalesTeam).Entities).GetAttributeValue<Guid>("roleid"));
            Assert.Equal(SalesTeam, Assert.Single(Query(svc, "teammembership", "systemuserid", Alice).Entities).GetAttributeValue<Guid>("teamid"));
        }

        [Fact]
        public void A_plugin_can_resolve_a_users_role_names_by_joining_systemuserroles_to_role()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", SalesRoleId, r => r.Grant("role", AccessLevel.BusinessUnit, Privilege.Read))
                    .User(Alice, SalesBu, "Salesperson"));

            QueryExpression query = new QueryExpression("systemuserroles")
            {
                ColumnSet = new ColumnSet(false),
                Criteria = { Conditions = { new ConditionExpression("systemuserid", ConditionOperator.Equal, Alice) } },
                LinkEntities =
                {
                    new LinkEntity("systemuserroles", "role", "roleid", "roleid", JoinOperator.Inner)
                    {
                        Columns = new ColumnSet("name"),
                        EntityAlias = "r",
                    },
                },
            };

            Entity row = Assert.Single(svc.RetrieveMultiple(query).Entities);
            Assert.Equal("Salesperson", row.GetAttributeValue<AliasedValue>("r.name").Value);
        }

        [Fact]
        public void Materialized_ids_are_deterministic_across_runs()
        {
            InMemoryOrganizationService Build() => new InMemoryOrganizationService()
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "Salesperson"));

            Guid first = Assert.Single(Query(Build(), "systemuserroles", "systemuserid", Alice).Entities).Id;
            Guid second = Assert.Single(Query(Build(), "systemuserroles", "systemuserid", Alice).Entities).Id;
            Assert.Equal(first, second); // reproducible fixtures
        }
    }
}
