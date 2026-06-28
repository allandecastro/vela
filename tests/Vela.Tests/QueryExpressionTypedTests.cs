using Vela.Querying;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Tests;

public sealed class QueryExpressionTypedTests
{
    [Fact]
    public void Select_BuildsTypedColumns()
    {
        QueryExpression query = new QueryExpression(TestAccount.EntityLogicalNameValue)
            .Select<TestAccount>(x => x.AccountId, x => x.Name);

        Assert.Equal(TestAccount.EntityLogicalNameValue, query.EntityName);
        Assert.Contains("accountid", query.ColumnSet.Columns);
        Assert.Contains("name", query.ColumnSet.Columns);
    }

    [Fact]
    public void Where_AddsTypedCondition()
    {
        QueryExpression query = new QueryExpression(TestAccount.EntityLogicalNameValue)
            .Select<TestAccount>(x => x.Name)
            .Where<TestAccount>(x => x.Name, ConditionOperator.Equal, "Zava");

        Assert.Single(query.Criteria.Conditions);
        Assert.Equal("name", query.Criteria.Conditions[0].AttributeName);
        Assert.Equal(ConditionOperator.Equal, query.Criteria.Conditions[0].Operator);
        Assert.Equal("Zava", query.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public void WhereAllAndAny_AddNestedTypedFilters()
    {
        QueryExpression query = new QueryExpression(TestAccount.EntityLogicalNameValue)
            .Select<TestAccount>(x => x.AccountId, x => x.Name)
            .WhereAll<TestAccount>(filter => filter
                .Condition(x => x.Name, ConditionOperator.NotNull)
                .Any(or => or
                    .Condition(x => x.Name, ConditionOperator.BeginsWith, "A")
                    .Condition(x => x.Name, ConditionOperator.BeginsWith, "B")));

        Assert.Single(query.Criteria.Filters);
        Assert.Equal(LogicalOperator.And, query.Criteria.Filters[0].FilterOperator);
        Assert.Single(query.Criteria.Filters[0].Conditions);
        Assert.Single(query.Criteria.Filters[0].Filters);
        Assert.Equal(LogicalOperator.Or, query.Criteria.Filters[0].Filters[0].FilterOperator);
        Assert.Equal(2, query.Criteria.Filters[0].Filters[0].Conditions.Count);
    }
}
