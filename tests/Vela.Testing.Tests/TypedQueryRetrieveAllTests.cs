using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
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
        public void Scalar_aggregates_count_sum_and_average_only_the_matching_rows()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            crm.Create(new Entity("account") { ["name"] = "keep", ["numberofemployees"] = 10 });
            crm.Create(new Entity("account") { ["name"] = "keep", ["numberofemployees"] = 30 });
            crm.Create(new Entity("account") { ["name"] = "drop", ["numberofemployees"] = 999 });

            // A Where must compose into the aggregate (the "drop" row is excluded): count 2, sum 40, avg 20 -
            // fails if the scalar executor loses Criteria.
            TypedQuery<FakeAccount> Matching() => Query.For<FakeAccount>().Where(a => a.Name, ConditionOperator.Equal, "keep");

            Assert.Equal(2, Matching().CountScalar(crm, a => a.NumberOfEmployees));
            Assert.Equal(40m, Matching().SumScalar(crm, a => a.NumberOfEmployees));
            Assert.Equal(20m, Matching().AverageScalar(crm, a => a.NumberOfEmployees));
        }

        [Fact]
        public void Scalar_min_and_max_return_the_column_type_over_the_matching_rows()
        {
            // Seeded: a Create ignores a supplied createdon, as on Dataverse.
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { ["name"] = "keep", ["numberofemployees"] = 10, ["createdon"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new Entity("account") { ["name"] = "keep", ["numberofemployees"] = 30, ["createdon"] = new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
                new Entity("account") { ["name"] = "drop", ["numberofemployees"] = 999, ["createdon"] = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

            TypedQuery<FakeAccount> Matching() => Query.For<FakeAccount>().Where(a => a.Name, ConditionOperator.Equal, "keep");

            // int? column -> int? back (no cast at the call site), and the Where excludes the "drop" row.
            Assert.Equal(10, Matching().MinScalar(crm, a => a.NumberOfEmployees));
            Assert.Equal(30, Matching().MaxScalar(crm, a => a.NumberOfEmployees));

            // DateTime? column -> DateTime? back.
            Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Matching().MinScalar(crm, a => a.CreatedOn));
            Assert.Equal(new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc), Matching().MaxScalar(crm, a => a.CreatedOn));
        }

        [Fact]
        public void Scalar_min_and_max_over_a_money_column_return_the_decimal_amount()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            crm.Create(new Entity("account") { ["name"] = "keep", ["revenue"] = new Money(100m) });
            crm.Create(new Entity("account") { ["name"] = "keep", ["revenue"] = new Money(250m) });

            TypedQuery<FakeAccount> Matching() => Query.For<FakeAccount>().Where(a => a.Name, ConditionOperator.Equal, "keep");

            // Money column surfaces as Microsoft.Xrm.Sdk.Money; the dedicated overload hands back the decimal amount.
            Assert.Equal(100m, Matching().MinScalar(crm, a => a.Revenue));
            Assert.Equal(250m, Matching().MaxScalar(crm, a => a.Revenue));
        }

        [Fact]
        public void Scalar_min_and_max_are_null_when_nothing_matches()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Assert.Null(Query.For<FakeAccount>().MinScalar(crm, a => a.NumberOfEmployees));
            Assert.Null(Query.For<FakeAccount>().MaxScalar(crm, a => a.CreatedOn));
            Assert.Null(Query.For<FakeAccount>().MaxScalar(crm, a => a.Revenue));
        }

        [Fact]
        public void Scalar_sum_is_null_and_count_is_zero_when_nothing_matches()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            // null (not 0) distinguishes "no rows" from a real sum of 0.
            Assert.Null(Query.For<FakeAccount>().SumScalar(crm, a => a.NumberOfEmployees));
            Assert.Equal(0, Query.For<FakeAccount>().CountScalar(crm, a => a.NumberOfEmployees));
        }

        [Fact]
        public void Scalar_aggregate_does_not_mutate_a_reusable_query()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            crm.Create(new Entity("account") { ["name"] = "A", ["numberofemployees"] = 5 });
            crm.Create(new Entity("account") { ["name"] = "B", ["numberofemployees"] = 7 });

            TypedQuery<FakeAccount> query = Query.For<FakeAccount>().SelectAll();

            _ = query.SumScalar(crm, a => a.NumberOfEmployees);   // swaps the ColumnSet to an aggregate internally...
            List<FakeAccount> all = query.ToList(crm);             // ...and must restore it, so this is a normal read

            Assert.Equal(2, all.Count);
            Assert.Contains(all, a => a.Name == "A");
        }

        [Fact]
        public void FirstOrDefault_does_not_permanently_cap_a_reusable_query()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            for (int i = 0; i < 3; i++)
            {
                crm.Create(new Entity("account") { ["name"] = $"A{i}" });
            }

            TypedQuery<FakeAccount> query = Query.For<FakeAccount>().SelectAll();

            _ = query.FirstOrDefault(crm);            // caps Top at 1 internally...
            List<FakeAccount> all = query.ToList(crm); // ...but must be restored, so this still returns all 3

            Assert.Equal(3, all.Count);
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
