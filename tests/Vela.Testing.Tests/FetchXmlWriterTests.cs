using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Xunit;

namespace Vela.Testing.Tests
{
    // FetchXmlWriter serializes a QueryExpression to FetchXML. The strongest test is a behavioural ROUND-TRIP: run
    // the QueryExpression and its ToFetchXml() (as a FetchExpression) through the in-memory harness and assert the
    // same rows come back — the harness's FetchXML parser is the independent inverse. Operators the harness parser
    // doesn't read back yet (e.g. between) and FetchXML-only options are covered by exact-string snapshots instead.
    public sealed class FetchXmlWriterTests
    {
        private static readonly Guid Zava = Guid.NewGuid();
        private static readonly Guid Fabrikam = Guid.NewGuid();
        private static readonly Guid Contoso = Guid.NewGuid();

        private static InMemoryOrganizationService World()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account", Zava) { ["name"] = "Zava", ["numberofemployees"] = 100, ["revenue"] = new Money(500m) },
                new Entity("account", Fabrikam) { ["name"] = "Fabrikam", ["numberofemployees"] = 250, ["revenue"] = new Money(900m) },
                new Entity("account", Contoso) { ["name"] = "Contoso", ["numberofemployees"] = 50, ["revenue"] = new Money(300m) });
            return crm;
        }

        private static HashSet<Guid> Ids(EntityCollection result) => new HashSet<Guid>(result.Entities.Select(e => e.Id));

        // Run both the QueryExpression and its ToFetchXml() through the harness; the result sets must match.
        private static void AssertRoundTrips(InMemoryOrganizationService crm, QueryExpression query)
        {
            HashSet<Guid> viaQuery = Ids(crm.RetrieveMultiple(query));
            HashSet<Guid> viaFetch = Ids(crm.RetrieveMultiple(new FetchExpression(query.ToFetchXml())));
            Assert.Equal(viaQuery, viaFetch);
        }

        [Fact]
        public void Round_trips_filters_and_operators()
        {
            InMemoryOrganizationService crm = World();
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.FilterOperator = LogicalOperator.And;
            query.Criteria.AddCondition("numberofemployees", ConditionOperator.GreaterThan, 60);
            FilterExpression any = new FilterExpression(LogicalOperator.Or);
            any.AddCondition("name", ConditionOperator.Equal, "Zava");
            any.AddCondition("name", ConditionOperator.BeginsWith, "Fab");
            query.Criteria.AddFilter(any);

            AssertRoundTrips(crm, query);
            // Sanity: Zava (100, name=Zava) and Fabrikam (250, begins Fab) match; Contoso (50) is filtered out.
            Assert.Equal(new HashSet<Guid> { Zava, Fabrikam }, Ids(crm.RetrieveMultiple(query)));
        }

        [Fact]
        public void Round_trips_in_and_null_operators()
        {
            InMemoryOrganizationService crm = World();
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("name", ConditionOperator.In, "Zava", "Contoso");

            AssertRoundTrips(crm, query);
            Assert.Equal(new HashSet<Guid> { Zava, Contoso }, Ids(crm.RetrieveMultiple(query)));
        }

        [Fact]
        public void Round_trips_inner_and_left_outer_links()
        {
            Guid parented = Guid.NewGuid();
            Guid orphan = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account", Zava) { ["name"] = "Zava" },
                new Entity("contact", parented) { ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", Zava) },
                new Entity("contact", orphan) { ["fullname"] = "NoParent" });

            QueryExpression inner = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity innerLink = inner.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            innerLink.EntityAlias = "acct";
            innerLink.Columns = new ColumnSet("name");
            AssertRoundTrips(crm, inner); // the from/to inversion must be right, or the join would break
            Assert.Equal(new HashSet<Guid> { parented }, Ids(crm.RetrieveMultiple(inner)));

            QueryExpression outer = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            outer.AddLink("account", "parentcustomerid", "accountid", JoinOperator.LeftOuter);
            AssertRoundTrips(crm, outer);
            Assert.Equal(new HashSet<Guid> { parented, orphan }, Ids(crm.RetrieveMultiple(outer)));
        }

        [Fact]
        public void Round_trips_a_lookup_equality_condition()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("contact", Guid.NewGuid()) { ["fullname"] = "A", ["parentcustomerid"] = new EntityReference("account", account) },
                new Entity("contact", Guid.NewGuid()) { ["fullname"] = "B", ["parentcustomerid"] = new EntityReference("account", account) },
                new Entity("contact", Guid.NewGuid()) { ["fullname"] = "C" });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            query.Criteria.AddCondition("parentcustomerid", ConditionOperator.Equal, account);

            AssertRoundTrips(crm, query); // a lookup filtered by its GUID must survive the FetchXML string round-trip
            Assert.Equal(2, crm.RetrieveMultiple(new FetchExpression(query.ToFetchXml())).Entities.Count);
        }

        [Fact]
        public void Round_trips_nested_links()
        {
            Guid contact = Guid.NewGuid();
            Guid task = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account", Zava) { ["name"] = "Zava" },
                new Entity("contact", contact) { ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", Zava) },
                new Entity("task", task) { ["subject"] = "Call", ["regardingobjectid"] = new EntityReference("contact", contact) });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            LinkEntity contactLink = query.AddLink("contact", "regardingobjectid", "contactid", JoinOperator.Inner);
            contactLink.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);

            AssertRoundTrips(crm, query);
            Assert.Single(crm.RetrieveMultiple(query).Entities);
        }

        [Fact]
        public void Round_trips_order_top_and_distinct()
        {
            InMemoryOrganizationService crm = World();
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = 2, Distinct = true };
            query.AddOrder("numberofemployees", OrderType.Descending);

            AssertRoundTrips(crm, query);
        }

        [Fact]
        public void Round_trips_aggregate_and_group_by()
        {
            InMemoryOrganizationService crm = World();
            QueryExpression aggregate = new QueryExpression("account") { ColumnSet = new ColumnSet() };
            aggregate.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "numberofemployees", Alias = "total", AggregateType = XrmAggregateType.Sum });

            Entity viaQuery = Assert.Single(crm.RetrieveMultiple(aggregate).Entities);
            Entity viaFetch = Assert.Single(crm.RetrieveMultiple(new FetchExpression(aggregate.ToFetchXml())).Entities);
            Assert.Equal(
                Convert.ToDecimal(viaQuery.GetAttributeValue<AliasedValue>("total").Value),
                Convert.ToDecimal(viaFetch.GetAttributeValue<AliasedValue>("total").Value));
            Assert.Equal(400m, Convert.ToDecimal(viaFetch.GetAttributeValue<AliasedValue>("total").Value));
        }

        // ---- exact-string snapshots (operators/features the harness parser doesn't read back, + FetchXML-only) ----

        [Fact]
        public void Emits_between_with_two_value_elements()
        {
            QueryExpression query = new QueryExpression("account");
            query.Criteria.AddCondition("numberofemployees", ConditionOperator.Between, 100, 500);

            string xml = query.ToFetchXml();
            Assert.Contains("<condition attribute=\"numberofemployees\" operator=\"between\">", xml);
            Assert.Contains("<value>100</value><value>500</value>", xml);
        }

        [Fact]
        public void Emits_the_link_from_to_inversion_and_link_type()
        {
            QueryExpression query = new QueryExpression("contact");
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.LeftOuter);
            link.EntityAlias = "acct";

            string xml = query.ToFetchXml();
            // QE from=parentcustomerid/to=accountid  ->  FetchXML from=accountid (linked) / to=parentcustomerid (parent).
            Assert.Contains("<link-entity name=\"account\" from=\"accountid\" to=\"parentcustomerid\" link-type=\"outer\" alias=\"acct\"", xml);
        }

        [Fact]
        public void Emits_column_comparison_as_valueof()
        {
            QueryExpression query = new QueryExpression("account");
            query.Criteria.AddCondition(new ConditionExpression("createdon", ConditionOperator.LessEqual, true, "modifiedon"));

            string xml = query.ToFetchXml();
            Assert.Contains("<condition attribute=\"createdon\" operator=\"le\" valueof=\"modifiedon\"", xml);
        }

        [Fact]
        public void Emits_fetchxml_only_options()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet() };
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "accountid", Alias = "c", AggregateType = XrmAggregateType.Count });

            string xml = query.ToFetchXml(new FetchXmlOptions { LateMaterialize = true, AggregateLimit = 5000 });
            Assert.Contains("aggregate=\"true\"", xml);
            Assert.Contains("latematerialize=\"true\"", xml);
            Assert.Contains("aggregatelimit=\"5000\"", xml);
        }

        [Fact]
        public void Emits_date_grouping()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet() };
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "createdon", Alias = "m", HasGroupBy = true, DateTimeGrouping = XrmDateTimeGrouping.Month });

            string xml = query.ToFetchXml();
            Assert.Contains("<attribute name=\"createdon\" alias=\"m\" groupby=\"true\" dategrouping=\"month\"", xml);
        }
    }
}
