using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Vela.DI;
using Vela.Querying;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers SimpleServiceResolver lifetimes/guards and the TypedQuery / TypedLink fluent builders.
public sealed class ResolverAndTypedQueryCoverageTests
{
    private interface ITestSvc { }

    private sealed class TestSvc : ITestSvc { }

    // ---- SimpleServiceResolver -----------------------------------------------------------------------

    [Fact]
    public void CreateDefault_rejects_null_and_registers_managed_identity_only_when_present()
    {
        Assert.Throws<ArgumentNullException>(() => SimpleServiceResolver.CreateDefault(null!));

        SimpleServiceResolver without = SimpleServiceResolver.CreateDefault(new PluginContextBuilder().Build());
        Assert.NotNull(without.GetService(typeof(ITracingService)));
        Assert.Null(without.GetService(typeof(IManagedIdentityService)));

        LocalPluginContext withMi = new PluginContextBuilder()
            .WithManagedIdentityService(Moq.Mock.Of<IManagedIdentityService>())
            .Build();
        Assert.NotNull(SimpleServiceResolver.CreateDefault(withMi).GetService(typeof(IManagedIdentityService)));
    }

    [Fact]
    public void Scoped_services_are_cached_and_transient_services_are_not()
    {
        SimpleServiceResolver resolver = new SimpleServiceResolver();
        resolver.AddScoped<ITestSvc>(_ => new TestSvc());
        object? first = resolver.GetService(typeof(ITestSvc));
        Assert.Same(first, resolver.GetService(typeof(ITestSvc))); // cached scoped instance

        SimpleServiceResolver transient = new SimpleServiceResolver();
        transient.AddTransient<ITestSvc>(_ => new TestSvc());
        Assert.NotSame(transient.GetService(typeof(ITestSvc)), transient.GetService(typeof(ITestSvc)));
    }

    [Fact]
    public void GetService_unregistered_returns_null_and_GetRequiredService_throws()
    {
        SimpleServiceResolver resolver = new SimpleServiceResolver();
        Assert.Null(resolver.GetService(typeof(ITestSvc)));
        Assert.Throws<InvalidPluginExecutionException>(() => resolver.GetRequiredService<ITestSvc>());
    }

    [Fact]
    public void GetService_circular_dependency_throws_clearly_instead_of_stack_overflow()
    {
        SimpleServiceResolver resolver = new SimpleServiceResolver();
        resolver.AddTransient<ITestSvc>(r =>
        {
            r.GetService(typeof(ITestSvc)); // self-resolve -> cycle
            return new TestSvc();
        });

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => resolver.GetService(typeof(ITestSvc)));
        Assert.Contains("Circular service dependency", ex.Message);
    }

    [Fact]
    public void A_factory_returning_null_throws_on_resolution()
    {
        SimpleServiceResolver resolver = new SimpleServiceResolver();
        resolver.AddScoped<ITestSvc>(_ => null!);
        Assert.Throws<InvalidPluginExecutionException>(() => resolver.GetService(typeof(ITestSvc)));
    }

    [Fact]
    public void A_throwing_factory_surfaces_the_real_exception_not_the_reflection_wrapper()
    {
        SimpleServiceResolver resolver = new SimpleServiceResolver();
        resolver.AddScoped<ITestSvc>(_ => throw new InvalidOperationException("boom"));

        // Without unwrapping, DynamicInvoke would surface a TargetInvocationException.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => resolver.GetService(typeof(ITestSvc)));
        Assert.Equal("boom", ex.Message);
    }

    // ---- TypedQuery / TypedLink ----------------------------------------------------------------------

    [Fact]
    public void TypedQuery_builds_columns_filters_orders_and_paging()
    {
        QueryExpression query = Query.For<TestAccount>()
            .Select(a => a.Name)
            .Where(a => a.Name, ConditionOperator.Equal, "Acme")
            .WhereAll(f => f.Condition(a => a.AccountId, ConditionOperator.NotNull))
            .WhereAny(f => f.Condition(a => a.Name, ConditionOperator.BeginsWith, "A"))
            .OrderBy(a => a.Name)
            .OrderByDescending(a => a.AccountId)
            .Top(10)
            .Distinct()
            .WhereColumns(a => a.Name, ConditionOperator.Equal, a => a.AccountId)
            .Build();

        Assert.Equal("account", query.EntityName);
        Assert.Equal(new[] { "name" }, query.ColumnSet.Columns.ToArray());
        Assert.True(query.Distinct);
        Assert.Equal(10, query.TopCount);
        Assert.Equal(2, query.Orders.Count);
        Assert.Equal(2, query.Criteria.Filters.Count);
    }

    [Fact]
    public void TypedQuery_SelectAll_paging_and_total_count()
    {
        TypedQuery<TestAccount> typed = Query.For<TestAccount>().SelectAll().Page(2, 25).WithTotalRecordCount();
        QueryExpression query = typed; // implicit conversion

        Assert.True(query.ColumnSet.AllColumns);
        Assert.Equal(2, query.PageInfo.PageNumber);
        Assert.True(query.PageInfo.ReturnTotalRecordCount);
    }

    [Fact]
    public void TypedQuery_links_and_typed_link_configuration()
    {
        QueryExpression query = Query.For<TestAccount>()
            .Link<TestAccount>(a => a.AccountId, c => c.AccountId, JoinOperator.LeftOuter, "l", link => link
                .Select(c => c.Name)
                .Where(c => c.Name, ConditionOperator.NotNull)
                .WhereAll(f => f.Condition(c => c.Name, ConditionOperator.NotNull))
                .WhereAny(f => f.Condition(c => c.Name, ConditionOperator.NotNull))
                .Link<TestAccount>(c => c.AccountId, d => d.AccountId, JoinOperator.Inner, "nested", n => n.SelectAll()))
            .WhereLink<TestAccount>("l", c => c.Name, ConditionOperator.Equal, "x")
            .Build();

        LinkEntity link = Assert.Single(query.LinkEntities);
        Assert.Equal("l", link.EntityAlias);
        Assert.Equal(JoinOperator.LeftOuter, link.JoinOperator);
        Assert.Single(link.LinkEntities); // nested link
    }

    [Fact]
    public void TypedQuery_where_related_and_aggregates()
    {
        QueryExpression related = Query.For<TestAccount>()
            .WhereRelated<TestAccount>(a => a.AccountId, c => c.AccountId, JoinOperator.Any, l => l.Where(c => c.Name, ConditionOperator.NotNull))
            .Build();
        Assert.NotNull(related.Criteria.AnyAllFilterLinkEntity);

        QueryExpression agg = Query.For<TestAccount>()
            .GroupBy(a => a.Name, "byname")
            .Count(a => a.AccountId, "cnt")
            .Sum(a => a.AccountId, "s")
            .Average(a => a.AccountId, "avg")
            .Min(a => a.AccountId, "mn")
            .Max(a => a.AccountId, "mx")
            .Build();
        Assert.Equal(6, agg.ColumnSet.AttributeExpressions.Count);
    }

    [Fact]
    public void TypedQuery_filter_builders_reject_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => Query.For<TestAccount>().WhereAll(null!));
        Assert.Throws<ArgumentNullException>(() => Query.For<TestAccount>().WhereAny(null!));
    }
}
