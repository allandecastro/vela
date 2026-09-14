using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Small correctness fixes from the second audit pass: deep-cloning alternate-key values, and the paging
    // cookie naming the next page.
    public sealed class HarnessCorrectnessTests
    {
        [Fact]
        public void Alternate_key_values_on_a_lookup_are_deep_copied_so_a_read_cannot_corrupt_the_store()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = Guid.NewGuid();
            EntityReference lookup = new EntityReference("account", Guid.NewGuid());
            lookup.KeyAttributes["accountnumber"] = new byte[] { 1, 2, 3 };
            crm.Create(new Entity("contact") { Id = id, ["parentcustomerid"] = lookup });

            // Mutate the key value on a retrieved copy ...
            Entity first = crm.Retrieve("contact", id, new ColumnSet(true));
            byte[] retrieved = (byte[])first.GetAttributeValue<EntityReference>("parentcustomerid").KeyAttributes["accountnumber"];
            retrieved[0] = 99;

            // ... the stored value must be untouched (read isolation).
            Entity second = crm.Retrieve("contact", id, new ColumnSet(true));
            byte[] stored = (byte[])second.GetAttributeValue<EntityReference>("parentcustomerid").KeyAttributes["accountnumber"];
            Assert.Equal(1, stored[0]); // was corrupted to 99 before the deep-copy fix
        }

        [Fact]
        public void Paging_cookie_names_the_next_page_when_more_records_remain()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "A" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "B" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "C" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddOrder("name", OrderType.Ascending);
            query.PageInfo = new PagingInfo { PageNumber = 1, Count = 2 };

            EntityCollection page1 = crm.RetrieveMultiple(query);
            Assert.True(page1.MoreRecords);
            Assert.Equal("2", page1.PagingCookie); // the page to fetch next, not the one just returned
        }

        [Fact]
        public void Retrieve_with_explicit_columns_deep_copies_so_a_read_cannot_corrupt_the_store()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = Guid.NewGuid();
            crm.Create(new Entity("account") { Id = id, ["primarycontactid"] = new EntityReference("contact", Guid.NewGuid()) { Name = "original" } });

            // Mutate a reference-typed value on a projected read (explicit ColumnSet, not AllColumns) ...
            Entity first = crm.Retrieve("account", id, new ColumnSet("primarycontactid"));
            first.GetAttributeValue<EntityReference>("primarycontactid").Name = "hacked";

            // ... the stored value must be untouched.
            Entity second = crm.Retrieve("account", id, new ColumnSet("primarycontactid"));
            Assert.Equal("original", second.GetAttributeValue<EntityReference>("primarycontactid").Name); // aliased before the deep-copy fix
        }

        [Fact]
        public void QueryByAttribute_deep_copies_so_a_read_cannot_corrupt_the_store()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = Guid.NewGuid();
            crm.Create(new Entity("account") { Id = id, ["name"] = "Acme", ["primarycontactid"] = new EntityReference("contact", Guid.NewGuid()) { Name = "original" } });

            QueryByAttribute query = new QueryByAttribute("account") { ColumnSet = new ColumnSet("primarycontactid") };
            query.AddAttributeValue("name", "Acme");
            EntityCollection result = crm.RetrieveMultiple(query);
            result.Entities[0].GetAttributeValue<EntityReference>("primarycontactid").Name = "hacked";

            Entity reread = crm.Retrieve("account", id, new ColumnSet("primarycontactid"));
            Assert.Equal("original", reread.GetAttributeValue<EntityReference>("primarycontactid").Name); // aliased before the fix, unlike QueryExpression
        }

        [Fact]
        public void Linked_alias_columns_deep_copy_so_a_read_cannot_corrupt_the_store()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid contactId = Guid.NewGuid();
            crm.Create(new Entity("contact") { Id = contactId, ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()) { Name = "original" } });
            Guid accountId = Guid.NewGuid();
            crm.Create(new Entity("account") { Id = accountId, ["primarycontactid"] = new EntityReference("contact", contactId) });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet(false) };
            query.LinkEntities.Add(new LinkEntity("account", "contact", "primarycontactid", "contactid", JoinOperator.Inner)
            {
                EntityAlias = "c",
                Columns = new ColumnSet("ownerid"),
            });

            EntityCollection result = crm.RetrieveMultiple(query);
            AliasedValue aliased = (AliasedValue)result.Entities[0]["c.ownerid"];
            ((EntityReference)aliased.Value!).Name = "hacked";

            Entity reread = crm.Retrieve("contact", contactId, new ColumnSet("ownerid"));
            Assert.Equal("original", reread.GetAttributeValue<EntityReference>("ownerid").Name); // aliased store value before the fix
        }
    }
}
