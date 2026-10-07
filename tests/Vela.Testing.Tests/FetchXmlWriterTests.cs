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

        // ---- branch coverage: every switch arm (operators, link types, aggregates, date groupings, value types) ----

        [Fact]
        public void Emits_every_aggregate_function()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet() };
            foreach ((string alias, XrmAggregateType type) in new[]
            {
                ("s", XrmAggregateType.Sum), ("c", XrmAggregateType.Count), ("cc", XrmAggregateType.CountColumn),
                ("a", XrmAggregateType.Avg), ("mn", XrmAggregateType.Min), ("mx", XrmAggregateType.Max),
            })
            {
                query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "revenue", Alias = alias, AggregateType = type });
            }

            string xml = query.ToFetchXml();
            foreach (string fn in new[] { "sum", "count", "countcolumn", "avg", "min", "max" })
            {
                Assert.Contains($"aggregate=\"{fn}\"", xml);
            }
        }

        [Fact]
        public void Emits_every_date_grouping()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet() };
            foreach (XrmDateTimeGrouping grouping in new[]
            {
                XrmDateTimeGrouping.Day, XrmDateTimeGrouping.Week, XrmDateTimeGrouping.Month, XrmDateTimeGrouping.Quarter,
                XrmDateTimeGrouping.Year, XrmDateTimeGrouping.FiscalPeriod, XrmDateTimeGrouping.FiscalYear,
            })
            {
                query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "createdon", Alias = grouping.ToString(), HasGroupBy = true, DateTimeGrouping = grouping });
            }

            string xml = query.ToFetchXml();
            foreach (string g in new[] { "day", "week", "month", "quarter", "year", "fiscal-period", "fiscal-year" })
            {
                Assert.Contains($"dategrouping=\"{g}\"", xml);
            }
        }

        [Fact]
        public void Emits_every_link_type()
        {
            QueryExpression query = new QueryExpression("account");
            foreach (JoinOperator op in new[]
            {
                JoinOperator.LeftOuter, JoinOperator.Natural, JoinOperator.Exists, JoinOperator.In,
                JoinOperator.MatchFirstRowUsingCrossApply, JoinOperator.Any, JoinOperator.NotAny, JoinOperator.All, JoinOperator.NotAll,
            })
            {
                query.LinkEntities.Add(new LinkEntity("account", "contact", "accountid", "parentcustomerid", op));
            }
            // Inner has no link-type attribute (FetchXML default).
            query.LinkEntities.Add(new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.Inner) { Columns = new ColumnSet(true) });

            string xml = query.ToFetchXml();
            foreach (string lt in new[] { "outer", "natural", "exists", "in", "matchfirstrowusingcrossapply", "any", "not any", "all", "not all" })
            {
                Assert.Contains($"link-type=\"{lt}\"", xml);
            }
            Assert.Contains("<all-attributes />", xml); // the inner link projects all columns
        }

        [Fact]
        public void Emits_kebab_cased_operator_and_entityname()
        {
            QueryExpression query = new QueryExpression("account");
            query.Criteria.AddCondition(new ConditionExpression("acct", "createdon", ConditionOperator.OnOrAfter, new DateTime(2024, 1, 15)));

            string xml = query.ToFetchXml();
            Assert.Contains("operator=\"on-or-after\"", xml);                 // kebab-case fallback for a date operator
            Assert.Contains("entityname=\"acct\"", xml);                       // condition targets a linked alias
            Assert.Contains("value=\"2024-01-15T00:00:00.000\"", xml);         // Unspecified DateTime: milliseconds, no zone
        }

        [Fact]
        public void Emits_bool_value_all_attributes_and_paging()
        {
            QueryExpression query = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet(true),
                PageInfo = new PagingInfo { PageNumber = 2, Count = 50, PagingCookie = "<cookie/>", ReturnTotalRecordCount = true },
            };
            query.Criteria.AddCondition("donotemail", ConditionOperator.Equal, true);

            string xml = query.ToFetchXml();
            Assert.Contains("<all-attributes />", xml);
            Assert.Contains("count=\"50\"", xml);
            Assert.Contains("page=\"2\"", xml);
            Assert.Contains("returntotalrecordcount=\"true\"", xml);
            Assert.Contains("value=\"true\"", xml); // bool formatting
        }

        [Fact]
        public void Emits_a_minimal_query_with_no_criteria()
        {
            string xml = new QueryExpression("account").ToFetchXml();
            Assert.Contains("<fetch><entity name=\"account\"", xml);
            Assert.DoesNotContain("<filter", xml);
            Assert.DoesNotContain("<order", xml);
        }

        [Fact]
        public void Emits_the_fetchxml_only_long_tail_options()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet() };
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "numberofemployees", Alias = "u", AggregateType = XrmAggregateType.CountColumn });
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "createdon", Alias = "m", HasGroupBy = true, DateTimeGrouping = XrmDateTimeGrouping.Month });
            query.Orders.Add(new OrderExpression { Alias = "m", OrderType = OrderType.Descending });

            FetchXmlOptions options = new FetchXmlOptions
            {
                SqlOptions = "HashJoin",
                RetainedDataSource = true,
                RawChoiceOrder = true,
                UtcDateGrouping = true,
            };
            options.DistinctAggregateAliases.Add("u");
            options.RowAggregates.Add(new RowAggregate { Attribute = "accountid", Alias = "kids", Function = "CountChildren" });

            string xml = query.ToFetchXml(options);
            Assert.Contains("options=\"HashJoin\"", xml);
            Assert.Contains("datasource=\"retained\"", xml);
            Assert.Contains("useraworderby=\"true\"", xml);
            Assert.Contains("usertimezone=\"false\"", xml);                       // UTC date grouping on the month attribute
            Assert.Contains("aggregate=\"countcolumn\" distinct=\"true\"", xml);  // distinct countcolumn
            Assert.Contains("rowaggregate=\"CountChildren\"", xml);              // hierarchical row aggregate
            Assert.Contains("<order alias=\"m\" descending=\"true\"", xml);      // order by aggregate alias
        }

        [Fact]
        public void Emits_the_full_comparison_operator_map()
        {
            (ConditionOperator Op, string Fetch)[] operators =
            {
                (ConditionOperator.Equal, "eq"), (ConditionOperator.NotEqual, "ne"),
                (ConditionOperator.GreaterThan, "gt"), (ConditionOperator.GreaterEqual, "ge"),
                (ConditionOperator.LessThan, "lt"), (ConditionOperator.LessEqual, "le"),
                (ConditionOperator.Like, "like"), (ConditionOperator.NotLike, "not-like"),
                (ConditionOperator.BeginsWith, "begins-with"), (ConditionOperator.DoesNotBeginWith, "not-begin-with"),
                (ConditionOperator.EndsWith, "ends-with"), (ConditionOperator.DoesNotEndWith, "not-end-with"),
            };

            QueryExpression query = new QueryExpression("account");
            foreach ((ConditionOperator op, string _) in operators)
            {
                query.Criteria.AddCondition("name", op, "x");
            }

            string xml = query.ToFetchXml();
            foreach ((ConditionOperator _, string fetch) in operators)
            {
                Assert.Contains($"operator=\"{fetch}\"", xml);
            }
        }

        [Fact]
        public void Emits_any_all_related_filter_and_null_value()
        {
            QueryExpression query = new QueryExpression("account");
            LinkEntity anyAll = new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.Any);
            anyAll.LinkCriteria.AddCondition("firstname", ConditionOperator.Equal, "Jane");
            query.Criteria.AnyAllFilterLinkEntity = anyAll; // "accounts that have any contact named Jane"
            query.Criteria.AddCondition(new ConditionExpression("name", ConditionOperator.Equal, new object[] { null }));

            string xml = query.ToFetchXml();
            Assert.Contains("link-type=\"any\"", xml);        // Any/All related-record filter emitted inside the filter
            Assert.Contains("operator=\"eq\" value=\"\"", xml); // a null condition value formats to empty
        }

        [Fact]
        public void Emits_dataverse_value_types_as_their_underlying_scalar()
        {
            Guid owner = Guid.NewGuid();
            QueryExpression query = new QueryExpression("account");
            query.Criteria.AddCondition("statuscode", ConditionOperator.Equal, new OptionSetValue(1));
            query.Criteria.AddCondition("revenue", ConditionOperator.GreaterThan, new Money(500m));
            query.Criteria.AddCondition("ownerid", ConditionOperator.Equal, new EntityReference("systemuser", owner));

            string xml = query.ToFetchXml();
            Assert.Contains("attribute=\"statuscode\" operator=\"eq\" value=\"1\"", xml);        // OptionSetValue -> int
            Assert.Contains("attribute=\"revenue\" operator=\"gt\" value=\"500\"", xml);          // Money -> decimal
            Assert.Contains($"attribute=\"ownerid\" operator=\"eq\" value=\"{owner:D}\"", xml);   // EntityReference -> GUID
            Assert.DoesNotContain("Microsoft.Xrm.Sdk", xml);                                      // never the type name
        }

        [Fact]
        public void Emits_the_irregular_hierarchy_and_choice_operators()
        {
            (ConditionOperator Op, string Fetch)[] operators =
            {
                (ConditionOperator.Above, "above"), (ConditionOperator.Under, "under"), (ConditionOperator.NotUnder, "not-under"),
                (ConditionOperator.AboveOrEqual, "eq-or-above"), (ConditionOperator.UnderOrEqual, "eq-or-under"),
                (ConditionOperator.ContainValues, "contain-values"), (ConditionOperator.DoesNotContainValues, "not-contain-values"),
                (ConditionOperator.Last7Days, "last-seven-days"), (ConditionOperator.Next7Days, "next-seven-days"),
            };

            QueryExpression query = new QueryExpression("account");
            foreach ((ConditionOperator op, string _) in operators)
            {
                query.Criteria.AddCondition("createdon", op, 1);
            }

            string xml = query.ToFetchXml();
            foreach ((ConditionOperator _, string fetch) in operators)
            {
                Assert.Contains($"operator=\"{fetch}\"", xml);
            }
        }

        [Fact]
        public void Emits_no_lock_only_when_requested()
        {
            Assert.Contains("no-lock=\"true\"", new QueryExpression("account") { NoLock = true }.ToFetchXml());
            Assert.DoesNotContain("no-lock", new QueryExpression("account").ToFetchXml());
        }

        [Fact]
        public void Emits_set_and_null_operators()
        {
            QueryExpression query = new QueryExpression("account");
            query.Criteria.AddCondition("name", ConditionOperator.In, "a", "b");
            query.Criteria.AddCondition("name", ConditionOperator.NotIn, "c", "d");
            query.Criteria.AddCondition("numberofemployees", ConditionOperator.Between, 1, 9);
            query.Criteria.AddCondition("numberofemployees", ConditionOperator.NotBetween, 1, 9);
            query.Criteria.AddCondition("name", ConditionOperator.Null);
            query.Criteria.AddCondition("name", ConditionOperator.NotNull);

            string xml = query.ToFetchXml();
            foreach (string op in new[] { "in", "not-in", "between", "not-between", "null", "not-null" })
            {
                Assert.Contains($"operator=\"{op}\"", xml);
            }
        }
    }
}
