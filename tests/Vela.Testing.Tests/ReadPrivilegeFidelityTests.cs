using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Read behaviour verified live (2026-11-02):
    // 1. No Read privilege on a table, at any depth: the whole read fails with 0x80040220, whether the table is the
    //    root of the query or appears in any link-entity (inner or outer, with or without columns, nested), and for a
    //    Retrieve by id.
    // 2. Privilege held but the row out of reach: RetrieveMultiple drops the row; a Retrieve by id fails with 0x80048306.
    // 3. A link to a row the caller can't read: inner drops the root row; outer keeps it with no linked columns, and a
    //    null condition on the link alias matches it.
    // 4. A lookup on a visible row to a hidden row keeps its value.
    public sealed class ReadPrivilegeFidelityTests
    {
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);
        private const int RecordReadAccessDenied = unchecked((int)0x80048306);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000e01");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000e02");
        private static readonly Guid Bu = new Guid("b0000000-0000-0000-0000-000000000eb1");
        private static readonly Guid MyAccount = new Guid("acc00000-0000-0000-0000-000000000e01");
        private static readonly Guid TheirAccount = new Guid("acc00000-0000-0000-0000-000000000e02");
        private static readonly Guid MyContact = new Guid("c0000000-0000-0000-0000-000000000e01");
        private static readonly Guid TheirContact = new Guid("c0000000-0000-0000-0000-000000000e02");

        // Alice reads every contact and task; account Read is User-level (only her own) unless noAccountRead, in which
        // case she holds no account privilege at all.
        private static InMemoryOrganizationService World(bool noAccountRead = false)
        {
            return new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(Bu)
                    .Role("reader", r =>
                    {
                        r.Grant("contact", AccessLevel.Organization, Privilege.Read)
                            .Grant("task", AccessLevel.Organization, Privilege.Read);
                        if (!noAccountRead)
                        {
                            r.Grant("account", AccessLevel.User, Privilege.Read);
                        }
                    })
                    .User(Alice, Bu, "reader")
                    .User(Bob, Bu, "reader"))
                .Seed(
                    new Entity("account", MyAccount) { ["name"] = "Mine", ["ownerid"] = new EntityReference("systemuser", Alice) },
                    new Entity("account", TheirAccount) { ["name"] = "Theirs", ["ownerid"] = new EntityReference("systemuser", Bob) },
                    new Entity("contact", MyContact) { ["fullname"] = "HasMine", ["parentcustomerid"] = new EntityReference("account", MyAccount) },
                    new Entity("contact", TheirContact) { ["fullname"] = "HasTheirs", ["parentcustomerid"] = new EntityReference("account", TheirAccount) },
                    new Entity("task", Guid.NewGuid()) { ["subject"] = "T", ["regardingobjectid"] = new EntityReference("contact", MyContact) });
        }

        // ctx.SystemService for a plugin run against this store.
        private static IOrganizationService SystemOf(InMemoryOrganizationService store) =>
            new PluginContextBuilder<Entity>().WithOrganizationService(store).Factory.CreateOrganizationService(null);

        private static void AssertMissingRead(Action read, string table)
        {
            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(read);
            Assert.Equal(PrincipalPrivilegeDenied, fault.Detail.ErrorCode);
            Assert.Contains($"is missing prvRead{table} privilege", fault.Detail.Message);
        }

        private static QueryExpression ContactsLinkedToAccount(JoinOperator join, bool withColumns)
        {
            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", join);
            link.EntityAlias = "acct";
            if (withColumns)
            {
                link.Columns = new ColumnSet("name");
            }

            return query;
        }

        // ---- 1. No Read privilege on a table -------------------------------------------------------------------

        [Fact]
        public void QueryExpression_on_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            AssertMissingRead(() => crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) }), "account");
        }

        [Fact]
        public void FetchXml_on_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            AssertMissingRead(() => crm.RetrieveMultiple(new FetchExpression("<fetch><entity name='account'><attribute name='name' /></entity></fetch>")), "account");
        }

        [Fact]
        public void QueryByAttribute_on_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);
            QueryByAttribute query = new QueryByAttribute("account") { ColumnSet = new ColumnSet("name") };
            query.AddAttributeValue("name", "Mine");

            AssertMissingRead(() => crm.RetrieveMultiple(query), "account");
        }

        [Fact]
        public void Aggregate_on_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            AssertMissingRead(() => crm.RetrieveMultiple(new FetchExpression(
                "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='n' aggregate='count' /></entity></fetch>")), "account");
        }

        [Fact]
        public void Typed_query_on_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            AssertMissingRead(() => crm.RetrieveAll(Query.For<FakeAccount>().SelectAll()), "account");
        }

        [Theory]
        [InlineData(JoinOperator.Inner, false)]
        [InlineData(JoinOperator.Inner, true)]
        [InlineData(JoinOperator.LeftOuter, false)]
        [InlineData(JoinOperator.LeftOuter, true)]
        public void QueryExpression_linking_to_a_table_with_no_read_privilege_fails(JoinOperator join, bool withColumns)
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            AssertMissingRead(() => crm.RetrieveMultiple(ContactsLinkedToAccount(join, withColumns)), "account");
        }

        [Fact]
        public void A_link_filter_alone_still_needs_read_on_the_linked_table()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);
            QueryExpression query = ContactsLinkedToAccount(JoinOperator.LeftOuter, withColumns: false);
            query.LinkEntities[0].LinkCriteria.AddCondition("name", ConditionOperator.Equal, "Mine");

            AssertMissingRead(() => crm.RetrieveMultiple(query), "account");
        }

        [Theory]
        [InlineData("inner")]
        [InlineData("outer")]
        public void FetchXml_linking_to_a_table_with_no_read_privilege_fails(string linkType)
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);
            string fetch =
                "<fetch><entity name='contact'><attribute name='fullname' />" +
                $"<link-entity name='account' from='accountid' to='parentcustomerid' link-type='{linkType}' alias='acct' />" +
                "</entity></fetch>";

            AssertMissingRead(() => crm.RetrieveMultiple(new FetchExpression(fetch)), "account");
        }

        [Fact]
        public void A_nested_link_to_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);
            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            LinkEntity contactLink = query.AddLink("contact", "regardingobjectid", "contactid", JoinOperator.LeftOuter);
            contactLink.AddLink("account", "parentcustomerid", "accountid", JoinOperator.LeftOuter);

            AssertMissingRead(() => crm.RetrieveMultiple(query), "account");
        }

        [Fact]
        public void A_nested_fetch_link_to_a_table_with_no_read_privilege_fails()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);
            string fetch =
                "<fetch><entity name='task'><attribute name='subject' />" +
                "<link-entity name='contact' from='contactid' to='regardingobjectid' link-type='outer'>" +
                "<link-entity name='account' from='accountid' to='parentcustomerid' link-type='outer' />" +
                "</link-entity></entity></fetch>";

            AssertMissingRead(() => crm.RetrieveMultiple(new FetchExpression(fetch)), "account");
        }

        [Fact]
        public void Retrieve_by_id_on_a_table_with_no_read_privilege_fails_with_the_privilege_fault()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            AssertMissingRead(() => crm.Retrieve("account", MyAccount, new ColumnSet("name")), "account");
        }

        [Fact]
        public void SystemService_and_no_security_read_without_restriction()
        {
            InMemoryOrganizationService secured = World(noAccountRead: true);
            IOrganizationService system = SystemOf(secured);
            Assert.Equal(2, system.RetrieveMultiple(ContactsLinkedToAccount(JoinOperator.Inner, withColumns: true)).Entities.Count);
            Assert.Equal("Theirs", system.Retrieve("account", TheirAccount, new ColumnSet("name")).GetAttributeValue<string>("name"));

            InMemoryOrganizationService open = new InMemoryOrganizationService().Seed(
                new Entity("account", MyAccount) { ["name"] = "Mine" },
                new Entity("contact", MyContact) { ["parentcustomerid"] = new EntityReference("account", MyAccount) });
            Assert.Single(open.RetrieveMultiple(ContactsLinkedToAccount(JoinOperator.Inner, withColumns: true)).Entities);
        }

        // ---- 2. Privilege held, row out of reach ---------------------------------------------------------------

        [Fact]
        public void RetrieveMultiple_drops_a_row_out_of_reach()
        {
            InMemoryOrganizationService crm = World();

            Entity only = Assert.Single(crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet("name") }).Entities);
            Assert.Equal(MyAccount, only.Id);
        }

        [Fact]
        public void Retrieve_by_id_of_a_row_out_of_reach_fails_with_the_record_access_fault()
        {
            InMemoryOrganizationService crm = World();

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Retrieve("account", TheirAccount, new ColumnSet("name")));

            Assert.Equal(RecordReadAccessDenied, fault.Detail.ErrorCode);
            Assert.Contains("does not have ReadAccess right(s) for record", fault.Detail.Message);
        }

        // ---- 3. Links to a row out of reach --------------------------------------------------------------------

        [Fact]
        public void Inner_link_to_a_row_out_of_reach_drops_the_root_row()
        {
            InMemoryOrganizationService crm = World();

            Entity only = Assert.Single(crm.RetrieveMultiple(ContactsLinkedToAccount(JoinOperator.Inner, withColumns: true)).Entities);
            Assert.Equal(MyContact, only.Id);
        }

        [Fact]
        public void Outer_link_to_a_row_out_of_reach_keeps_the_root_row_without_linked_columns()
        {
            InMemoryOrganizationService crm = World();

            List<Entity> rows = crm.RetrieveMultiple(ContactsLinkedToAccount(JoinOperator.LeftOuter, withColumns: true)).Entities.ToList();

            Assert.Equal(2, rows.Count);
            Assert.False(rows.Single(e => e.Id == TheirContact).Contains("acct.name"));
            Assert.Equal("Mine", rows.Single(e => e.Id == MyContact).GetAttributeValue<AliasedValue>("acct.name").Value);
        }

        [Fact]
        public void Null_condition_on_an_outer_link_alias_matches_a_row_whose_linked_row_is_out_of_reach()
        {
            InMemoryOrganizationService crm = World();
            QueryExpression query = ContactsLinkedToAccount(JoinOperator.LeftOuter, withColumns: true);
            query.Criteria.AddCondition("acct", "accountid", ConditionOperator.Null);

            Entity only = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal(TheirContact, only.Id);
        }

        [Fact]
        public void FetchXml_null_condition_on_an_outer_link_alias_matches_a_row_whose_linked_row_is_out_of_reach()
        {
            InMemoryOrganizationService crm = World();
            string fetch =
                "<fetch><entity name='contact'><attribute name='fullname' />" +
                "<link-entity name='account' from='accountid' to='parentcustomerid' link-type='outer' alias='acct'><attribute name='name' /></link-entity>" +
                "<filter><condition entityname='acct' attribute='accountid' operator='null' /></filter>" +
                "</entity></fetch>";

            Entity only = Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);
            Assert.Equal(TheirContact, only.Id);
        }

        // ---- Security tables ---------------------------------------------------------------------------------

        private static readonly Guid SalesRole = new Guid("50100000-0000-0000-0000-000000000e77");

        private static InMemoryOrganizationService RoleWorld(AccessLevel? roleRead) => new InMemoryOrganizationService()
            .WithCurrentUser(Alice)
            .WithSecurity(s => s
                .BusinessUnit(Bu)
                .Role("Salesperson", SalesRole, r =>
                {
                    if (roleRead is AccessLevel level)
                    {
                        r.Grant("role", level, Privilege.Read);
                    }
                })
                .User(Alice, Bu, "Salesperson"));

        private static QueryExpression MyRoleNames()
        {
            QueryExpression query = new QueryExpression("systemuserroles") { ColumnSet = new ColumnSet(false) };
            query.Criteria.AddCondition("systemuserid", ConditionOperator.Equal, Alice);
            LinkEntity role = query.AddLink("role", "roleid", "roleid", JoinOperator.Inner);
            role.EntityAlias = "r";
            role.Columns = new ColumnSet("name");
            return query;
        }

        [Fact]
        public void The_role_table_needs_the_read_privilege_like_any_table()
        {
            InMemoryOrganizationService crm = RoleWorld(roleRead: null);

            AssertMissingRead(() => crm.RetrieveMultiple(new QueryExpression("role") { ColumnSet = new ColumnSet("name") }), "role");
            AssertMissingRead(() => crm.Retrieve("role", SalesRole, new ColumnSet("name")), "role");
        }

        [Fact]
        public void A_link_from_an_intersect_table_to_role_still_needs_the_role_read_privilege()
        {
            InMemoryOrganizationService crm = RoleWorld(roleRead: null);

            Assert.Single(crm.RetrieveMultiple(new QueryExpression("systemuserroles") { ColumnSet = new ColumnSet(true) }).Entities);
            AssertMissingRead(() => crm.RetrieveMultiple(MyRoleNames()), "role");
        }

        [Fact]
        public void A_business_unit_role_read_reaches_the_roles_of_the_users_business_unit()
        {
            InMemoryOrganizationService crm = RoleWorld(AccessLevel.BusinessUnit);

            Entity row = Assert.Single(crm.RetrieveMultiple(MyRoleNames()).Entities);
            Assert.Equal("Salesperson", row.GetAttributeValue<AliasedValue>("r.name").Value);
            Assert.Equal("Salesperson", crm.Retrieve("role", SalesRole, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void SystemService_reads_roles_without_any_privilege()
        {
            IOrganizationService system = SystemOf(RoleWorld(roleRead: null));

            Entity row = Assert.Single(system.RetrieveMultiple(MyRoleNames()).Entities);
            Assert.Equal("Salesperson", row.GetAttributeValue<AliasedValue>("r.name").Value);
        }

        [Fact]
        public void The_team_table_needs_the_read_privilege_like_any_table()
        {
            Guid team = Guid.NewGuid();
            InMemoryOrganizationService crm = RoleWorld(AccessLevel.Organization)
                .Seed(new Entity("team", team) { ["name"] = "Sales" });

            AssertMissingRead(() => crm.RetrieveMultiple(new QueryExpression("team") { ColumnSet = new ColumnSet("name") }), "team");
        }

        // ---- 4. Lookups to hidden rows -------------------------------------------------------------------------

        [Fact]
        public void A_lookup_to_a_row_out_of_reach_keeps_its_value()
        {
            InMemoryOrganizationService crm = World();

            Entity viaQuery = crm.RetrieveMultiple(new QueryExpression("contact") { ColumnSet = new ColumnSet("parentcustomerid") })
                .Entities.Single(e => e.Id == TheirContact);
            Entity viaRetrieve = crm.Retrieve("contact", TheirContact, new ColumnSet("parentcustomerid"));

            Assert.Equal(TheirAccount, viaQuery.GetAttributeValue<EntityReference>("parentcustomerid").Id);
            Assert.Equal(TheirAccount, viaRetrieve.GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        [Fact]
        public void A_lookup_to_a_table_with_no_read_privilege_keeps_its_value()
        {
            InMemoryOrganizationService crm = World(noAccountRead: true);

            Entity contact = crm.Retrieve("contact", TheirContact, new ColumnSet("parentcustomerid"));

            Assert.Equal(TheirAccount, contact.GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }
    }
}
