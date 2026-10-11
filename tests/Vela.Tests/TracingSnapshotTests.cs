using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;
using Moq;
using Xunit;

namespace Vela.Tests;

public sealed class TracingSnapshotTests
{
    [Fact]
    public void TraceExecutionSnapshot_EmitsStructuredSnapshot()
    {
        Guid targetId = Guid.NewGuid();
        ParameterCollection input = new ParameterCollection();
        input["Target"] = new Entity("account") { Id = targetId, ["name"] = "Zava", ["creditlimit"] = new Money(1500m) };
        input["Custom"] = "x";

        ParameterCollection output = new ParameterCollection();
        output["Result"] = true;

        EntityImageCollection pre = new EntityImageCollection
        {
            ["PreImage"] = new Entity("account")
            {
                Id = targetId,
                ["name"] = "Old Zava"
            }
        };
        EntityImageCollection post = new EntityImageCollection
        {
            ["PostImage"] = new Entity("account")
            {
                Id = targetId,
                ["name"] = "Zava"
            }
        };

        Mock<IPluginExecutionContext> execution = new Mock<IPluginExecutionContext>();
        execution.SetupGet(x => x.InputParameters).Returns(input);
        execution.SetupGet(x => x.OutputParameters).Returns(output);
        execution.SetupGet(x => x.PreEntityImages).Returns(pre);
        execution.SetupGet(x => x.PostEntityImages).Returns(post);
        execution.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
        execution.SetupGet(x => x.InitiatingUserId).Returns(Guid.NewGuid());

        Mock<ILocalPluginContext> context = new Mock<ILocalPluginContext>();
        context.SetupGet(x => x.PluginExecutionContext).Returns(execution.Object);
        context.SetupGet(x => x.MessageName).Returns("Update");
        context.SetupGet(x => x.Stage).Returns(20);
        context.SetupGet(x => x.Mode).Returns(0);
        context.SetupGet(x => x.Depth).Returns(1);
        context.SetupGet(x => x.CorrelationId).Returns(Guid.NewGuid());
        context.SetupGet(x => x.PrimaryEntityName).Returns("account");
        context.SetupGet(x => x.PrimaryEntityId).Returns(targetId);
        context.SetupGet(x => x.UserId).Returns(execution.Object.UserId);
        context.SetupGet(x => x.InitiatingUserId).Returns(execution.Object.InitiatingUserId);

        string? traceOutput = null;
        context
            .Setup(x => x.Trace(It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, object[]>((format, args) =>
            {
                traceOutput = args == null || args.Length == 0 ? format : string.Format(format, args);
            });

        context.Object.TraceExecutionSnapshot("start", TraceSnapshotDetail.Full);

        Assert.NotNull(traceOutput);
        Assert.Contains("ExecutionSnapshot", traceOutput);
        Assert.Contains("InputParameters:", traceOutput);
        Assert.Contains("Target:", traceOutput);
        Assert.Contains("name: \"Zava\"", traceOutput);
        Assert.Contains("creditlimit: 1500 (Money)", traceOutput);
        Assert.Contains("PreEntityImages:", traceOutput);
    }
}
