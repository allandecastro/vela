using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class InMemoryQueryEngineCoverageTests
    {
        // Alpha (100), Bravo (200), Charlie (no employee count -> null).
        private static InMemoryOrganizationService Numbered()
        {
            return new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Alpha", ["numberofemployees"] = 100 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Bravo", ["numberofemployees"] = 200 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Charlie" });
        }

        [Theory]
        [InlineData(ConditionOperator.GreaterThan, 150, 1)]  // Bravo (200)
        [InlineData(ConditionOperator.GreaterEqual, 100, 2)] // Alpha, Bravo
        [InlineData(ConditionOperator.LessThan, 200, 1)]     // Alpha (100)
        [InlineData(ConditionOperator.LessEqual, 200, 2)]    // Alpha, Bravo
        public void Numeric_relational_operators_skip_the_null_row(ConditionOperator op, int value, int expected)
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("numberofemployees", op, value);

            Assert.Equal(expected, Numbered().RetrieveMultiple(query).Entities.Count);
        }

        [Fact]
        public void Between_filters_the_range_and_skips_the_null_row()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("numberofemployees", ConditionOperator.Between, 100, 200);

            Assert.Equal(2, Numbered().RetrieveMultiple(query).Entities.Count); // Alpha, Bravo; Charlie (null) skipped
        }

        [Fact]
        public void FetchXml_numeric_comparison_handles_a_null_row()
        {
            // Regression: a null attribute used to throw "cannot compare the supplied values" on gt/lt.
            string fetch = "<fetch><entity name='account'><attribute name='name' /><filter><condition attribute='numberofemployees' operator='gt' value='150' /></filter></entity></fetch>";

            Entity match = Assert.Single(Numbered().RetrieveMultiple(new FetchExpression(fetch)).Entities);
            Assert.Equal("Bravo", match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Native_aggregates_cover_count_countcolumn_avg_min_max()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["estimatedvalue"] = new Money(100m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["estimatedvalue"] = new Money(50m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["estimatedvalue"] = new Money(30m) });

            QueryExpression query = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet
                {
                    AttributeExpressions =
                    {
                        new XrmAttributeExpression { AttributeName = "opportunityid", Alias = "cnt", AggregateType = XrmAggregateType.Count },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "cntcol", AggregateType = XrmAggregateType.CountColumn },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "average", AggregateType = XrmAggregateType.Avg },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "lowest", AggregateType = XrmAggregateType.Min },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "highest", AggregateType = XrmAggregateType.Max }
                    }
                }
            };

            Entity agg = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal(3, agg.GetAttributeValue<AliasedValue>("cnt").Value);
            Assert.Equal(3, agg.GetAttributeValue<AliasedValue>("cntcol").Value);
            Assert.Equal(60m, agg.GetAttributeValue<AliasedValue>("average").Value);
            // min/max preserve the source type (Money); avg/count are unwrapped.
            Assert.Equal(30m, ((Money)agg.GetAttributeValue<AliasedValue>("lowest").Value).Value);
            Assert.Equal(100m, ((Money)agg.GetAttributeValue<AliasedValue>("highest").Value).Value);
        }

        [Fact]
        public void Avg_over_a_column_with_no_values_is_null_like_Dataverse()
        {
            // Rows exist but none carry estimatedvalue -> avg/min/max are null (count still counts the rows).
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["name"] = "A" },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["name"] = "B" });

            QueryExpression query = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet
                {
                    AttributeExpressions =
                    {
                        new XrmAttributeExpression { AttributeName = "opportunityid", Alias = "cnt", AggregateType = XrmAggregateType.Count },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "average", AggregateType = XrmAggregateType.Avg },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "lowest", AggregateType = XrmAggregateType.Min },
                    }
                }
            };

            Entity agg = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal(2, agg.GetAttributeValue<AliasedValue>("cnt").Value);
            Assert.Null(agg.GetAttributeValue<AliasedValue>("average").Value); // was 0m before the fix
            Assert.Null(agg.GetAttributeValue<AliasedValue>("lowest").Value);
        }
    }
}
