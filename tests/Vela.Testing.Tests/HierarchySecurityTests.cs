using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Hierarchy security: a higher user reaches a report's records up the chain — read/write on a direct report,
    // read-only deeper (within depth) — provided they already hold at least User-level access for that privilege (per
    // MS: "at least the user level Read privilege to see the reports' data"). The manager model also requires the
    // manager to be in the report's business unit or its parent; the position model spans business units. The
    // isolating case: the report owns a record in a CHILD business unit that the manager's BU-level role does NOT
    // cover, so only the hierarchy grants access.
    public sealed class HierarchySecurityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000901"); // manager
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000902");   // direct report
        private static readonly Guid Carol = new Guid("ca401000-0000-0000-0000-000000000903"); // report of Bob
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000009a1");
        private static readonly Guid SubBu = new Guid("b0000000-0000-0000-0000-0000000009a2");
        private static readonly Guid OtherBu = new Guid("b0000000-0000-0000-0000-0000000009a3");
        private static readonly Guid CeoPos = new Guid("9051d000-0000-0000-0000-0000000009c1");   // higher position
        private static readonly Guid SalesPos = new Guid("9051d000-0000-0000-0000-0000000009c2");  // lower position

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
        public void Hierarchy_read_needs_only_user_level_read_on_the_manager()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read)) // only User-level Read
                    .User(Alice, SalesBu, "basic")
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            // Per MS hierarchy security, "at least the user level Read privilege" is enough to see reports' data —
            // the manager doesn't need a BU/Deep grant. (The record is in a child BU the User-level role can't reach.)
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Hierarchy_still_needs_at_least_a_user_level_read_grant()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    // Alice can read contacts, but has NO read grant on account at all — hierarchy can't extend nothing.
                    .Role("other", r => r.Grant("contact", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "other")
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Hierarchy_confers_direct_report_write_from_only_a_user_level_read_grant()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    .Role("readonly", r => r.Grant("account", AccessLevel.User, Privilege.Read)) // Read only, no Write
                    .User(Alice, SalesBu, "readonly")
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            // Per MS hierarchy security, User-level Read is the only gate: the model then confers the direct-report
            // write set (Write/Append/AppendTo), even though Alice holds no Write privilege of her own.
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
            svc.Update(new Entity("account", record) { ["name"] = "edited by manager" }); // direct report: write conferred
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
                    .Role("mgr", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read))
                    .User(Alice, OtherBu, "mgr") // Alice is in an unrelated BU
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Hierarchy_precondition_is_met_by_a_team_inherited_grant()
        {
            Guid record = Guid.NewGuid();
            Guid team = new Guid("7ea11000-0000-0000-0000-0000000009f1");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    // Alice holds account Read at User (own role) AND BusinessUnit (team role) — mixed levels.
                    .Role("ownUser", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .Role("teamBu", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read))
                    .User(Alice, SalesBu, "ownUser")
                    .Team(team, SalesBu, "teamBu")
                    .Member(Alice, team)
                    .User(Bob, SubBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            // The Read grant Alice inherits from her team satisfies the precondition just as a direct grant would.
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

        [Fact]
        public void A_manager_reaches_a_reports_team_owned_record()
        {
            Guid record = Guid.NewGuid();
            Guid crew = new Guid("7ea11000-0000-0000-0000-0000000009d1");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerScenario(s, depth: 1).Team(crew, SubBu).Member(Bob, crew))
                .Seed(new Entity("account", record)
                {
                    ["ownerid"] = new EntityReference("team", crew),           // owned by a team Bob belongs to
                    ["owningbusinessunit"] = new EntityReference("businessunit", SubBu),
                });

            // The record isn't owned by Bob directly, but by a team he's a member of; the manager still reaches it.
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void A_manager_reaches_a_record_shared_with_a_direct_report()
        {
            Guid record = Guid.NewGuid();
            Guid stranger = new Guid("57a11000-0000-0000-0000-0000000009e1");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("mgr", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "mgr")
                    .User(Bob, SubBu)
                    .User(stranger, OtherBu)
                    .Manager(Bob, Alice)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, stranger, OtherBu)) // owned outside the chain
                .WithSharedRecord(new EntityReference("account", record), new EntityReference("systemuser", Bob), AccessRights.ReadAccess);

            // The record is shared to Bob (read-only); his manager reaches it read-only through the share.
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
            Assert.Throws<FaultException<OrganizationServiceFault>>( // ...but the read-only share confers no write
                () => svc.Update(new Entity("account", record) { ["name"] = "no" }));
        }

        // ---- Position hierarchy: like the manager model but across business units, keyed on positions not reporting. ----

        [Fact]
        public void A_position_reaches_lower_positions_across_business_units()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, SalesBu)
                    .BusinessUnit(OtherBu) // Alice sits in a totally unrelated BU
                    .Role("rw", r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write))
                    .User(Alice, OtherBu, "rw")
                    .User(Bob, SubBu)
                    .Position(Alice, CeoPos)
                    .Position(Bob, SalesPos)
                    .PositionParent(SalesPos, CeoPos)
                    .PositionHierarchy(1))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            // The manager model would deny (unrelated BU); the position model spans business units.
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
            svc.Update(new Entity("account", record) { ["name"] = "by higher position" }); // direct position report: write
        }

        [Fact]
        public void A_higher_position_reads_a_deeper_position_read_only_within_depth()
        {
            Guid record = Guid.NewGuid();
            Guid midPos = new Guid("9051d000-0000-0000-0000-0000000009c3");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("r", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "r")
                    .User(Bob, SalesBu)
                    .Position(Alice, CeoPos)
                    .Position(Bob, SalesPos)
                    .PositionParent(SalesPos, midPos) // Bob's position is two levels below Alice's
                    .PositionParent(midPos, CeoPos)
                    .PositionHierarchy(2))
                .Seed(AccountOwnedBy(record, Bob, SalesBu));

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true))); // read-only reaches depth 2
            Assert.Throws<FaultException<OrganizationServiceFault>>(              // ...but no write two levels down
                () => svc.Update(new Entity("account", record) { ["name"] = "no" }));
        }

        [Fact]
        public void Peers_sharing_a_position_get_no_hierarchy_access()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("r", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "r")
                    .User(Bob, SalesBu)
                    .Position(Alice, SalesPos)
                    .Position(Bob, SalesPos) // same position — peers, neither is "higher"
                    .PositionHierarchy(3))
                .Seed(AccountOwnedBy(record, Bob, SalesBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Configuring_both_hierarchy_models_throws()
        {
            // Dataverse activates only one hierarchy model at a time; the harness fails fast rather than run a hybrid.
            Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .ManagerHierarchy(1)
                    .PositionHierarchy(2)));
        }
    }
}
