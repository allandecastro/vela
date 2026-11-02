using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // The dependency-light generated Roles class feeds grants as (entity, access level, privilege) name triples.
    // These cover the Role(name, id, params (string, string, string)[]) harness overload that rehydrates them into
    // a RoleBuilder, in parity with the hand-written Action<RoleBuilder> form.
    public sealed class GeneratedRoleGrantsTests
    {
        // 0x80040220 PrincipalPrivilegeDenied — the fault code the platform raises on a denied verb.
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-0000000009a1");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000009b1");
        private static readonly Guid SalesRoleId = new Guid("50100000-0000-0000-0000-000000000999");

        // Mirrors the shape of a generated Roles.X field.
        private static readonly (string Entity, string Level, string Privilege)[] SalespersonGrants =
            new[] { ("account", "User", "Create"), ("account", "User", "Read") };

        [Fact]
        public void Grant_data_enforces_privileges_like_a_hand_written_role()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", SalesRoleId, SalespersonGrants)
                    .User(Alice, SalesBu, "Salesperson"));

            Guid id = svc.Create(new Entity("account"));                         // Create granted
            Assert.NotNull(svc.Retrieve("account", id, new ColumnSet(true)));    // Read granted (own record)
        }

        [Fact]
        public void A_verb_not_in_the_grant_data_is_denied()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", SalesRoleId, ("account", "Organization", "Read"))
                    .User(Alice, SalesBu, "reader"));

            FaultException<OrganizationServiceFault> ex =
                Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Create(new Entity("account")));
            Assert.Equal(PrincipalPrivilegeDenied, ex.Detail.ErrorCode);
        }

        [Fact]
        public void The_explicit_id_flows_into_systemuserroles()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("Salesperson", SalesRoleId, SalespersonGrants)
                    .User(Alice, SalesBu, "Salesperson"));

            Entity row = Assert.Single(svc.RetrieveMultiple(new QueryExpression("systemuserroles")
            {
                ColumnSet = new ColumnSet(true),
                Criteria = { Conditions = { new ConditionExpression("systemuserid", ConditionOperator.Equal, Alice) } },
            }).Entities);
            Assert.Equal(SalesRoleId, row.GetAttributeValue<Guid>("roleid")); // the explicit (generated) role id
        }

        [Fact]
        public void Empty_grant_data_defines_a_role_with_no_privileges()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("empty", SalesRoleId) // no grants
                    .User(Alice, SalesBu, "empty"))
                .Seed(new Entity("account", account)
                {
                    ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()),
                    ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
                });

            FaultException<OrganizationServiceFault> denied = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) }));
            Assert.Equal(unchecked((int)0x80040220), denied.Detail.ErrorCode); // no Read privilege on account
        }

        [Fact]
        public void An_unknown_access_level_throws()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s =>
                    s.Role("bad", SalesRoleId, ("account", "Universe", "Read"))));
            Assert.Equal("grants", ex.ParamName);
        }

        [Fact]
        public void An_unknown_privilege_throws()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s =>
                    s.Role("bad", SalesRoleId, ("account", "User", "Frobnicate"))));
            Assert.Equal("grants", ex.ParamName);
        }

        [Theory]
        [InlineData("2", "Read")]     // a numeric level string must not sneak through as ParentChild
        [InlineData("User", "3")]     // a numeric privilege string must not sneak through as Delete
        [InlineData("user", "Read")]  // wrong case is not a member name
        public void A_numeric_or_miscased_grant_value_throws(string level, string privilege)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s =>
                    s.Role("bad", SalesRoleId, ("account", level, privilege))));
            Assert.Equal("grants", ex.ParamName);
        }

        [Fact]
        public void A_blank_role_name_throws()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s =>
                    s.Role("  ", SalesRoleId, ("account", "User", "Read"))));
            Assert.Equal("name", ex.ParamName);
        }
    }
}
