using System;
using Vela.Querying;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Tests
{
    public sealed class QueryFactoryTests
    {
        [EntityLogicalName("contact")]
        private sealed class TestContact : Entity
        {
            public TestContact() : base("contact") { }

            [AttributeLogicalName("fullname")]
            public string FullName
            {
                get => GetAttributeValue<string>("fullname");
                set => SetAttributeValue("fullname", value);
            }

            [AttributeLogicalName("parentcustomerid")]
            public EntityReference ParentCustomerId
            {
                get => GetAttributeValue<EntityReference>("parentcustomerid");
                set => SetAttributeValue("parentcustomerid", value);
            }
        }

        [Fact]
        public void For_DerivesEntityName_AndConvertsImplicitlyToQueryExpression()
        {
            QueryExpression query = Query.For<TestAccount>(); // implicit conversion
            Assert.Equal(TestAccount.EntityLogicalNameValue, query.EntityName);
        }

        [Fact]
        public void Select_And_SelectAll_SetColumns()
        {
            Assert.Contains("name", Query.For<TestAccount>().Select(a => a.Name).Expression.ColumnSet.Columns);
            Assert.True(Query.For<TestAccount>().SelectAll().Expression.ColumnSet.AllColumns);
        }

        [Fact]
        public void Chains_WithoutRepeatingTheEntityType()
        {
            QueryExpression query = Query.For<TestAccount>()
                .Where(a => a.Name, ConditionOperator.Equal, "Zava")
                .OrderBy(a => a.Name);

            Assert.Single(query.Criteria.Conditions);
            Assert.Equal("name", query.Criteria.Conditions[0].AttributeName);
            Assert.Single(query.Orders);
        }

        [Fact]
        public void Link_IsTyped_WithColumnsAndCriteria()
        {
            QueryExpression query = Query.For<TestAccount>()
                .Link<TestContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.Inner, "c",
                    link => link.Select(c => c.FullName).Where(c => c.FullName, ConditionOperator.NotNull));

            LinkEntity link = Assert.Single(query.LinkEntities);
            Assert.Equal("contact", link.LinkToEntityName);
            Assert.Equal("accountid", link.LinkFromAttributeName);
            Assert.Equal("parentcustomerid", link.LinkToAttributeName);
            Assert.Equal("c", link.EntityAlias);
            Assert.Contains("fullname", link.Columns.Columns);
            Assert.Single(link.LinkCriteria.Conditions);
        }

        [Fact]
        public void Link_SupportsNestedLinks()
        {
            QueryExpression query = Query.For<TestAccount>()
                .Link<TestContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.Inner, "c",
                    link => link.Link<TestAccount>(c => c.ParentCustomerId, a => a.AccountId, JoinOperator.LeftOuter, "a2"));

            LinkEntity contactLink = Assert.Single(query.LinkEntities);
            LinkEntity nested = Assert.Single(contactLink.LinkEntities);
            Assert.Equal("account", nested.LinkToEntityName);
            Assert.Equal("a2", nested.EntityAlias);
        }

        [Fact]
        public void Distinct_Page_TotalRecordCount_SetTheFlags()
        {
            QueryExpression query = Query.For<TestAccount>().Distinct().Page(2, 50).WithTotalRecordCount();

            Assert.True(query.Distinct);
            Assert.Equal(2, query.PageInfo.PageNumber);
            Assert.Equal(50, query.PageInfo.Count);
            Assert.True(query.PageInfo.ReturnTotalRecordCount);
        }

        [Fact]
        public void Aggregates_BuildAttributeExpressions()
        {
            QueryExpression query = Query.For<TestAccount>()
                .GroupBy(a => a.Name, "n")
                .Sum(a => a.AccountId, "x");

            Assert.Equal(2, query.ColumnSet.AttributeExpressions.Count);
            Assert.True(query.ColumnSet.AttributeExpressions[0].HasGroupBy);
            Assert.Equal("name", query.ColumnSet.AttributeExpressions[0].AttributeName);
            Assert.Equal(XrmAggregateType.Sum, query.ColumnSet.AttributeExpressions[1].AggregateType);
            Assert.Equal("x", query.ColumnSet.AttributeExpressions[1].Alias);
        }

        [Fact]
        public void WhereColumns_SetsCompareColumns()
        {
            QueryExpression query = Query.For<TestAccount>().WhereColumns(a => a.Name, ConditionOperator.Equal, a => a.Name);

            ConditionExpression condition = query.Criteria.Conditions[0];
            Assert.True(condition.CompareColumns);
            Assert.Equal("name", (string)condition.Values[0]);
        }

        [Fact]
        public void WhereLink_SetsConditionEntityName()
        {
            QueryExpression query = Query.For<TestAccount>()
                .Link<TestContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.LeftOuter, "c")
                .WhereLink<TestContact>("c", c => c.ParentCustomerId, ConditionOperator.Null);

            ConditionExpression condition = query.Criteria.Conditions[0];
            Assert.Equal("c", condition.EntityName);
            Assert.Equal("parentcustomerid", condition.AttributeName);
            Assert.Equal(ConditionOperator.Null, condition.Operator);
        }

        [Fact]
        public void WhereRelated_SetsAnyAllFilterLinkEntity()
        {
            QueryExpression query = Query.For<TestContact>()
                .WhereRelated<TestAccount>(c => c.ParentCustomerId, a => a.AccountId, JoinOperator.Any,
                    link => link.Where(a => a.Name, ConditionOperator.Equal, "Zava"));

            Assert.NotNull(query.Criteria.Filters[0].AnyAllFilterLinkEntity);
            Assert.Equal(JoinOperator.Any, query.Criteria.Filters[0].AnyAllFilterLinkEntity.JoinOperator);
            Assert.Single(query.Criteria.Filters[0].AnyAllFilterLinkEntity.LinkCriteria.Conditions);
        }

        [Fact]
        public void Select_after_aggregates_keeps_them()
        {
            QueryExpression query = Query.For<TestAccount>()
                .GroupBy(a => a.Name, "n")
                .Count(a => a.AccountId, "c")
                .Select(a => a.Name);

            Assert.Equal(2, query.ColumnSet.AttributeExpressions.Count);
            Assert.Equal("c", query.ColumnSet.AttributeExpressions[1].Alias);
            Assert.Contains("name", query.ColumnSet.Columns);
        }

        [Fact]
        public void SelectAll_after_aggregates_keeps_them()
        {
            QueryExpression query = Query.For<TestAccount>().GroupBy(a => a.Name, "n").SelectAll();

            Assert.Single(query.ColumnSet.AttributeExpressions);
            Assert.True(query.ColumnSet.AllColumns);
        }
    }
}
