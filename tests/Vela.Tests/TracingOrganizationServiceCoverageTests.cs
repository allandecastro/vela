using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Context;
using Xunit;

namespace Vela.Tests;

// Drives every TracingOrganizationService operation with null/edge arguments so the null-conditional
// detail builders (entity?.LogicalName, relationship?.SchemaName, query?.GetType()) are exercised on both sides.
public sealed class TracingOrganizationServiceCoverageTests
{
    private static (TracingOrganizationService Svc, Mock<IOrganizationService> Inner) Build()
    {
        Mock<ILocalPluginContext> ctx = new Mock<ILocalPluginContext>();
        ctx.SetupGet(c => c.MessageName).Returns("Update");
        ctx.SetupGet(c => c.Stage).Returns(40);
        ctx.SetupGet(c => c.Mode).Returns(0);
        ctx.SetupGet(c => c.Depth).Returns(1);
        ctx.SetupGet(c => c.CorrelationId).Returns(Guid.NewGuid());
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        return (new TracingOrganizationService(inner.Object, ctx.Object, "S"), inner);
    }

    [Fact]
    public void Every_operation_traces_even_with_null_or_edge_arguments()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();

        svc.Create(null!);                                  // entity?.LogicalName -> null branch
        svc.Update(null!);                                  // entity?.LogicalName / Id -> null branch
        svc.Delete("account", Guid.NewGuid());
        svc.Retrieve("account", Guid.NewGuid(), new ColumnSet());
        svc.Associate("account", Guid.NewGuid(), null!, new EntityReferenceCollection());      // relationship?.SchemaName -> null
        svc.Disassociate("account", Guid.NewGuid(), null!, new EntityReferenceCollection());   // relationship?.SchemaName -> null
        svc.RetrieveMultiple(new QueryByAttribute("account"));                                   // non-QueryExpression details
        svc.RetrieveMultiple(null!);                                                             // query?.GetType() -> null branch

        inner.Verify(s => s.Create(null!), Times.Once);
        inner.Verify(s => s.Update(null!), Times.Once);
        inner.Verify(s => s.RetrieveMultiple(null!), Times.Once);
    }
}
