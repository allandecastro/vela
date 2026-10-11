using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Context;
using Xunit;

namespace Vela.Tests;

// Drives every TracingOrganizationService operation with null/edge arguments and checks the trace lines it writes:
// a start line with the scope, depth and metadata (the null-conditional detail builders such as entity?.LogicalName,
// relationship?.SchemaName and query?.GetType() render empty / "null"), then an "ok" line, and the call still reaches
// the inner service.
public sealed class TracingOrganizationServiceCoverageTests
{
    private static readonly Guid Id = new Guid("0a3a6c2e-4d6b-4a59-9f1e-6d2b8c1f7e21");

    private static (TracingOrganizationService Svc, Mock<IOrganizationService> Inner, List<string> Lines) Build()
    {
        List<string> lines = new List<string>();
        Mock<ILocalPluginContext> ctx = new Mock<ILocalPluginContext>();
        ctx.SetupGet(c => c.Depth).Returns(3);
        ctx.Setup(c => c.Trace(It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, object[]>((format, args) => lines.Add(string.Format(format, args)));
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        return (new TracingOrganizationService(inner.Object, ctx.Object, "S"), inner, lines);
    }

    // The start line, then exactly one "ok" line for the same operation (the elapsed ms varies, so it is matched).
    private static void AssertTraced(List<string> lines, string operation, string details)
    {
        Assert.Equal(2, lines.Count);
        Assert.Equal($"Dataverse.{operation} start scope=S depth=3 {details}", lines[0]);
        Assert.Matches(new Regex($"^Dataverse\\.{operation} ok \\d+ ms$"), lines[1]);
    }

    [Fact]
    public void Create_with_a_null_entity_traces_an_empty_table()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();

        svc.Create(null!);

        AssertTraced(lines, "Create", "entity=");
        inner.Verify(s => s.Create(null!), Times.Once);
    }

    [Fact]
    public void Update_with_a_null_entity_traces_an_empty_table_and_id()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();

        svc.Update(null!);

        AssertTraced(lines, "Update", "entity=; id=");
        inner.Verify(s => s.Update(null!), Times.Once);
    }

    [Fact]
    public void Delete_and_Retrieve_trace_the_table_and_id()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();

        svc.Delete("account", Id);
        AssertTraced(lines, "Delete", $"entity=account; id={Id}");

        lines.Clear();
        svc.Retrieve("contact", Id, new ColumnSet());
        AssertTraced(lines, "Retrieve", $"entity=contact; id={Id}");

        inner.Verify(s => s.Delete("account", Id), Times.Once);
        inner.Verify(s => s.Retrieve("contact", Id, It.IsAny<ColumnSet>()), Times.Once);
    }

    [Fact]
    public void Associate_and_Disassociate_with_a_null_relationship_trace_an_empty_schema_name()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();

        svc.Associate("account", Id, null!, new EntityReferenceCollection());
        AssertTraced(lines, "Associate", $"entity=account; id={Id}; relationship=");

        lines.Clear();
        svc.Disassociate("account", Id, new Relationship("account_contacts"), new EntityReferenceCollection());
        AssertTraced(lines, "Disassociate", $"entity=account; id={Id}; relationship=account_contacts");

        inner.Verify(s => s.Associate("account", Id, null!, It.IsAny<EntityReferenceCollection>()), Times.Once);
        inner.Verify(s => s.Disassociate("account", Id, It.Is<Relationship>(r => r.SchemaName == "account_contacts"), It.IsAny<EntityReferenceCollection>()), Times.Once);
    }

    [Fact]
    public void RetrieveMultiple_traces_each_query_kind()
    {
        (TracingOrganizationService svc, _, List<string> lines) = Build();

        svc.RetrieveMultiple(new QueryByAttribute("account"));
        AssertTraced(lines, "RetrieveMultiple", "query=QueryByAttribute; entity=account");

        lines.Clear();
        svc.RetrieveMultiple(new QueryExpression("contact") { TopCount = 5 });
        AssertTraced(lines, "RetrieveMultiple", "query=QueryExpression; entity=contact; top=5");

        lines.Clear();
        svc.RetrieveMultiple(new QueryExpression("contact"));
        AssertTraced(lines, "RetrieveMultiple", "query=QueryExpression; entity=contact; top=null");

        lines.Clear();
        svc.RetrieveMultiple(null!);
        AssertTraced(lines, "RetrieveMultiple", "query=null");
    }

    [Fact]
    public void A_failing_call_traces_the_exception_type_only_and_rethrows()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner, List<string> lines) = Build();
        inner.Setup(s => s.Delete("account", Id)).Throws(new InvalidOperationException("secret record name"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Delete("account", Id));

        Assert.Equal("secret record name", ex.Message);
        Assert.Equal(2, lines.Count);
        Assert.Matches(new Regex("^Dataverse\\.Delete fail \\d+ ms System\\.InvalidOperationException$"), lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("secret"));
    }
}
