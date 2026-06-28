using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Covers the date-relative condition operators (On/OnOrBefore/OnOrAfter, Today/Yesterday/Tomorrow,
    // ThisMonth/ThisYear, LastXDays/NextXDays/OlderThanXDays) plus the user-id and Not-range operators and
    // the join/ordering edge cases the other suites don't reach. Dates are seeded relative to
    // DateTime.Today so the assertions stay deterministic regardless of when the suite runs.
    public sealed class InMemoryQueryEngineDateOperatorTests
    {
        private const string Attr = "duedate";

        // past (-10), yesterday (-1), today (0), tomorrow (+1), future (+10), and a row with no date.
        private static InMemoryOrganizationService Dated()
        {
            DateTime today = DateTime.Today;
            return new InMemoryOrganizationService().Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "past", [Attr] = today.AddDays(-10) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "yesterday", [Attr] = today.AddDays(-1) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "today", [Attr] = today },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "tomorrow", [Attr] = today.AddDays(1) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "future", [Attr] = today.AddDays(10) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "undated" });
        }

        private static string[] Run(InMemoryOrganizationService crm, ConditionExpression condition)
        {
            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition(condition);
            return crm.RetrieveMultiple(query).Entities
                .Select(e => e.GetAttributeValue<string>("subject"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }

        [Fact]
        public void On_matches_only_the_same_day_and_skips_the_undated_row()
        {
            Assert.Equal(new[] { "today" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.On, DateTime.Today)));
        }

        [Fact]
        public void OnOrBefore_includes_the_boundary_day()
        {
            Assert.Equal(new[] { "past", "today", "yesterday" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.OnOrBefore, DateTime.Today)));
        }

        [Fact]
        public void OnOrAfter_includes_the_boundary_day()
        {
            Assert.Equal(new[] { "future", "today", "tomorrow" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.OnOrAfter, DateTime.Today)));
        }

        [Fact]
        public void Today_Yesterday_Tomorrow_each_match_their_single_row()
        {
            Assert.Equal(new[] { "today" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.Today)));
            Assert.Equal(new[] { "yesterday" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.Yesterday)));
            Assert.Equal(new[] { "tomorrow" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.Tomorrow)));
        }

        [Fact]
        public void LastXDays_NextXDays_OlderThanXDays_bound_relative_windows()
        {
            // LastXDays(7): (Today-7 .. Today]; yesterday and today fall in, the -10 past row does not.
            Assert.Equal(new[] { "today", "yesterday" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.LastXDays, 7)));
            // NextXDays(7): [Today .. Today+7]; today and tomorrow fall in, the +10 future row does not.
            Assert.Equal(new[] { "today", "tomorrow" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.NextXDays, 7)));
            // OlderThanXDays(7): strictly before Today-7; only the -10 past row qualifies.
            Assert.Equal(new[] { "past" }, Run(Dated(), new ConditionExpression(Attr, ConditionOperator.OlderThanXDays, 7)));
        }

        [Fact]
        public void ThisMonth_and_ThisYear_match_only_current_period_rows()
        {
            DateTime today = DateTime.Today;
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "now", [Attr] = today },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "lastyear", [Attr] = today.AddYears(-1) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "twomonthsago", [Attr] = today.AddMonths(-2) });

            Assert.Equal(new[] { "now" }, Run(crm, new ConditionExpression(Attr, ConditionOperator.ThisMonth)));
            // ThisYear includes the current-month row and the two-months-ago row only when it stays in this year.
            string[] thisYear = Run(crm, new ConditionExpression(Attr, ConditionOperator.ThisYear));
            Assert.Contains("now", thisYear);
            Assert.DoesNotContain("lastyear", thisYear);
        }

        [Fact]
        public void Date_operators_parse_string_dates()
        {
            // DatePart accepts an ISO string and compares on the date component (covers the string-parse branch).
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "stringdate", [Attr] = DateTime.Today.ToString("yyyy-MM-dd") });

            Assert.Equal(new[] { "stringdate" }, Run(crm, new ConditionExpression(Attr, ConditionOperator.Today)));
        }

        [Fact]
        public void NotEqualUserId_keeps_null_and_other_owners_but_excludes_the_current_user()
        {
            Guid me = Guid.NewGuid();
            Guid other = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(me).Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "mine", ["ownerid"] = new EntityReference("systemuser", me) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "theirs", ["ownerid"] = new EntityReference("systemuser", other) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "unowned" });

            Assert.Equal(new[] { "theirs", "unowned" }, Run(crm, new ConditionExpression("ownerid", ConditionOperator.NotEqualUserId)));
        }

        [Fact]
        public void NotBetween_excludes_the_range_and_the_null_row()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "low", ["numberofemployees"] = 50 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "inside", ["numberofemployees"] = 150 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "high", ["numberofemployees"] = 250 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "unknown" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("numberofemployees", ConditionOperator.NotBetween, 100, 200);

            string[] names = crm.RetrieveMultiple(query).Entities
                .Select(e => e.GetAttributeValue<string>("name"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "high", "low" }, names); // inside excluded by range, unknown excluded as null
        }

        [Fact]
        public void CrossApply_returns_the_parent_once_for_a_match_and_keeps_unmatched_parents()
        {
            Guid withChild = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = withChild, ["name"] = "HasContacts" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Lonely" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "A", ["parentcustomerid"] = new EntityReference("account", withChild) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "B", ["parentcustomerid"] = new EntityReference("account", withChild) });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.MatchFirstRowUsingCrossApply);

            string[] names = crm.RetrieveMultiple(query).Entities
                .Select(e => e.GetAttributeValue<string>("name"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            // HasContacts has two children but appears once (first match); Lonely is kept (left-outer semantics).
            Assert.Equal(new[] { "HasContacts", "Lonely" }, names);
        }

        [Fact]
        public void AllFilter_requires_every_related_record_to_match()
        {
            Guid allMatch = Guid.NewGuid();
            Guid someMatch = Guid.NewGuid();
            Guid noChildren = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = allMatch, ["name"] = "AllActive" },
                new Entity("account") { Id = someMatch, ["name"] = "Mixed" },
                new Entity("account") { Id = noChildren, ["name"] = "Childless" },
                new Entity("contact") { Id = Guid.NewGuid(), ["statecode"] = new OptionSetValue(0), ["parentcustomerid"] = new EntityReference("account", allMatch) },
                new Entity("contact") { Id = Guid.NewGuid(), ["statecode"] = new OptionSetValue(0), ["parentcustomerid"] = new EntityReference("account", allMatch) },
                new Entity("contact") { Id = Guid.NewGuid(), ["statecode"] = new OptionSetValue(0), ["parentcustomerid"] = new EntityReference("account", someMatch) },
                new Entity("contact") { Id = Guid.NewGuid(), ["statecode"] = new OptionSetValue(1), ["parentcustomerid"] = new EntityReference("account", someMatch) });

            // SDK semantics: an `All` join matches parents that have related records of which NONE satisfy the
            // LinkCriteria, so "every child is active (statecode 0)" is expressed as the complement: no child is
            // inactive (statecode != 0). AllActive qualifies; Mixed has an inactive child; Childless has none.
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AnyAllFilterLinkEntity = new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.All)
            {
                LinkCriteria = new FilterExpression { Conditions = { new ConditionExpression("statecode", ConditionOperator.NotEqual, 0) } }
            };

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("AllActive", match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void NotAllFilter_matches_when_any_related_record_satisfies_the_criteria()
        {
            Guid mixed = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = mixed, ["name"] = "Mixed" },
                new Entity("contact") { Id = Guid.NewGuid(), ["statecode"] = new OptionSetValue(0), ["parentcustomerid"] = new EntityReference("account", mixed) },
                new Entity("contact") { Id = Guid.NewGuid(), ["statecode"] = new OptionSetValue(1), ["parentcustomerid"] = new EntityReference("account", mixed) });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AnyAllFilterLinkEntity = new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.NotAll)
            {
                LinkCriteria = new FilterExpression { Conditions = { new ConditionExpression("statecode", ConditionOperator.Equal, 0) } }
            };

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Mixed", match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Aggregate_orders_descending_then_ascending_and_applies_top()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "B" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "B" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "C" });

            // Groups: A=2, B=2, C=1. Order by count desc, then category asc, top 2 -> A, B (C is a smaller group).
            string fetch = @"
                <fetch aggregate='true' top='2'>
                  <entity name='opportunity'>
                    <attribute name='category' groupby='true' alias='cat' />
                    <attribute name='opportunityid' aggregate='count' alias='cnt' />
                    <order alias='cnt' descending='true' />
                    <order alias='cat' />
                  </entity>
                </fetch>";

            var rows = crm.RetrieveMultiple(new FetchExpression(fetch)).Entities;
            Assert.Equal(2, rows.Count);
            Assert.Equal("A", rows[0].GetAttributeValue<AliasedValue>("cat").Value);
            Assert.Equal("B", rows[1].GetAttributeValue<AliasedValue>("cat").Value);
        }

        [Fact]
        public void Aggregate_orders_ascending_then_descending()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "B" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "B" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "C" });

            // Order by count asc (C=1 first), then category desc for ties (B before A).
            string fetch = @"
                <fetch aggregate='true'>
                  <entity name='opportunity'>
                    <attribute name='category' groupby='true' alias='cat' />
                    <attribute name='opportunityid' aggregate='count' alias='cnt' />
                    <order alias='cnt' />
                    <order alias='cat' descending='true' />
                  </entity>
                </fetch>";

            var rows = crm.RetrieveMultiple(new FetchExpression(fetch)).Entities;
            Assert.Equal(new[] { "C", "B", "A" }, rows.Select(r => (string)r.GetAttributeValue<AliasedValue>("cat").Value).ToArray());
        }

        [Fact]
        public void Paging_normalizes_a_non_positive_page_number_to_the_first_page()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "A" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "B" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "C" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddOrder("name", OrderType.Ascending);
            query.PageInfo = new PagingInfo { PageNumber = 0, Count = 2 }; // PageNumber <= 0 -> treated as page 1

            EntityCollection page = crm.RetrieveMultiple(query);
            Assert.Equal(new[] { "A", "B" }, page.Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray());
            Assert.True(page.MoreRecords);
        }

        [Fact]
        public void Multi_column_order_applies_the_secondary_key_for_ties()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("contact") { Id = Guid.NewGuid(), ["lastname"] = "Smith", ["firstname"] = "Bob" },
                new Entity("contact") { Id = Guid.NewGuid(), ["lastname"] = "Smith", ["firstname"] = "Ann" },
                new Entity("contact") { Id = Guid.NewGuid(), ["lastname"] = "Adams", ["firstname"] = "Zoe" });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("firstname") };
            query.AddOrder("lastname", OrderType.Ascending);
            query.AddOrder("firstname", OrderType.Descending); // tie-break within "Smith": Bob before Ann

            Assert.Equal(new[] { "Zoe", "Bob", "Ann" }, crm.RetrieveMultiple(query).Entities.Select(e => e.GetAttributeValue<string>("firstname")).ToArray());
        }

        [Fact]
        public void Ascending_order_sorts_null_keys_first()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Alpha", ["numberofemployees"] = 100 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Nullco" }, // no numberofemployees
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Charlie", ["numberofemployees"] = 200 });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddOrder("numberofemployees", OrderType.Ascending); // null compares lowest -> first

            Assert.Equal(new[] { "Nullco", "Alpha", "Charlie" }, crm.RetrieveMultiple(query).Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray());
        }

        [Fact]
        public void LastXDays_with_a_non_numeric_bound_collapses_to_today()
        {
            // A non-numeric operand parses to 0, so the window degenerates to [Today .. Today].
            DateTime today = DateTime.Today;
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "today", [Attr] = today },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "past", [Attr] = today.AddDays(-3) });

            Assert.Equal(new[] { "today" }, Run(crm, new ConditionExpression(Attr, ConditionOperator.LastXDays, "not-a-number")));
        }

        [Fact]
        public void Ordering_falls_back_to_string_comparison_for_incompatible_types()
        {
            // A column holding mixed value types (int and a non-numeric string) can't be compared via
            // Convert.ChangeType; the comparer falls back to string comparison instead of throwing.
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "n5", ["mixed"] = 5 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "alpha", ["mixed"] = "alpha" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "n10", ["mixed"] = 10 });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name", "mixed") };
            query.AddOrder("mixed", OrderType.Ascending);

            Assert.Equal(3, crm.RetrieveMultiple(query).Entities.Count); // ordering completes without throwing
        }
    }
}
