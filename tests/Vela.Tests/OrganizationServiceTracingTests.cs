using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;
using Moq;
using Xunit;

namespace Vela.Tests;

public sealed class OrganizationServiceTracingTests
{
    [Fact]
    public void Create_EmitsDataverseOperationTraces()
    {
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        inner.Setup(service => service.Create(It.IsAny<Entity>())).Returns(Guid.NewGuid());

        Mock<ILocalPluginContext> context = new Mock<ILocalPluginContext>();
        context.SetupGet(value => value.MessageName).Returns("Create");
        context.SetupGet(value => value.Stage).Returns(20);
        context.SetupGet(value => value.Mode).Returns(0);
        context.SetupGet(value => value.Depth).Returns(1);
        context.SetupGet(value => value.CorrelationId).Returns(Guid.NewGuid());

        TracingOrganizationService traced = new TracingOrganizationService(inner.Object, context.Object, "User");
        _ = traced.Create(new Entity("account"));

        context.Verify(
            value => value.Trace(
                It.Is<string>(format => format.Contains("start")),
                It.Is<object[]>(args => args.Length > 1 && object.Equals(args[1], "Create"))),
            Times.Once);
        context.Verify(
            value => value.Trace(
                It.Is<string>(format => format.Contains("ok")),
                It.Is<object[]>(args => args.Length > 1 && object.Equals(args[1], "Create"))),
            Times.Once);
    }
}
