using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Context;
using Vela.Querying;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers the typed query/attribute helpers and the tracing service decorator.
public sealed class PluginHelpersCoverageTests
{
    // A linked entity type WITHOUT [EntityLogicalName] so the logical-name fallback (type name) is exercised.
    private sealed class PlainLink : Entity
    {
        public PlainLink() : base("plainlink") { }

        public Guid? Ref
        {
            get => GetAttributeValue<Guid?>("ref");
            set => SetAttributeValue("ref", value);
        }
    }

    // ---- QueryExpressionExtensions -------------------------------------------------------------------

    [Fact]
    public void Select_Where_OrderBy_build_typed_query_parts()
    {
        QueryExpression query = new QueryExpression("account")
            .Select<TestAccount>(a => a.Name, a => a.AccountId)
            .Where<TestAccount>(a => a.Name, ConditionOperator.Equal, "Acme")
            .OrderBy<TestAccount>(a => a.Name, OrderType.Descending);

        Assert.Equal(new[] { "name", "accountid" }, query.ColumnSet.Columns.ToArray());
        ConditionExpression condition = Assert.Single(query.Criteria.Conditions);
        Assert.Equal("name", condition.AttributeName);
        OrderExpression order = Assert.Single(query.Orders);
        Assert.Equal(OrderType.Descending, order.OrderType);
    }

    [Fact]
    public void Select_with_no_columns_yields_an_empty_column_set()
    {
        QueryExpression query = new QueryExpression("account").Select<TestAccount>();
        Assert.False(query.ColumnSet.AllColumns);
        Assert.Empty(query.ColumnSet.Columns);
    }

    [Fact]
    public void WhereAll_and_WhereAny_add_nested_filters_with_conditions()
    {
        QueryExpression query = new QueryExpression("account")
            .WhereAll<TestAccount>(f => f
                .Condition(a => a.Name, ConditionOperator.Equal, "Acme")
                .All(inner => inner.Condition(a => a.AccountId, ConditionOperator.NotNull)))
            .WhereAny<TestAccount>(f => f
                .Condition(a => a.Name, ConditionOperator.BeginsWith, "A")
                .Any(inner => inner.Condition(a => a.Name, ConditionOperator.EndsWith, "e")));

        FilterExpression all = query.Criteria.Filters[0];
        Assert.Equal(LogicalOperator.And, all.FilterOperator);
        Assert.Single(all.Conditions);
        Assert.Single(all.Filters); // nested All
        FilterExpression any = query.Criteria.Filters[1];
        Assert.Equal(LogicalOperator.Or, any.FilterOperator);
        Assert.Single(any.Filters); // nested Any
    }

    [Fact]
    public void Link_maps_entity_and_attribute_names_with_alias_and_columns()
    {
        QueryExpression query = new QueryExpression("account");
        LinkEntity link = query.Link<TestAccount, PlainLink>(
            a => a.AccountId, t => t.Ref, JoinOperator.LeftOuter, "l", t => t.Ref);

        Assert.Equal("plainlink", link.LinkToEntityName); // [EntityLogicalName]-less fallback to type name
        Assert.Equal("accountid", link.LinkFromAttributeName);
        Assert.Equal("ref", link.LinkToAttributeName);
        Assert.Equal("l", link.EntityAlias);
        Assert.Equal(new[] { "ref" }, link.Columns.Columns.ToArray());
    }

    [Fact]
    public void Typed_query_methods_reject_null_arguments()
    {
        QueryExpression query = new QueryExpression("account");
        Assert.Throws<ArgumentNullException>(() => ((QueryExpression)null!).Select<TestAccount>(a => a.Name));
        Assert.Throws<ArgumentNullException>(() => ((QueryExpression)null!).Where<TestAccount>(a => a.Name, ConditionOperator.Equal, "x"));
        Assert.Throws<ArgumentNullException>(() => ((QueryExpression)null!).OrderBy<TestAccount>(a => a.Name));
        Assert.Throws<ArgumentNullException>(() => query.WhereAll<TestAccount>(null!));
        Assert.Throws<ArgumentNullException>(() => query.WhereAny<TestAccount>(null!));
        Assert.Throws<ArgumentNullException>(() => ((QueryExpression)null!).Link<TestAccount, PlainLink>(a => a.AccountId, t => t.Ref));
    }

    [Fact]
    public void Typed_column_expression_must_target_a_property()
    {
        QueryExpression query = new QueryExpression("account");
        Assert.Throws<ArgumentException>(() => query.Where<TestAccount>(a => "literal", ConditionOperator.Equal, "x"));
    }

    // ---- EntityAttributeExtensions -------------------------------------------------------------------

    [Fact]
    public void Contains_single_checks_attribute_presence()
    {
        TestAccount account = new TestAccount { Name = "Acme" };
        Assert.True(account.Contains(a => a.Name));
        Assert.False(account.Contains(a => a.AccountId));
        Assert.Throws<ArgumentNullException>(() => ((TestAccount)null!).Contains(a => a.Name));
    }

    [Fact]
    public void Contains_all_requires_every_attribute()
    {
        TestAccount account = new TestAccount { Name = "Acme", AccountId = Guid.NewGuid() };
        Assert.True(account.Contains(a => a.Name, a => a.AccountId));
        Assert.False(account.Contains(a => a.Name, a => a.Customers));

        Assert.Throws<ArgumentNullException>(() => account.Contains((System.Linq.Expressions.Expression<Func<TestAccount, object?>>[])null!));
        Assert.Throws<ArgumentException>(() => account.Contains(Array.Empty<System.Linq.Expressions.Expression<Func<TestAccount, object?>>>()));
    }

    [Fact]
    public void ContainsAny_requires_at_least_one_attribute_present()
    {
        TestAccount account = new TestAccount { Name = "Acme" };
        Assert.True(account.ContainsAny(a => a.AccountId, a => a.Name));
        Assert.False(account.ContainsAny(a => a.AccountId, a => a.Customers));
        Assert.Throws<ArgumentException>(() => account.ContainsAny(Array.Empty<System.Linq.Expressions.Expression<Func<TestAccount, object?>>>()));
    }

    [Fact]
    public void GetFormattedValue_returns_the_label_or_null()
    {
        TestAccount account = new TestAccount();
        account.FormattedValues["name"] = "Acme Corp";
        Assert.Equal("Acme Corp", account.GetFormattedValue(a => a.Name));
        Assert.Null(account.GetFormattedValue(a => a.AccountId));
    }

    // ---- TracingOrganizationService ------------------------------------------------------------------

    private static Mock<ILocalPluginContext> TraceContext()
    {
        Mock<ILocalPluginContext> ctx = new Mock<ILocalPluginContext>();
        ctx.SetupGet(c => c.MessageName).Returns("Update");
        ctx.SetupGet(c => c.Stage).Returns(40);
        ctx.SetupGet(c => c.Mode).Returns(0);
        ctx.SetupGet(c => c.Depth).Returns(1);
        ctx.SetupGet(c => c.CorrelationId).Returns(Guid.NewGuid());
        return ctx;
    }

    [Fact]
    public void Tracing_service_rejects_null_inner_and_context()
    {
        Assert.Throws<ArgumentNullException>(() => new TracingOrganizationService(null!, TraceContext().Object, "S"));
        Assert.Throws<ArgumentNullException>(() => new TracingOrganizationService(Mock.Of<IOrganizationService>(), null!, "S"));
    }

    [Fact]
    public void Tracing_service_delegates_every_operation_and_traces_start_and_ok()
    {
        Mock<ILocalPluginContext> ctx = TraceContext();
        List<string> traces = new List<string>();
        ctx.Setup(c => c.Trace(It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, object[]>((fmt, args) => traces.Add(string.Format(fmt, args)));

        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        inner.Setup(s => s.Create(It.IsAny<Entity>())).Returns(id);
        inner.Setup(s => s.Retrieve("account", id, It.IsAny<ColumnSet>())).Returns(new Entity("account", id));
        inner.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>())).Returns(new EntityCollection());
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());

        // scopeName null -> defaults to "Service".
        TracingOrganizationService svc = new TracingOrganizationService(inner.Object, ctx.Object, null!);

        Assert.Equal(id, svc.Create(new Entity("account")));
        Assert.Equal(id, svc.Retrieve("account", id, new ColumnSet()).Id);
        svc.Update(new Entity("account", id));
        svc.Delete("account", id);
        svc.Execute(new OrganizationRequest("WhoAmI"));
        svc.Associate("account", id, new Relationship("r"), new EntityReferenceCollection());
        svc.Disassociate("account", id, new Relationship("r"), new EntityReferenceCollection());
        svc.RetrieveMultiple(new QueryExpression("account") { TopCount = 5 });
        svc.RetrieveMultiple(new QueryByAttribute("account")); // non-QueryExpression details branch

        inner.Verify(s => s.Update(It.IsAny<Entity>()), Times.Once);
        inner.Verify(s => s.Delete("account", id), Times.Once);
        Assert.Contains(traces, t => t.Contains("Dataverse.Create start") && t.Contains("Service"));
        Assert.Contains(traces, t => t.Contains("Dataverse.Create ok"));
    }

    [Fact]
    public void Tracing_service_traces_and_rethrows_on_failure()
    {
        Mock<ILocalPluginContext> ctx = TraceContext();
        List<string> traces = new List<string>();
        ctx.Setup(c => c.Trace(It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, object[]>((fmt, args) => traces.Add(string.Format(fmt, args)));

        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        inner.Setup(s => s.Create(It.IsAny<Entity>())).Throws(new InvalidOperationException("boom"));
        TracingOrganizationService svc = new TracingOrganizationService(inner.Object, ctx.Object, "Custom");

        Assert.Throws<InvalidOperationException>(() => svc.Create(new Entity("account")));
        // The exception type only: a fault message can echo record data (the exception itself still propagates).
        Assert.Contains(traces, t => t.Contains("Dataverse.Create fail") && t.Contains("InvalidOperationException") && !t.Contains("boom"));
    }

    [Fact]
    public void Tracing_service_handles_a_null_request_name()
    {
        Mock<ILocalPluginContext> ctx = TraceContext();
        ctx.Setup(c => c.Trace(It.IsAny<string>(), It.IsAny<object[]>()));
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());

        TracingOrganizationService svc = new TracingOrganizationService(inner.Object, ctx.Object, "S");
        Assert.NotNull(svc.Execute(new OrganizationRequest())); // RequestName null -> "UnknownRequest"
    }
}
