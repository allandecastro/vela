using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The plug-in trace log is capped at about 10 KB per execution, so the automatic traces can be turned down to
// leave room for the plugin's own: no snapshot (TraceSnapshotDetail.None), and no per-call Dataverse tracing
// (AutoTraceServiceCalls), which still records a failed call.
public sealed class TraceVolumeTests
{
    public class CallingPlugin : EntityPluginBase<TestAccount>
    {
        public CallingPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdate);

        private void OnUpdate(IUpdatePostContext<TestAccount> ctx)
        {
            ctx.Service.Create(new Entity("contact"));
            ctx.Trace("own trace");
            ctx.Service.Delete("contact", Guid.Empty);
        }
    }

    public sealed class QuietPlugin : CallingPlugin
    {
        public QuietPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.None;

        protected override bool AutoTraceServiceCalls => false;
    }

    private static PluginContextBuilder<TestAccount> Update()
    {
        Mock<IOrganizationService> service = new Mock<IOrganizationService>();
        service.Setup(s => s.Create(It.IsAny<Entity>())).Returns(Guid.NewGuid());
        service.Setup(s => s.Delete("contact", Guid.Empty)).Throws(new InvalidOperationException("missing"));
        return PluginContext.For<TestAccount>().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount { Name = "Contoso" })
            .WithUserService(service.Object);
    }

    [Fact]
    public void Every_call_and_a_snapshot_are_traced_by_default()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        Assert.Throws<InvalidPluginExecutionException>(() => builder.Execute<CallingPlugin>());

        string traces = builder.TraceCapture.Output;
        Assert.Contains("Dataverse.Create start scope=User", traces);
        Assert.Contains("Dataverse.Create ok", traces);
        Assert.Contains("Dataverse.Delete start scope=User", traces);
        Assert.Contains("Dataverse.Delete fail", traces);
        Assert.Contains("ExecutionSnapshot", traces);
    }

    [Fact]
    public void A_quiet_plugin_traces_only_its_own_lines_and_the_failed_call()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        Assert.Throws<InvalidPluginExecutionException>(() => builder.Execute<QuietPlugin>());

        string traces = builder.TraceCapture.Output;
        Assert.Contains("own trace", traces);
        Assert.DoesNotContain("ExecutionSnapshot", traces);
        Assert.DoesNotContain("Dataverse.Create", traces);
        Assert.DoesNotContain(" start ", traces);
        // The failed call keeps the detail the omitted start line would have carried, on one line.
        string failure = Assert.Single(builder.TraceCapture.Traces, t => t.Contains("Dataverse."));
        Assert.Contains("Dataverse.Delete fail", failure);
        Assert.Contains("System.InvalidOperationException scope=User depth=1 entity=contact", failure);
        Assert.DoesNotContain("missing", failure);
    }

    [Fact]
    public void An_explicit_snapshot_with_None_writes_nothing()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        LocalPluginContext ctx = builder.Build();

        ctx.TraceExecutionSnapshot("explicit", TraceSnapshotDetail.None);

        Assert.Empty(builder.TraceCapture.Traces);
    }

    [Fact]
    public void The_public_constructor_traces_every_call()
    {
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        PluginContextBuilder<TestAccount> builder = Update();
        TracingOrganizationService traced = new TracingOrganizationService(inner.Object, builder.Build(), "User");

        traced.Update(new Entity("account"));

        Assert.Equal(2, builder.TraceCapture.Traces.Count(t => t.StartsWith("Dataverse.Update", StringComparison.Ordinal)));
    }
}
