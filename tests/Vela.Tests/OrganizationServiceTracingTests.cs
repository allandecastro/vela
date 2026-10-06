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

        // One compact line when the call starts and one when it ends, each a single line (no embedded newline).
        context.Verify(
            value => value.Trace(
                It.Is<string>(format => format.Contains("start") && !format.Contains("\n")),
                It.Is<object[]>(args => object.Equals(args[0], "Create") && object.Equals(args[1], "User") && object.Equals(args[3], "entity=account"))),
            Times.Once);
        context.Verify(
            value => value.Trace(
                It.Is<string>(format => format.Contains("ok") && !format.Contains("\n")),
                It.Is<object[]>(args => object.Equals(args[0], "Create"))),
            Times.Once);
    }
}
