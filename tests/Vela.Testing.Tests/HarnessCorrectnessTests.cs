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
    }
}
