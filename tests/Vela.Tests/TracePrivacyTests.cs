using System.Linq;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The automatic execution snapshots must not copy record data into the trace log (by default) nor into
// Application Insights (ever): plugins routinely handle personal data and secrets.
public sealed class TracePrivacyTests
{
    private const string Secret = "Jane Doe <jane@contoso.com>";

    public class AccountPlugin : EntityPluginBase<TestAccount>
    {
        public AccountPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdate);

        private void OnUpdate(IUpdatePostContext<TestAccount> ctx) { }
    }

    public sealed class DebugAccountPlugin : AccountPlugin
    {
        public DebugAccountPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.Full;
    }

    private static PluginContextBuilder<TestAccount> Update() =>
        PluginContext.For<TestAccount>().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount { Name = Secret });

    [Fact]
    public void The_automatic_snapshot_lists_column_names_but_no_values_by_default()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        builder.Execute<AccountPlugin>();

        string traces = builder.TraceCapture.Output;
        Assert.Contains("ExecutionSnapshot", traces);
        Assert.Contains("[name]", traces);           // the column is named...
        Assert.DoesNotContain("Jane Doe", traces);   // ...its value is not
        Assert.Single(builder.TraceCapture.Traces, t => t.Contains("ExecutionSnapshot")); // start only, no success repeat
    }

    [Fact]
    public void A_plugin_can_opt_into_full_snapshots_while_debugging()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        builder.Execute<DebugAccountPlugin>();

        Assert.Contains("Jane Doe", builder.TraceCapture.Output);
        Assert.Equal(2, builder.TraceCapture.Traces.Count(t => t.Contains("ExecutionSnapshot"))); // start + success
    }

    [Fact]
    public void Snapshots_never_reach_the_logger()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        LocalPluginContext ctx = builder.Build();

        ctx.TraceExecutionSnapshot("explicit", TraceSnapshotDetail.Full); // a Full snapshot...
        builder.Execute<DebugAccountPlugin>();

        Assert.Contains("Jane Doe", builder.TraceCapture.Output);  // ...in the trace log
        Assert.DoesNotContain(builder.LogCapture.Entries, e => e.Message.Contains("ExecutionSnapshot") || e.Message.Contains("Jane Doe"));
    }

    [Fact]
    public void An_unknown_snapshot_detail_fails_closed_to_metadata()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        LocalPluginContext ctx = builder.Build();

        ctx.TraceExecutionSnapshot("cast", (TraceSnapshotDetail)42);

        Assert.Contains("[name]", builder.TraceCapture.Output);
        Assert.DoesNotContain("Jane Doe", builder.TraceCapture.Output);
    }

    [Fact]
    public void A_failed_Dataverse_call_traces_the_exception_type_but_not_its_message()
    {
        var inner = new Moq.Mock<Microsoft.Xrm.Sdk.IOrganizationService>();
        inner.Setup(s => s.Create(Moq.It.IsAny<Microsoft.Xrm.Sdk.Entity>())).Throws(new System.InvalidOperationException("duplicate name " + Secret));
        PluginContextBuilder<TestAccount> builder = Update();
        var traced = new TracingOrganizationService(inner.Object, builder.Build(), "User");

        Assert.Throws<System.InvalidOperationException>(() => traced.Create(new TestAccount()));

        Assert.Contains("Dataverse.Create fail", builder.TraceCapture.Output);
        Assert.Contains("System.InvalidOperationException", builder.TraceCapture.Output);
        Assert.DoesNotContain("Jane Doe", builder.TraceCapture.Output);
    }

    [Fact]
    public void An_explicit_snapshot_defaults_to_metadata_like_the_automatic_one()
    {
        PluginContextBuilder<TestAccount> builder = Update();
        LocalPluginContext ctx = builder.Build();

        ctx.TraceExecutionSnapshot("explicit");

        Assert.Contains("[name]", builder.TraceCapture.Output);
        Assert.DoesNotContain("Jane Doe", builder.TraceCapture.Output);
    }
}
