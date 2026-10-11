using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: the in-memory query engine must filter, join and order like Dataverse, including SQL
    // three-valued-logic edge cases (a NULL never satisfies a comparison) and a clear NotSupported for an
    // operator it does not implement. These add the parity/edge cases the existing InMemoryQueryTests /
    // FetchXmlOperatorParityTests do not already cover (which own the string operators, Between/relational
    // null-row skipping, inner/outer/nested joins, Any-filter, aggregates and paging).
    public sealed class QueryEngineFidelityTests
    {
        private static InMemoryOrganizationService Service() => new InMemoryOrganizationService();

        private static Entity Account(Guid id, string name, int? revenue = null, string? category = null)
        {
            Entity e = new Entity("account", id) { ["name"] = name };
            if (revenue.HasValue)
            {
                e["revenue"] = revenue.Value;
            }

            if (category != null)
            {
                e["category"] = category;
            }

            return e;
        }

        private static QueryExpression Query(params ConditionExpression[] conditions)
        {
            QueryExpression q = new QueryExpression("account") { ColumnSet = new ColumnSet(true) };
            foreach (ConditionExpression c in conditions)
            {
                q.Criteria.AddCondition(c);
            }

            return q;
        }

        private static string[] Names(EntityCollection result) =>
            result.Entities.Select(e => e.GetAttributeValue<string>("name")).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // --- In / NotIn ---------------------------------------------------------------------------------

        [Fact]
        public void In_matches_any_listed_value()
        {
            InMemoryOrganizationService svc = Service().WithCurrentUser(Guid.NewGuid());
            svc.Create(Account(Guid.NewGuid(), "A", category: "gold"));
            svc.Create(Account(Guid.NewGuid(), "B", category: "silver"));
            svc.Create(Account(Guid.NewGuid(), "C", category: "bronze"));

            EntityCollection result = svc.RetrieveMultiple(Query(
                new ConditionExpression("category", ConditionOperator.In, new object[] { "gold", "bronze" })));

            Assert.Equal(new[] { "A", "C" }, Names(result));
        }

        [Fact]
        public void NotIn_excludes_listed_values_and_matches_null_rows()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "A", category: "gold"));
            svc.Create(Account(Guid.NewGuid(), "B", category: "silver"));
            svc.Create(Account(Guid.NewGuid(), "C")); // category is null -> matches (verified live, #451)

            EntityCollection result = svc.RetrieveMultiple(Query(
                new ConditionExpression("category", ConditionOperator.NotIn, new object[] { "gold" })));

            // "B" and the null-category "C": Dataverse adds "OR column IS NULL" to a negative operator.
            Assert.Equal(new[] { "B", "C" }, Names(result));
        }

        // --- Null / NotNull -----------------------------------------------------------------------------

        [Fact]
        public void NotNull_matches_only_rows_where_the_attribute_is_present()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "A", category: "gold"));
            svc.Create(Account(Guid.NewGuid(), "B"));

            Assert.Equal(new[] { "A" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("category", ConditionOperator.NotNull)))));
            Assert.Equal(new[] { "B" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("category", ConditionOperator.Null)))));
        }

        // --- Negative string operators match null rows (verified live, #451) ------------------------------

        [Fact]
        public void DoesNotBeginWith_and_DoesNotEndWith_and_NotLike_match_null_rows()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "Contoso"));
            svc.Create(Account(Guid.NewGuid(), "Fabrikam"));
            Entity noName = new Entity("account", Guid.NewGuid()); // name is null
            svc.Create(noName);

            Assert.Equal(new[] { null, "Fabrikam" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.DoesNotBeginWith, "Cont")))));
            Assert.Equal(new[] { null, "Contoso" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.DoesNotEndWith, "am")))));
            Assert.Equal(new[] { null, "Fabrikam" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.NotLike, "Cont%")))));

            // not-like '%' matches only the null row; like '%' matches every row but that one.
            Assert.Equal(new string?[] { null }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.NotLike, "%")))));
            Assert.Equal(new[] { "Contoso", "Fabrikam" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.Like, "%")))));
            Assert.Equal(new[] { "Contoso" }, Names(svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.BeginsWith, string.Empty),
                new ConditionExpression("name", ConditionOperator.EndsWith, "so")))));
        }

        // --- Like with a %contains% pattern (the way "contains" is expressed) ----------------------------

        [Fact]
        public void Like_expresses_contains_semantics_with_surrounding_wildcards()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "Contoso Ltd"));
            svc.Create(Account(Guid.NewGuid(), "Fabrikam"));

            EntityCollection result = svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.Like, "%oso%")));

            Assert.Equal(new[] { "Contoso Ltd" }, Names(result));
        }

        // --- Ordered operators: a NULL OPERAND yields NO match, not an exception -------------------------

        [Fact]
        public void An_ordered_operator_with_a_null_operand_matches_nothing_without_throwing()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "A", revenue: 50));
            svc.Create(Account(Guid.NewGuid(), "B", revenue: 100));

            ConditionExpression condition = new ConditionExpression("revenue", ConditionOperator.GreaterThan);
            condition.Values.Add(null); // the operand is null

            EntityCollection result = svc.RetrieveMultiple(Query(condition));

            Assert.Empty(result.Entities); // no match, and no exception
        }

        [Fact]
        public void Between_with_a_null_bound_matches_nothing_without_throwing()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "A", revenue: 50));

            ConditionExpression condition = new ConditionExpression("revenue", ConditionOperator.Between);
            condition.Values.Add(10);
            condition.Values.Add(null); // upper bound is null

            Assert.Empty(svc.RetrieveMultiple(Query(condition)).Entities);
        }

        // --- Multi-column OrderBy -----------------------------------------------------------------------

        [Fact]
        public void Multi_column_order_by_sorts_by_the_primary_then_the_secondary_key()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "a-low", revenue: 10, category: "b"));
            svc.Create(Account(Guid.NewGuid(), "a-high", revenue: 90, category: "b"));
            svc.Create(Account(Guid.NewGuid(), "z-mid", revenue: 50, category: "a"));

            QueryExpression q = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet(true),
                Orders =
                {
                    new OrderExpression("category", OrderType.Ascending),  // primary
                    new OrderExpression("revenue", OrderType.Descending),  // secondary tie-break
                },
            };

            string[] ordered = svc.RetrieveMultiple(q).Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray();

            // category 'a' first (z-mid), then category 'b' rows by descending revenue (a-high before a-low).
            Assert.Equal(new[] { "z-mid", "a-high", "a-low" }, ordered);
        }

        // --- ColumnSet: partial vs true -----------------------------------------------------------------

        [Fact]
        public void A_partial_column_set_returns_only_the_requested_columns_while_true_returns_all()
        {
            InMemoryOrganizationService svc = Service();
            Guid id = svc.Create(Account(Guid.NewGuid(), "Contoso", revenue: 42, category: "gold"));

            Entity partial = svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet("name") })
                .Entities.Single(e => e.Id == id);
            Assert.True(partial.Contains("name"));
            Assert.False(partial.Contains("revenue"));
            Assert.False(partial.Contains("category"));

            Entity all = svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) })
                .Entities.Single(e => e.Id == id);
            Assert.True(all.Contains("name"));
            Assert.True(all.Contains("revenue"));
            Assert.True(all.Contains("category"));
        }

        // --- LinkEntity: inner join drops unmatched, left-outer keeps them -------------------------------

        [Fact]
        public void Inner_join_drops_unmatched_parents_left_outer_keeps_them()
        {
            InMemoryOrganizationService svc = Service();
            Guid withChild = svc.Create(new Entity("account") { ["name"] = "HasContact" });
            svc.Create(new Entity("account") { ["name"] = "Lonely" });
            svc.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = new EntityReference("account", withChild) });

            QueryExpression inner = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            inner.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.Inner);
            Assert.Equal(new[] { "HasContact" }, Names(svc.RetrieveMultiple(inner)));

            QueryExpression outer = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            outer.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.LeftOuter);
            Assert.Equal(new[] { "HasContact", "Lonely" }, Names(svc.RetrieveMultiple(outer)));
        }

        // --- Any / NotAny / All link semantics ----------------------------------------------------------

        private static InMemoryOrganizationService WithAccountsAndContacts(out Guid allAda, out Guid mixed, out Guid noAda, out Guid childless)
        {
            InMemoryOrganizationService svc = Service();
            allAda = svc.Create(new Entity("account") { ["name"] = "AllAda" });
            mixed = svc.Create(new Entity("account") { ["name"] = "Mixed" });
            noAda = svc.Create(new Entity("account") { ["name"] = "NoAda" });
            childless = svc.Create(new Entity("account") { ["name"] = "Childless" });

            svc.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = new EntityReference("account", allAda) });
            svc.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = new EntityReference("account", allAda) });
            svc.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = new EntityReference("account", mixed) });
            svc.Create(new Entity("contact") { ["firstname"] = "Bob", ["parentcustomerid"] = new EntityReference("account", mixed) });
            svc.Create(new Entity("contact") { ["firstname"] = "Bob", ["parentcustomerid"] = new EntityReference("account", noAda) });
            return svc;
        }

        private static QueryExpression AnyAllQuery(JoinOperator op)
        {
            QueryExpression q = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            LinkEntity link = new LinkEntity("account", "contact", "accountid", "parentcustomerid", op);
            link.LinkCriteria.AddCondition("firstname", ConditionOperator.Equal, "Ada");
            q.Criteria.AnyAllFilterLinkEntity = link;
            return q;
        }

        [Fact]
        public void Any_matches_parents_with_at_least_one_related_record_that_satisfies_the_filter()
        {
            InMemoryOrganizationService svc = WithAccountsAndContacts(out _, out _, out _, out _);
            Assert.Equal(new[] { "AllAda", "Mixed" }, Names(svc.RetrieveMultiple(AnyAllQuery(JoinOperator.Any))));
        }

        [Fact]
        public void NotAny_matches_parents_with_no_related_record_that_satisfies_the_filter()
        {
            InMemoryOrganizationService svc = WithAccountsAndContacts(out _, out _, out _, out _);
            // Childless has no contacts, NoAda has only Bob — neither has an "Ada" child.
            Assert.Equal(new[] { "Childless", "NoAda" }, Names(svc.RetrieveMultiple(AnyAllQuery(JoinOperator.NotAny))));
        }

        [Fact]
        public void All_matches_parents_that_have_related_records_none_of_which_match_the_link_criteria()
        {
            // The engine models JoinOperator.All as "has related records AND none of them satisfies the link
            // criteria" (linked.Count > 0 && !anySatisfies). This mirrors the platform applying the inner condition
            // negated, so an All(firstname = "Ada") returns parents whose children are NONE of them "Ada".
            InMemoryOrganizationService svc = WithAccountsAndContacts(out _, out _, out _, out _);

            // NoAda has children (Bob) and none is "Ada"; AllAda/Mixed each have an "Ada" child; Childless has none.
            Assert.Equal(new[] { "NoAda" }, Names(svc.RetrieveMultiple(AnyAllQuery(JoinOperator.All))));
        }

        // --- An unimplemented operator throws a clear NotSupported rather than silently mis-filtering ----

        [Fact]
        public void An_unsupported_condition_operator_throws_NotSupported()
        {
            InMemoryOrganizationService svc = Service();
            svc.Create(Account(Guid.NewGuid(), "Contoso"));

            // ConditionOperator.Contains is not implemented by the engine (contains is expressed via Like '%x%').
            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => svc.RetrieveMultiple(Query(
                new ConditionExpression("name", ConditionOperator.Contains, "ont"))));

            Assert.Contains("Contains", ex.Message);
        }
    }
}
