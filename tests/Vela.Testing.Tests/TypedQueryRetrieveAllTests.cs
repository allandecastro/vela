using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Vela.Querying;
using Xunit;

namespace Vela.Testing.Tests
{
    // #314: the RetrieveAll / ToList auto-paging execution helpers for TypedQuery<T>.
    public sealed class TypedQueryRetrieveAllTests
    {
        [Fact]
        public void RetrieveAll_pages_through_every_matching_record()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            for (int i = 0; i < 12; i++)
            {
                crm.Create(new Entity("account") { ["name"] = $"A{i}" });
            }

            // pageSize 5 over 12 records forces three pages (5 + 5 + 2); the helper follows MoreRecords to the end.
            List<FakeAccount> all = crm.RetrieveAll(Query.For<FakeAccount>().SelectAll(), pageSize: 5);

            Assert.Equal(12, all.Count);
        }

        [Fact]
        public void ToList_terminal_returns_every_matching_record()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            crm.Create(new Entity("account") { ["name"] = "A" });
            crm.Create(new Entity("account") { ["name"] = "B" });

            List<FakeAccount> all = Query.For<FakeAccount>().SelectAll().ToList(crm, pageSize: 5);

            Assert.Equal(2, all.Count);
        }

        [Fact]
        public void FirstOrDefault_returns_the_first_matching_record_typed()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            crm.Create(new Entity("account") { ["name"] = "Only" });

            FakeAccount? first = Query.For<FakeAccount>().SelectAll().FirstOrDefault(crm);

            Assert.NotNull(first);
            Assert.Equal("Only", first!.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void FirstOrDefault_returns_null_when_nothing_matches()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            FakeAccount? first = Query.For<FakeAccount>().SelectAll().FirstOrDefault(crm);

            Assert.Null(first);
        }

        [Fact]
        public void RetrieveAll_honors_a_Top_cap_instead_of_paging()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            for (int i = 0; i < 10; i++)
            {
                crm.Create(new Entity("account") { ["name"] = $"A{i}" });
            }

            List<FakeAccount> top = crm.RetrieveAll(Query.For<FakeAccount>().SelectAll().Top(3));

            Assert.Equal(3, top.Count);
        }

        [Fact]
        public void RetrieveAll_rejects_null_arguments_and_an_out_of_range_page_size()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).RetrieveAll(Query.For<FakeAccount>()));
            Assert.Throws<ArgumentNullException>(() => crm.RetrieveAll<FakeAccount>(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => crm.RetrieveAll(Query.For<FakeAccount>(), pageSize: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => crm.RetrieveAll(Query.For<FakeAccount>(), pageSize: 5001));
        }
    }
}
