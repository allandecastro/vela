using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Targeted tests that pin behaviour specific mutation-testing survivors slipped through: each asserts an exact
    // outcome a surviving mutant would change (an existing test that merely exercises the feature didn't
    // distinguish the mutation). Everything runs through the public RetrieveMultiple path.
    public sealed class HarnessMutationKillTests
    {
        private static Entity TaskRow(string subject, int minutes) =>
            new Entity("task") { Id = Guid.NewGuid(), ["subject"] = subject, ["actualdurationminutes"] = minutes };

        private static Entity Account(string name) =>
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = name };

        private static string[] Names(EntityCollection result, string attribute = "name") =>
            result.Entities.Select(e => e.GetAttributeValue<string>(attribute)).ToArray();

        // Between is '>= lo && <= hi'. A value ABOVE the range must be excluded (kills && -> ||).
        [Fact]
        public void Between_excludes_values_above_the_upper_bound()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                TaskRow("low", 50), TaskRow("mid", 150), TaskRow("high", 300));

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition("actualdurationminutes", ConditionOperator.Between, 100, 200);

            Assert.Equal(new[] { "mid" }, Names(crm.RetrieveMultiple(query), "subject"));
        }

        // NotBetween is '!(>= lo && <= hi)'. Values on BOTH sides of the range match; the in-range one is excluded.
        [Fact]
        public void NotBetween_matches_only_values_outside_the_range()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                TaskRow("low", 50), TaskRow("mid", 150), TaskRow("high", 300));

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition("actualdurationminutes", ConditionOperator.NotBetween, 100, 200);

            Assert.Equal(new[] { "high", "low" }, Names(crm.RetrieveMultiple(query), "subject").OrderBy(n => n).ToArray());
        }

        // NotIn is '!values.Any(equal)'. A row equal to any listed value is excluded (kills Any -> All, which
        // would let a row matching just one of several values slip through).
        [Fact]
        public void NotIn_excludes_rows_matching_any_listed_value()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(Account("A"), Account("C"));

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("name", ConditionOperator.NotIn, "A", "B");

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("C", match.GetAttributeValue<string>("name"));
        }

        // EqualUserId is 'actual != null && equals(currentUser)'. A record owned by someone ELSE is excluded
        // (kills && -> ||, which would let any non-null owner through). Tested in isolation so no other condition
        // masks it.
        [Fact]
        public void EqualUserId_excludes_records_owned_by_another_user()
        {
            Guid me = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(me).Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "mine", ["ownerid"] = new EntityReference("systemuser", me) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "theirs", ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()) });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition(new ConditionExpression("ownerid", ConditionOperator.EqualUserId));

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("mine", match.GetAttributeValue<string>("subject"));
        }

        // OlderThanXDays is 'date < Today.AddDays(-n)'. A record dated EXACTLY n days ago is NOT older
        // (kills < -> <=, which would wrongly include the boundary day). The engine's "today" is the injected
        // clock's UTC date, so pin a fixed clock and seed relative to the SAME basis - seeding with the host's
        // local DateTime.Today made this flake a day in either direction whenever the local date differed from UTC.
        [Fact]
        public void OlderThanXDays_excludes_the_exact_boundary_day()
        {
            DateTime now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithClock(() => now).Seed(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "boundary", ["createdon"] = now.Date.AddDays(-7) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "older", ["createdon"] = now.Date.AddDays(-8) });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition("createdon", ConditionOperator.OlderThanXDays, 7);

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("older", match.GetAttributeValue<string>("subject"));
        }

        // Distinct keeps the FIRST occurrence of each projected row (kills negating seen.Add, which would instead
        // keep only the later duplicates). Three rows projecting to two distinct values pin the count.
        [Fact]
        public void Distinct_keeps_one_of_each_distinct_projection()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                Account("Same"), Account("Same"), Account("Other"));

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), Distinct = true };

            Assert.Equal(new[] { "Other", "Same" }, Names(crm.RetrieveMultiple(query)).OrderBy(n => n).ToArray());
        }

        // NotAny filter: a parent matches only when NO related record satisfies the criteria.
        [Fact]
        public void NotAny_filter_matches_parents_with_no_satisfying_related_record()
        {
            Guid jane = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("contact") { Id = jane, ["fullname"] = "Jane" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava", ["primarycontactid"] = new EntityReference("contact", jane) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            query.Criteria.AnyAllFilterLinkEntity = new LinkEntity("contact", "account", "contactid", "primarycontactid", JoinOperator.NotAny)
            {
                LinkCriteria = new FilterExpression { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, "Zava") } }
            };

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Bob", match.GetAttributeValue<string>("fullname"));
        }

        // Multi-level ordering: the second order key breaks ties from the first, ascending and descending
        // (kills the ThenBy/ThenByDescending swaps).
        [Fact]
        public void SecondaryOrder_breaks_ties_ascending_and_descending()
        {
            Entity Cat(string industry, string name) =>
                new Entity("account") { Id = Guid.NewGuid(), ["industrycode"] = industry, ["name"] = name };

            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                Cat("A", "Bravo"), Cat("A", "Alpha"), Cat("B", "Charlie"));

            QueryExpression asc = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            asc.AddOrder("industrycode", OrderType.Ascending);
            asc.AddOrder("name", OrderType.Ascending);
            Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, Names(crm.RetrieveMultiple(asc)));

            QueryExpression desc = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            desc.AddOrder("industrycode", OrderType.Ascending);
            desc.AddOrder("name", OrderType.Descending);
            Assert.Equal(new[] { "Bravo", "Alpha", "Charlie" }, Names(crm.RetrieveMultiple(desc)));
        }

        // FetchXML: a link-entity with no link-type is an INNER join, so unmatched parents are excluded
        // (kills forcing the join to LeftOuter).
        [Fact]
        public void FetchXml_inner_link_excludes_unmatched_rows()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", accountId) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob" });

            string fetch = @"
                <fetch>
                  <entity name='contact'>
                    <attribute name='fullname' />
                    <link-entity name='account' from='accountid' to='parentcustomerid' alias='acct'>
                      <attribute name='name' />
                    </link-entity>
                  </entity>
                </fetch>";

            Entity match = Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);
            Assert.Equal("Jane", match.GetAttributeValue<string>("fullname")); // Bob (no parent) excluded by the inner join
        }

        // FetchXML: a link-entity's own <filter> is applied to the linked rows (kills dropping LinkCriteria).
        [Fact]
        public void FetchXml_link_entity_filter_is_applied()
        {
            Guid zava = Guid.NewGuid();
            Guid fabrikam = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = zava, ["name"] = "Zava" },
                new Entity("account") { Id = fabrikam, ["name"] = "Fabrikam" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", zava) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob", ["parentcustomerid"] = new EntityReference("account", fabrikam) });

            string fetch = @"
                <fetch>
                  <entity name='contact'>
                    <attribute name='fullname' />
                    <link-entity name='account' from='accountid' to='parentcustomerid'>
                      <filter><condition attribute='name' operator='eq' value='Zava' /></filter>
                    </link-entity>
                  </entity>
                </fetch>";

            Entity match = Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);
            Assert.Equal("Jane", match.GetAttributeValue<string>("fullname"));
        }

        // FetchXML: distinct='true' de-duplicates the projected rows (kills emptying the "distinct" attribute name).
        [Fact]
        public void FetchXml_distinct_attribute_dedupes_rows()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(Account("Same"), Account("Same"));

            string fetch = @"
                <fetch distinct='true'>
                  <entity name='account'>
                    <attribute name='name' />
                  </entity>
                </fetch>";

            Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);
        }

        // FetchXML: only the listed <attribute>s are projected (kills forcing an empty/all column set).
        [Fact]
        public void FetchXml_projects_only_listed_attributes()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava", ["telephone1"] = "555" });

            string fetch = @"
                <fetch>
                  <entity name='account'>
                    <attribute name='name' />
                  </entity>
                </fetch>";

            Entity row = Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);
            Assert.Equal("Zava", row.GetAttributeValue<string>("name"));
            Assert.False(row.Contains("telephone1")); // only 'name' was requested
        }

        // FetchXML: an unsupported condition operator surfaces a clear error (kills removing the default throw).
        [Fact]
        public void FetchXml_unsupported_operator_throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(Account("Zava"));

            string fetch = @"
                <fetch>
                  <entity name='account'>
                    <filter><condition attribute='name' operator='totally-bogus' value='x' /></filter>
                  </entity>
                </fetch>";

            Assert.Throws<NotSupportedException>(() => crm.RetrieveMultiple(new FetchExpression(fetch)));
        }
    }
}
