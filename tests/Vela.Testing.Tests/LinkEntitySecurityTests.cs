using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Link-entity read-trimming: a joined record the caller can't read is not a match. An INNER join then drops the
    // primary row; a LEFT-OUTER join keeps it but without the linked columns. The trim applies at every level of a
    // nested join. Off by default (no security configured => joins are untrimmed, as the other query tests rely on).
    public sealed class LinkEntitySecurityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000f01");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000f02");
        private static readonly Guid Bu = new Guid("b0000000-0000-0000-0000-000000000fb1");

        // Alice reads all contacts, but only the accounts she OWNS (User-level Read). Bob owns the "other" account,
        // so it is unreadable to Alice — the row-level case that isolates the join trim.
        private static SecurityConfiguration Scenario(SecurityConfiguration s) => s
            .BusinessUnit(Bu)
            .Role("reader", r => r
                .Grant("contact", Privilege.Read, AccessLevel.Organization)
                .Grant("account", Privilege.Read, AccessLevel.User))
            .User(Alice, Bu, "reader")
            .User(Bob, Bu, "reader");

        // Two contacts: one parented to Alice's account (readable), one to Bob's account (unreadable to Alice).
        private static InMemoryOrganizationService World(out Guid myContact, out Guid theirContact)
        {
            Guid myAccount = Guid.NewGuid();
            Guid theirAccount = Guid.NewGuid();
            myContact = Guid.NewGuid();
            theirContact = Guid.NewGuid();
            return new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => Scenario(s))
                .Seed(
                    new Entity("account", myAccount) { ["name"] = "Mine", ["ownerid"] = new EntityReference("systemuser", Alice) },
                    new Entity("account", theirAccount) { ["name"] = "Theirs", ["ownerid"] = new EntityReference("systemuser", Bob) },
                    new Entity("contact", myContact) { ["fullname"] = "HasMine", ["parentcustomerid"] = new EntityReference("account", myAccount) },
                    new Entity("contact", theirContact) { ["fullname"] = "HasTheirs", ["parentcustomerid"] = new EntityReference("account", theirAccount) });
        }

        [Fact]
        public void Inner_join_drops_rows_whose_linked_record_is_unreadable()
        {
            InMemoryOrganizationService crm = World(out _, out _);

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name");

            Entity only = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("HasMine", only.GetAttributeValue<string>("fullname")); // the contact joined to Bob's account is dropped
            Assert.Equal("Mine", only.GetAttributeValue<AliasedValue>("acct.name").Value);
        }

        [Fact]
        public void Left_outer_join_keeps_the_row_but_omits_the_unreadable_linked_columns()
        {
            InMemoryOrganizationService crm = World(out _, out _);

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.LeftOuter);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name");

            EntityCollection result = crm.RetrieveMultiple(query);

            Assert.Equal(2, result.Entities.Count); // both contacts kept (left-outer)
            Entity theirs = result.Entities.Single(e => e.GetAttributeValue<string>("fullname") == "HasTheirs");
            Assert.False(theirs.Contains("acct.name")); // Bob's account is unreadable -> its columns are omitted
            Entity mine = result.Entities.Single(e => e.GetAttributeValue<string>("fullname") == "HasMine");
            Assert.Equal("Mine", mine.GetAttributeValue<AliasedValue>("acct.name").Value); // readable account's columns present
        }

        [Fact]
        public void Inner_join_to_a_table_with_no_read_privilege_returns_nothing()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(Bu)
                    .Role("contactonly", r => r.Grant("contact", Privilege.Read, AccessLevel.Organization)) // no account read at all
                    .User(Alice, Bu, "contactonly"))
                .Seed(
                    new Entity("account", account) { ["name"] = "Zava", ["ownerid"] = new EntityReference("systemuser", Bob) },
                    new Entity("contact", contact) { ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", account) });

            QueryExpression query = new QueryExpression("contact");
            query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);

            Assert.Empty(crm.RetrieveMultiple(query).Entities); // can't read the account table -> the inner join yields nothing
        }

        [Fact]
        public void Trimming_applies_at_every_level_of_a_nested_join()
        {
            Guid myAccount = Guid.NewGuid(), theirAccount = Guid.NewGuid();
            Guid myContact = Guid.NewGuid(), theirContact = Guid.NewGuid();
            Guid taskOverMine = Guid.NewGuid(), taskOverTheirs = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(Bu)
                    .Role("reader", r => r
                        .Grant("task", Privilege.Read, AccessLevel.Organization)
                        .Grant("contact", Privilege.Read, AccessLevel.Organization)
                        .Grant("account", Privilege.Read, AccessLevel.User)) // only her own accounts
                    .User(Alice, Bu, "reader")
                    .User(Bob, Bu, "reader"))
                .Seed(
                    new Entity("account", myAccount) { ["name"] = "Mine", ["ownerid"] = new EntityReference("systemuser", Alice) },
                    new Entity("account", theirAccount) { ["name"] = "Theirs", ["ownerid"] = new EntityReference("systemuser", Bob) },
                    new Entity("contact", myContact) { ["fullname"] = "C-Mine", ["parentcustomerid"] = new EntityReference("account", myAccount) },
                    new Entity("contact", theirContact) { ["fullname"] = "C-Theirs", ["parentcustomerid"] = new EntityReference("account", theirAccount) },
                    new Entity("task", taskOverMine) { ["subject"] = "T-Mine", ["regardingobjectid"] = new EntityReference("contact", myContact) },
                    new Entity("task", taskOverTheirs) { ["subject"] = "T-Theirs", ["regardingobjectid"] = new EntityReference("contact", theirContact) });

            // task -> contact -> account, both joins inner. The account (leaf) is the only trimmed level.
            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            LinkEntity contactLink = query.AddLink("contact", "regardingobjectid", "contactid", JoinOperator.Inner);
            contactLink.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);

            Entity only = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("T-Mine", only.GetAttributeValue<string>("subject")); // the task whose grandchild account is unreadable is dropped
        }

        [Fact]
        public void Any_related_record_filter_only_counts_readable_records()
        {
            Guid hasReadable = Guid.NewGuid(), hasUnreadable = Guid.NewGuid();
            Guid readableContact = Guid.NewGuid(), unreadableContact = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(Bu)
                    .Role("reader", r => r
                        .Grant("account", Privilege.Read, AccessLevel.Organization)
                        .Grant("contact", Privilege.Read, AccessLevel.User)) // only her own contacts
                    .User(Alice, Bu, "reader")
                    .User(Bob, Bu, "reader"))
                .Seed(
                    new Entity("account", hasReadable) { ["name"] = "HasReadable" },
                    new Entity("account", hasUnreadable) { ["name"] = "HasUnreadable" },
                    new Entity("contact", readableContact) { ["parentcustomerid"] = new EntityReference("account", hasReadable), ["ownerid"] = new EntityReference("systemuser", Alice) },
                    new Entity("contact", unreadableContact) { ["parentcustomerid"] = new EntityReference("account", hasUnreadable), ["ownerid"] = new EntityReference("systemuser", Bob) });

            // "accounts that have ANY contact" — but the Any is evaluated over the contacts the caller can read.
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AnyAllFilterLinkEntity = new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.Any);

            Entity only = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("HasReadable", only.GetAttributeValue<string>("name")); // the account whose only contact is unreadable doesn't match
        }
    }
}
