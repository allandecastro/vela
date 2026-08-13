using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Manager-hierarchy security: a manager reaches a report's records up the reporting chain — read/write on a
    // direct report, read-only deeper (within depth) — provided they already hold BU/Deep role access and are in the
    // report's business unit or its parent. The isolating case: the report owns a record in a CHILD business unit
    // that the manager's BusinessUnit-level role does NOT cover, so only the hierarchy grants access.
    public sealed class HierarchySecurityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000901"); // manager
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000902");   // direct report
        private static readonly Guid Carol = new Guid("ca401000-0000-0000-0000-000000000903"); // report of Bob
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000009a1");
        private static readonly Guid SubBu = new Guid("b0000000-0000-0000-0000-0000000009a2");
        private static readonly Guid OtherBu = new Guid("b0000000-0000-0000-0000-0000000009a3");

        private static Entity AccountOwnedBy(Guid id, Guid owner, Guid bu) => new Entity("account", id)
        {
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", bu),
        };

        // Alice manages Bob; Bob is in a child BU, so Alice's BusinessUnit-level role doesn't reach his records.
        private static SecurityConfiguration ManagerScenario(SecurityConfiguration s, int depth) => s
            .BusinessUnit(SalesBu)
            .BusinessUnit(SubBu, SalesBu)
            .Role("mgr", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read, Privilege.Write, Privilege.Delete))
            .User(Alice, SalesBu, "mgr")
            .User(Bob, SubBu)
            .Manager(Bob, Alice)
            .ManagerHierarchy(depth);

        [Fact]
        public void A_manager_reads_and_writes_a_direct_reports_record()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerScenario(s, depth: 1))
                .Seed(AccountOwnedBy(record, Bob, SubBu)); // in the child BU — outside Alice's BU role

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true))); // via hierarchy
            svc.Update(new Entity("account", record) { ["name"] = "edited by manager" }); // direct report: write allowed
        }

        [Fact]
        public void A_manager_reads_a_deeper_report_read_only_within_depth()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerScenario(s, depth: 2).User(Carol, SubBu).Manager(Carol, Bob))
                .Seed(AccountOwnedBy(record, Carol, SubBu)); // Carol is two levels below Alice

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true))); // read-only reaches depth 2
            Assert.Throws<FaultException<OrganizationServiceFault>>(                // ...but not write (deeper than direct)
                () => svc.Update(new Entity("account", record) { ["name"] = "no" }));
        }

        [Fact]
        public void Beyond_the_configured_depth_there_is_no_access()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerScenario(s, depth: 1).User(Carol, SubBu).Manager(Carol, Bob))
                .Seed(AccountOwnedBy(record, Carol, SubBu)); // Carol is level 2, depth is 1

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Hierarchy_requires_the_manager_to_hold_bu_or_deep_role_access()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    .Role("basic", r => r.Grant("account", Privilege.Read, AccessLevel.User)) // only User-level
                    .User(Alice, SalesBu, "basic")
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void A_manager_in_an_unrelated_business_unit_gets_no_hierarchy_access()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("mgr", r => r.Grant("account", Privilege.Read, AccessLevel.BusinessUnit))
                    .User(Alice, OtherBu, "mgr") // Alice is in an unrelated BU
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Hierarchy_precondition_needs_any_bu_or_deep_grant_not_all()
        {
            Guid record = Guid.NewGuid();
            Guid team = new Guid("7ea11000-0000-0000-0000-0000000009f1");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    // Alice holds account Read at User (own role) AND BusinessUnit (team role) — mixed levels.
                    .Role("ownUser", r => r.Grant("account", Privilege.Read, AccessLevel.User))
                    .Role("teamBu", r => r.Grant("account", Privilege.Read, AccessLevel.BusinessUnit))
                    .User(Alice, SalesBu, "ownUser")
                    .Team(team, SalesBu, "teamBu")
                    .Member(Alice, team)
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            // The precondition is satisfied by ANY BU/Deep grant, so the User-level grant must not veto it.
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Hierarchy_never_confers_delete()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerScenario(s, depth: 1)) // Alice has Delete @ BusinessUnit, but the record is in the child BU
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true))); // read via hierarchy
            Assert.Throws<FaultException<OrganizationServiceFault>>(              // delete is never conferred by hierarchy
                () => svc.Delete("account", record));
        }
    }
}
