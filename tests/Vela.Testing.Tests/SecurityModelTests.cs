using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Opt-in security model: role/privilege enforcement with User/BusinessUnit/ParentChild/Organization depth,
    // over users and teams in a BU hierarchy. Enforcement throws PrincipalPrivilegeDenied on write verbs and
    // filters RetrieveMultiple on read. Default-off is covered first (no behaviour change without WithSecurity).
    public sealed class SecurityModelTests
    {
        // 0x80040220 PrincipalPrivilegeDenied — the fault code the platform (and Vela's RetryPolicy) uses.
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000001");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000002");
        private static readonly Guid Carol = new Guid("ca401000-0000-0000-0000-000000000003");
        private static readonly Guid SalesTeam = new Guid("7ea11000-0000-0000-0000-000000000004");

        private static readonly Guid RootBu = new Guid("b0000000-0000-0000-0000-0000000000b0");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000000b1");
        private static readonly Guid SubSalesBu = new Guid("b0000000-0000-0000-0000-0000000000b2");
        private static readonly Guid OtherBu = new Guid("b0000000-0000-0000-0000-0000000000b9");

        private static Entity AccountOwnedBy(Guid recordId, Guid ownerUser, Guid businessUnit) =>
            new Entity("account", recordId)
            {
                ["ownerid"] = new EntityReference("systemuser", ownerUser),
                ["owningbusinessunit"] = new EntityReference("businessunit", businessUnit),
            };

        // --- Default-off: WithSecurity not called => full access, exactly as before -----------------------

        [Fact]
        public void Without_security_all_operations_are_allowed()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().WithCurrentUser(Alice);

            Guid id = svc.Create(new Entity("account"));
            Assert.NotEqual(Guid.Empty, id);
            Assert.NotNull(svc.Retrieve("account", id, new ColumnSet(true)));
            svc.Update(new Entity("account", id) { ["name"] = "ok" });
            Assert.Single(svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) }).Entities);
            svc.Delete("account", id);
            Assert.Empty(svc.RetrieveMultiple(new QueryExpression("account")).Entities);
        }

        // --- Create -------------------------------------------------------------------------------------

        [Fact]
        public void Create_is_denied_without_the_create_privilege()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization))
                    .User(Alice, SalesBu, "reader"));

            FaultException<OrganizationServiceFault> ex =
                Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Create(new Entity("account")));
            Assert.Equal(PrincipalPrivilegeDenied, ex.Detail.ErrorCode);
        }

        [Fact]
        public void Create_is_allowed_with_the_create_privilege()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "author"));

            Guid id = svc.Create(new Entity("account"));

            Assert.NotEqual(Guid.Empty, id);
            // The created record is stamped as owned by Alice in her BU, so her User-level Read covers it.
            Assert.NotNull(svc.Retrieve("account", id, new ColumnSet(true)));
        }

        // --- Read depth: User ---------------------------------------------------------------------------

        [Fact]
        public void User_level_read_covers_own_records_but_not_another_users()
        {
            Guid bobRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(bobRecord, Bob, SalesBu));

            Guid aliceRecord = svc.Create(new Entity("account"));

            Assert.NotNull(svc.Retrieve("account", aliceRecord, new ColumnSet(true)));
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", bobRecord, new ColumnSet(true)));

            // RetrieveMultiple silently omits Bob's record rather than faulting.
            EntityCollection visible = svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) });
            Assert.Equal(aliceRecord, Assert.Single(visible.Entities).Id);
        }

        // --- Read depth: BusinessUnit -------------------------------------------------------------------

        [Fact]
        public void Business_unit_level_read_covers_same_bu_only()
        {
            Guid sameBu = Guid.NewGuid();
            Guid otherBu = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("bu-reader", r => r.Grant("account", Privilege.Read, AccessLevel.BusinessUnit))
                    .User(Alice, SalesBu, "bu-reader"))
                .Seed(AccountOwnedBy(sameBu, Bob, SalesBu))
                .Seed(AccountOwnedBy(otherBu, Carol, OtherBu));

            Assert.NotNull(svc.Retrieve("account", sameBu, new ColumnSet(true)));
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", otherBu, new ColumnSet(true)));
        }

        // --- Read depth: ParentChild (deep) -------------------------------------------------------------

        [Fact]
        public void Parent_child_level_read_covers_descendant_bus_but_not_ancestors()
        {
            Guid childRecord = Guid.NewGuid();
            Guid parentRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(RootBu)
                    .BusinessUnit(SalesBu, parent: RootBu)
                    .BusinessUnit(SubSalesBu, parent: SalesBu)
                    .Role("deep-reader", r => r.Grant("account", Privilege.Read, AccessLevel.ParentChild))
                    .User(Alice, SalesBu, "deep-reader"))
                .Seed(AccountOwnedBy(childRecord, Bob, SubSalesBu))
                .Seed(AccountOwnedBy(parentRecord, Carol, RootBu));

            Assert.NotNull(svc.Retrieve("account", childRecord, new ColumnSet(true)));
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", parentRecord, new ColumnSet(true)));
        }

        // --- Teams: role inheritance + team-owned records -----------------------------------------------

        [Fact]
        public void A_user_inherits_privileges_from_their_teams()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("org-reader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization))
                    .User(Alice, SalesBu)                 // Alice holds no role of her own
                    .Team(SalesTeam, SalesBu, "org-reader")
                    .Member(Alice, SalesTeam))            // ...but inherits the team's role
                .Seed(AccountOwnedBy(record, Bob, OtherBu));

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void User_level_read_covers_records_owned_by_a_team_the_user_belongs_to()
        {
            Guid teamRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", Privilege.Read, AccessLevel.User))
                    .User(Alice, SalesBu, "author")
                    .Team(SalesTeam, SalesBu)
                    .Member(Alice, SalesTeam))
                .Seed(new Entity("account", teamRecord)
                {
                    ["ownerid"] = new EntityReference("team", SalesTeam),
                    ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
                });

            Assert.NotNull(svc.Retrieve("account", teamRecord, new ColumnSet(true)));
        }

        // --- Write / Delete -----------------------------------------------------------------------------

        [Fact]
        public void Write_and_delete_require_their_own_privileges()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization))
                    .User(Alice, SalesBu, "reader"))
                .Seed(AccountOwnedBy(record, Alice, SalesBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Update(new Entity("account", record) { ["name"] = "x" }));
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Delete("account", record));
        }

        [Fact]
        public void Write_and_delete_succeed_once_granted()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write, Privilege.Delete))
                    .User(Alice, SalesBu, "editor"))
                .Seed(AccountOwnedBy(record, Bob, OtherBu));

            svc.Update(new Entity("account", record) { ["name"] = "x" });
            Assert.Equal("x", svc.Retrieve("account", record, new ColumnSet(true)).GetAttributeValue<string>("name"));
            svc.Delete("account", record);
            Assert.Empty(svc.RetrieveMultiple(new QueryExpression("account")).Entities);
        }

        // --- WhoAmI -------------------------------------------------------------------------------------

        [Fact]
        public void WhoAmI_returns_the_current_user_and_its_business_unit()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithOrganizationId(new Guid("06000000-0000-0000-0000-0000000000aa"))
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", Privilege.Read, AccessLevel.User))
                    .User(Alice, SalesBu, "reader"));

            WhoAmIResponse response = (WhoAmIResponse)svc.Execute(new WhoAmIRequest());

            Assert.Equal(Alice, response.UserId);
            Assert.Equal(SalesBu, response.BusinessUnitId);           // resolved from Alice's registration
            Assert.Equal(new Guid("06000000-0000-0000-0000-0000000000aa"), response.OrganizationId);
        }

        // --- Configuration validation -------------------------------------------------------------------

        [Fact]
        public void Referencing_an_undefined_role_fails_fast()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .User(Alice, SalesBu, "does-not-exist")));

            Assert.Contains("does-not-exist", ex.Message);
        }

        [Fact]
        public void A_team_referencing_an_undefined_role_fails_fast()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Team(SalesTeam, SalesBu, "ghost-role")));

            Assert.Contains("ghost-role", ex.Message);
        }

        // --- Level resolution: the higher grant wins when a privilege is granted twice --------------------

        [Theory]
        [InlineData(false)] // User then Organization
        [InlineData(true)]  // Organization then User (order must not matter)
        public void Granting_a_privilege_twice_keeps_the_higher_level(bool organizationFirst)
        {
            Guid foreignRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("dual", r =>
                    {
                        if (organizationFirst)
                        {
                            r.Grant("account", Privilege.Read, AccessLevel.Organization).Grant("account", Privilege.Read, AccessLevel.User);
                        }
                        else
                        {
                            r.Grant("account", Privilege.Read, AccessLevel.User).Grant("account", Privilege.Read, AccessLevel.Organization);
                        }
                    })
                    .User(Alice, SalesBu, "dual"))
                .Seed(AccountOwnedBy(foreignRecord, Bob, OtherBu));

            // Organization is the higher level; it must win regardless of grant order, so the foreign record reads.
            Assert.NotNull(svc.Retrieve("account", foreignRecord, new ColumnSet(true)));
        }

        // --- Read filtering is independent of the selected columns ---------------------------------------

        [Fact]
        public void Read_filtering_uses_stored_ownership_even_when_owner_columns_are_not_selected()
        {
            Guid bobRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(bobRecord, Bob, SalesBu));
            Guid aliceRecord = svc.Create(new Entity("account") { ["name"] = "mine" });

            // Query selects only 'name' — no ownerid/owningbusinessunit — yet ownership is resolved from the store.
            EntityCollection visible = svc.RetrieveMultiple(
                new QueryExpression("account") { ColumnSet = new ColumnSet("name") });

            Assert.Equal(aliceRecord, Assert.Single(visible.Entities).Id);
        }

        // --- Aggregates are not filtered (computed over the whole store) ---------------------------------

        [Fact]
        public void Aggregate_results_are_not_filtered_by_read_access()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", Privilege.Read, AccessLevel.User))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(Guid.NewGuid(), Bob, OtherBu))
                .Seed(AccountOwnedBy(Guid.NewGuid(), Carol, OtherBu));

            // None of the records are readable at User level, but a count aggregate still sees all of them.
            EntityCollection result = svc.RetrieveMultiple(new FetchExpression(
                "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='n' aggregate='count'/></entity></fetch>"));

            Assert.Equal(2, Assert.Single(result.Entities).GetAttributeValue<AliasedValue>("n").Value);
        }

        // --- Business-unit stamping feeds depth checks --------------------------------------------------

        [Fact]
        public void Created_records_are_stamped_with_the_acting_users_business_unit()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", Privilege.Create, AccessLevel.User))
                    .User(Alice, SalesBu, "author"));

            svc.Create(new Entity("account"));

            Entity stored = Assert.Single(svc.GetAll("account"));
            Assert.Equal(Alice, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(SalesBu, stored.GetAttributeValue<EntityReference>("owningbusinessunit").Id);
        }

        // --- ParentChild edges --------------------------------------------------------------------------

        [Fact]
        public void Parent_child_level_covers_the_users_own_business_unit()
        {
            Guid sameBuRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(RootBu)
                    .BusinessUnit(SalesBu, parent: RootBu)
                    .Role("deep-reader", r => r.Grant("account", Privilege.Read, AccessLevel.ParentChild))
                    .User(Alice, SalesBu, "deep-reader"))
                .Seed(AccountOwnedBy(sameBuRecord, Bob, SalesBu));

            Assert.NotNull(svc.Retrieve("account", sameBuRecord, new ColumnSet(true)));
        }

        [Fact]
        public void Parent_child_level_denies_records_in_an_undeclared_business_unit()
        {
            Guid strayRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(RootBu)
                    .BusinessUnit(SalesBu, parent: RootBu)
                    .Role("deep-reader", r => r.Grant("account", Privilege.Read, AccessLevel.ParentChild))
                    .User(Alice, SalesBu, "deep-reader"))
                .Seed(AccountOwnedBy(strayRecord, Bob, OtherBu)); // OtherBu is never declared

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", strayRecord, new ColumnSet(true)));
        }

        // --- BusinessUnit-level guard: an empty BU never matches an empty record BU ----------------------

        [Fact]
        public void Business_unit_level_does_not_match_when_both_business_units_are_empty()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .Role("bu-reader", r => r.Grant("account", Privilege.Read, AccessLevel.BusinessUnit))
                    .User(Alice, Guid.Empty, "bu-reader"))    // user with no business unit
                .Seed(new Entity("account", record) { ["ownerid"] = new EntityReference("systemuser", Bob) }); // no owningbusinessunit

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // --- Write honours privilege depth, not just presence -------------------------------------------

        [Fact]
        public void Write_honours_privilege_depth()
        {
            Guid bobRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Create, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(bobRecord, Bob, SalesBu));
            Guid aliceRecord = svc.Create(new Entity("account"));

            svc.Update(new Entity("account", aliceRecord) { ["name"] = "ok" });     // own record: allowed
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Update(new Entity("account", bobRecord) { ["name"] = "no" })); // Bob's record: denied
        }

        // --- Fault carries a message that names the privilege and table ---------------------------------

        [Fact]
        public void The_privilege_fault_names_the_privilege_and_table()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization))
                    .User(Alice, SalesBu, "reader"));

            FaultException<OrganizationServiceFault> ex =
                Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Create(new Entity("account")));

            Assert.Contains("Create", ex.Detail.Message);
            Assert.Contains("account", ex.Detail.Message);
        }

        // --- User-level owner resolution: a record with no owner is never user-owned --------------------

        [Fact]
        public void User_level_read_denies_a_record_that_has_no_owner()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", Privilege.Read, AccessLevel.User))
                    .User(Alice, SalesBu, "author"))
                .Seed(new Entity("account", record) { ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu) });

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // --- Aggregates via QueryExpression are also left unfiltered -------------------------------------

        [Fact]
        public void Aggregate_query_expressions_are_not_filtered_by_read_access()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", Privilege.Read, AccessLevel.User))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(Guid.NewGuid(), Bob, OtherBu))
                .Seed(AccountOwnedBy(Guid.NewGuid(), Carol, OtherBu));

            QueryExpression query = new QueryExpression("account");
            query.ColumnSet.AddColumn("accountid");
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression("accountid", XrmAggregateType.Count, "n"));

            EntityCollection result = svc.RetrieveMultiple(query);

            Assert.Equal(2, Assert.Single(result.Entities).GetAttributeValue<AliasedValue>("n").Value);
        }

        // --- Entity names match case-insensitively, like the rest of the harness -------------------------

        [Fact]
        public void Grants_match_the_table_name_case_insensitively()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("Account", Privilege.Create, AccessLevel.User)) // capitalized grant
                    .User(Alice, SalesBu, "author"));

            Guid id = svc.Create(new Entity("account")); // lower-case query still resolves the grant

            Assert.NotEqual(Guid.Empty, id);
        }

        // --- ParentChild with a record that has no owning business unit ----------------------------------

        [Fact]
        public void Parent_child_level_denies_a_record_with_no_owning_business_unit()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("deep-reader", r => r.Grant("account", Privilege.Read, AccessLevel.ParentChild))
                    .User(Alice, SalesBu, "deep-reader"))
                .Seed(new Entity("account", record) { ["ownerid"] = new EntityReference("systemuser", Bob) });

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // --- Configuration guards -----------------------------------------------------------------------

        [Fact]
        public void Configuration_guards_reject_invalid_input()
        {
            Assert.Throws<ArgumentNullException>(() => new InMemoryOrganizationService().WithSecurity(null!));
            Assert.Throws<ArgumentException>(() => new SecurityConfiguration().BusinessUnit(Guid.Empty));
            Assert.Throws<ArgumentException>(() => new SecurityConfiguration().Role("", _ => { }));
            Assert.Throws<ArgumentNullException>(() => new SecurityConfiguration().Role("r", null!));
            // The entity guard on Grant surfaces through the Role(...) configuration callback.
            Assert.Throws<ArgumentException>(
                () => new SecurityConfiguration().Role("r", r => r.Grant("", Privilege.Read, AccessLevel.User)));
        }

        [Fact]
        public void Membership_of_an_undeclared_team_fails_fast()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .User(Alice, SalesBu)
                    .Member(Alice, SalesTeam)));   // SalesTeam is never declared with Team(...)

            Assert.Contains(SalesTeam.ToString(), ex.Message);
        }
    }
}
