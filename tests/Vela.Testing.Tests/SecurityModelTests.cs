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
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
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

        [Fact]
        public void Read_filtering_happens_before_paging()
        {
            Entity Named(Guid owner, string name) => new Entity("account", Guid.NewGuid())
            {
                ["ownerid"] = new EntityReference("systemuser", owner),
                ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
                ["name"] = name,
            };

            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                // Bob's records sort first by name; a naive page-then-filter would return an empty first page.
                .Seed(Named(Bob, "a1"), Named(Bob, "a2"), Named(Alice, "b1"), Named(Alice, "b2"));

            EntityCollection page = svc.RetrieveMultiple(new QueryExpression("account")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression("name", OrderType.Ascending) },
                PageInfo = new PagingInfo { Count = 2, PageNumber = 1 },
            });

            // The page is drawn from Alice's readable records, not filtered after paging over Bob's.
            Assert.Equal(2, page.Entities.Count);
            Assert.All(page.Entities, e => Assert.Equal(Alice, e.GetAttributeValue<EntityReference>("ownerid").Id));
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
                    .Role("bu-reader", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read))
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
                    .Role("deep-reader", r => r.Grant("account", AccessLevel.ParentChild, Privilege.Read))
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
                    .Role("org-reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
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
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Read))
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
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
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
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read))
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
                            r.Grant("account", AccessLevel.Organization, Privilege.Read).Grant("account", AccessLevel.User, Privilege.Read);
                        }
                        else
                        {
                            r.Grant("account", AccessLevel.User, Privilege.Read).Grant("account", AccessLevel.Organization, Privilege.Read);
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

        // --- Aggregates are security-trimmed (count only readable records) -------------------------------

        [Fact]
        public void Aggregates_count_only_readable_records()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(Guid.NewGuid(), Bob, OtherBu))    // not readable by Alice
                .Seed(AccountOwnedBy(Guid.NewGuid(), Carol, OtherBu))  // not readable by Alice
                .Seed(AccountOwnedBy(Guid.NewGuid(), Alice, SalesBu)); // readable (owned by Alice)

            // The count reflects only the record Alice may read, like the platform.
            EntityCollection result = svc.RetrieveMultiple(new FetchExpression(
                "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='n' aggregate='count'/></entity></fetch>"));

            Assert.Equal(1, Assert.Single(result.Entities).GetAttributeValue<AliasedValue>("n").Value);
        }

        // --- Business-unit stamping feeds depth checks --------------------------------------------------

        [Fact]
        public void Created_records_are_stamped_with_the_acting_users_business_unit()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Create))
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
                    .Role("deep-reader", r => r.Grant("account", AccessLevel.ParentChild, Privilege.Read))
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
                    .Role("deep-reader", r => r.Grant("account", AccessLevel.ParentChild, Privilege.Read))
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
                    .Role("bu-reader", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read))
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
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
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
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                .Seed(new Entity("account", record) { ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu) });

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // --- Aggregates via QueryExpression are also security-trimmed -----------------------------------

        [Fact]
        public void Aggregate_query_expressions_count_only_readable_records()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(Guid.NewGuid(), Bob, OtherBu))    // not readable
                .Seed(AccountOwnedBy(Guid.NewGuid(), Alice, SalesBu))  // readable
                .Seed(AccountOwnedBy(Guid.NewGuid(), Alice, SalesBu)); // readable

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
                    .Role("author", r => r.Grant("Account", AccessLevel.User, Privilege.Create)) // capitalized grant
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
                    .Role("deep-reader", r => r.Grant("account", AccessLevel.ParentChild, Privilege.Read))
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
                () => new SecurityConfiguration().Role("r", r => r.Grant("", AccessLevel.User, Privilege.Read)));
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

        [Fact]
        public void RetrievePrincipalAccess_for_a_team_reflects_the_teams_own_roles()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu)
                    .Team(SalesTeam, SalesBu, "reader")) // the team holds account Read at Organization
                .Seed(AccountOwnedBy(record, Bob, SalesBu));

            RetrievePrincipalAccessResponse access = (RetrievePrincipalAccessResponse)svc.Execute(new RetrievePrincipalAccessRequest
            {
                Target = new EntityReference("account", record),
                Principal = new EntityReference("team", SalesTeam), // a TEAM principal
            });

            Assert.True(access.AccessRights.HasFlag(AccessRights.ReadAccess)); // resolved from the team's own role
        }

        // --- Team privilege-inheritance mode (Member's privilege inheritance) ---------------------------

        private static Entity AccountOwnedByTeam(Guid id, Guid team) => new Entity("account", id)
        {
            ["ownerid"] = new EntityReference("team", team),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        [Fact]
        public void User_with_no_team_membership_is_denied_a_team_owned_record()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "reader")) // Alice belongs to no team
                .Seed(AccountOwnedByTeam(record, SalesTeam)); // owned by a team Alice is not in

            // User-level ownership check must short-circuit cleanly (no membership) and deny — not blow up.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Default_team_inheritance_lets_a_member_use_a_basic_team_grant_on_their_own_record()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu)                       // Alice holds no role of her own
                    .Team(SalesTeam, SalesBu, "reader")         // default inheritance (Team and user privileges)
                    .Member(Alice, SalesTeam))
                .Seed(AccountOwnedBy(record, Alice, SalesBu));  // owned by the member

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Team_only_inheritance_scopes_a_basic_team_grant_to_team_owned_records()
        {
            Guid ownRecord = Guid.NewGuid();
            Guid teamRecord = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu)
                    .Team(SalesTeam, SalesBu, TeamPrivilegeInheritance.TeamOnly, "reader")
                    .Member(Alice, SalesTeam))
                .Seed(AccountOwnedBy(ownRecord, Alice, SalesBu), AccountOwnedByTeam(teamRecord, SalesTeam));

            Assert.NotNull(svc.Retrieve("account", teamRecord, new ColumnSet(true))); // team-owned: allowed
            Assert.Throws<FaultException<OrganizationServiceFault>>(                    // member-owned: denied
                () => svc.Retrieve("account", ownRecord, new ColumnSet(true)));
        }

        [Fact]
        public void Team_only_inheritance_does_not_restrict_a_higher_level_team_grant()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("bureader", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read))
                    .User(Alice, SalesBu)
                    .Team(SalesTeam, SalesBu, TeamPrivilegeInheritance.TeamOnly, "bureader")
                    .Member(Alice, SalesTeam))
                .Seed(AccountOwnedBy(record, Bob, SalesBu)); // another user's record in the BU

            // A BusinessUnit-level grant is scope-based, so the Team-only mode doesn't restrict it.
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void A_members_own_role_still_covers_their_records_alongside_a_team_only_team()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "reader")             // Alice's OWN User-level Read
                    .Team(SalesTeam, SalesBu, TeamPrivilegeInheritance.TeamOnly, "reader")
                    .Member(Alice, SalesTeam))
                .Seed(AccountOwnedBy(record, Alice, SalesBu));

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // --- Assign requires the Assign privilege, not Write ---------------------------------------------

        [Fact]
        public void Assign_requires_the_assign_privilege_not_write()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "editor")) // has Write, not Assign
                .Seed(AccountOwnedBy(record, Bob, SalesBu));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = new EntityReference("systemuser", Alice),
            }));
        }

        [Fact]
        public void Assign_succeeds_with_the_assign_privilege()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("assigner", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Assign))
                    .User(Alice, SalesBu, "assigner"))
                .Seed(AccountOwnedBy(record, Bob, SalesBu));

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Assert.Equal(Alice, Assert.Single(svc.GetAll("account")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Updating_ownerid_needs_assign_while_updating_fields_needs_write()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "editor")) // Write but not Assign
                .Seed(AccountOwnedBy(record, Bob, SalesBu));

            svc.Update(new Entity("account", record) { ["name"] = "ok" }); // field change: Write covers it

            // A plain Update that changes ownerid is an assignment, so it needs Assign — not just Write.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Update(new Entity("account", record) { ["ownerid"] = new EntityReference("systemuser", Alice) }));
        }

        [Fact]
        public void Creating_a_record_owned_by_another_principal_requires_assign()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("author", r => r.Grant("account", AccessLevel.Organization, Privilege.Create)) // depth covers any owner
                    .User(Alice, SalesBu, "author")   // ...but no Assign
                    .User(Bob, OtherBu));

            svc.Create(new Entity("account")); // owned by the caller: fine

            // Create depth is satisfied, so this isolates the missing Assign privilege.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", Bob) }));
        }

        [Fact]
        public void Create_depth_restricts_the_new_records_owner_scope()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    // User-level Create only covers records Alice will own; Assign is present so this isolates depth.
                    .Role("author", r => r
                        .Grant("account", AccessLevel.User, Privilege.Create)
                        .Grant("account", AccessLevel.Organization, Privilege.Assign))
                    .User(Alice, SalesBu, "author")
                    .User(Bob, OtherBu));

            svc.Create(new Entity("account")); // Alice's own: User-level Create covers it

            // Bob is in another BU; a User-level Create does not reach a record that will be owned by him.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", Bob) }));
        }

        [Fact]
        public void Create_with_organization_depth_can_create_for_another_owner()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Assign))
                    .User(Alice, SalesBu, "admin")
                    .User(Bob, OtherBu));

            Guid id = svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", Bob) });

            Assert.NotEqual(Guid.Empty, id);
        }

        [Fact]
        public void Creating_on_behalf_stamps_the_owners_business_unit_not_the_callers()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)   // Alice's BU
                    .BusinessUnit(OtherBu)   // Bob's BU
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Assign))
                    .User(Alice, SalesBu, "admin")
                    .User(Bob, OtherBu));

            svc.Create(new Entity("account", record) { ["ownerid"] = new EntityReference("systemuser", Bob) });

            Entity stored = Assert.Single(svc.GetAll("account"));
            Assert.Equal(Bob, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            // owningbusinessunit is Bob's BU (the owner), not Alice's (the acting user).
            Assert.Equal(OtherBu, stored.GetAttributeValue<EntityReference>("owningbusinessunit").Id);
        }

        [Fact]
        public void Assigning_across_business_units_moves_the_owning_business_unit()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(OtherBu)
                    .BusinessUnit(SalesBu)
                    .Role("mover", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Assign)
                        .Grant("account", AccessLevel.BusinessUnit, Privilege.Read))
                    .User(Alice, SalesBu, "mover")) // Alice lives in SalesBu
                .Seed(AccountOwnedBy(record, Bob, OtherBu)); // record starts in OtherBu

            // Alice's BusinessUnit-level read does not reach a record owned in another BU.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = new EntityReference("systemuser", Alice),
            });

            // owningbusinessunit followed the new owner to SalesBu, so the depth check now covers the record.
            Assert.Equal(SalesBu, Assert.Single(svc.GetAll("account")).GetAttributeValue<EntityReference>("owningbusinessunit").Id);
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // --- Associate / Disassociate need Append (related) + AppendTo (primary) -------------------------

        private static InMemoryOrganizationService WithAccountContactM2M(Guid accountId, Guid contactId, Action<SecurityConfiguration> security) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .WithSecurity(security)
                .Seed(
                    AccountOwnedBy(accountId, Alice, SalesBu),
                    new Entity("contact", contactId)
                    {
                        ["ownerid"] = new EntityReference("systemuser", Alice),
                        ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
                    });

        [Fact]
        public void Associate_is_denied_without_append_and_appendto()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            InMemoryOrganizationService svc = WithAccountContactM2M(accountId, contactId, s => s
                .BusinessUnit(SalesBu)
                .Role("reader", r => r
                    .Grant("account", AccessLevel.Organization, Privilege.Read)
                    .Grant("contact", AccessLevel.Organization, Privilege.Read))
                .User(Alice, SalesBu, "reader"));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Associate(
                "account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) }));
        }

        [Fact]
        public void Associate_succeeds_with_append_and_appendto()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            InMemoryOrganizationService svc = WithAccountContactM2M(accountId, contactId, s => s
                .BusinessUnit(SalesBu)
                .Role("linker", r => r
                    .Grant("account", AccessLevel.Organization, Privilege.AppendTo)
                    .Grant("contact", AccessLevel.Organization, Privilege.Append))
                .User(Alice, SalesBu, "linker"));

            svc.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Assert.Single(svc.GetAll("account_contact"));
        }

        [Fact]
        public void Disassociate_is_denied_without_append_and_appendto()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            InMemoryOrganizationService svc = WithAccountContactM2M(accountId, contactId, s => s
                .BusinessUnit(SalesBu)
                .Role("reader", r => r
                    .Grant("account", AccessLevel.Organization, Privilege.Read)
                    .Grant("contact", AccessLevel.Organization, Privilege.Read))
                .User(Alice, SalesBu, "reader"));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Disassociate(
                "account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) }));
        }

        // --- G1: setting a lookup needs Append (on the record) + AppendTo (on the target) ----------------

        private static Entity ContactOwnedBy(Guid id, Guid owner) => new Entity("contact", id)
        {
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        [Fact]
        public void Setting_a_lookup_requires_append_and_appendto()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write)
                        .Grant("contact", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "editor")) // can write the account but has no Append/AppendTo
                .Seed(AccountOwnedBy(account, Alice, SalesBu), ContactOwnedBy(contact, Alice));

            // Pointing account.primarycontactid at the contact relates the two records.
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Update(
                new Entity("account", account) { ["primarycontactid"] = new EntityReference("contact", contact) }));
        }

        [Fact]
        public void Setting_a_lookup_succeeds_with_append_and_appendto()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write, Privilege.Append)
                        .Grant("contact", AccessLevel.Organization, Privilege.Read, Privilege.AppendTo))
                    .User(Alice, SalesBu, "editor"))
                .Seed(AccountOwnedBy(account, Alice, SalesBu), ContactOwnedBy(contact, Alice));

            svc.Update(new Entity("account", account) { ["primarycontactid"] = new EntityReference("contact", contact) });

            Assert.Equal(contact, svc.Retrieve("account", account, new ColumnSet(true))
                .GetAttributeValue<EntityReference>("primarycontactid").Id);
        }
    }
}
