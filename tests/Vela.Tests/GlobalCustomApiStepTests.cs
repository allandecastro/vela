using System;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Messages;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Stand-ins for the requests `vela models --custom-apis` generates: a global API's request is marked
    // [GlobalCustomApi]; a hand-written one may not be.
    [GlobalCustomApi]
    public sealed class GlobalExportRequest : CustomApiRequest
    {
        public GlobalExportRequest() : base("zava_GlobalExport") { }
    }

    public sealed class UnmarkedExportRequest : CustomApiRequest
    {
        public UnmarkedExportRequest() : base("zava_UnmarkedExport") { }
    }

    public sealed class BoundScoreRequest : CustomApiRequest
    {
        public BoundScoreRequest() : base("zava_BoundScore") { }
    }

    // Verified live (2026-11-16): a global Custom API has one message filter, with no table, and the steps on its
    // message see PrimaryEntityName "none" and an empty PrimaryEntityId at every stage and mode.
    public sealed class GlobalCustomApiStepTests
    {
        private sealed class GlobalSteps : EntityPluginBase<TestAccount>
        {
            public int Marked;
            public int Unmarked;

            public GlobalSteps() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().On<GlobalExportRequest>().PostOperation().Sync().Handle(ctx => Marked++);
                RegisterStep().On<UnmarkedExportRequest>().PostOperation().Async().Handle(ctx => Unmarked++);
                RegisterStep().On<BoundScoreRequest>().PostOperation().Sync().Handle(ctx => { });
            }
        }

        private static IServiceProvider Context(string message, ExecutionMode mode, string primaryEntity)
            => PluginContext.ForMessage(message)
                .Stage(PluginStages.PostOperation)
                .Mode(mode)
                .WithPrimaryEntity(primaryEntity, Guid.Empty)
                .BuildServiceProvider();

        [Fact]
        public void A_step_on_a_request_marked_GlobalCustomApi_registers_with_no_table_whatever_the_plugin_table()
        {
            StepDefinition[] steps = new GlobalSteps().GetStepDefinitions();

            Assert.Equal(string.Empty, Array.Find(steps, s => s.Message == "zava_GlobalExport")!.EntityLogicalName);
            // Not marked: the plugin's table stays (vela plan blocks it on a global API, the harness fails the call).
            Assert.Equal("account", Array.Find(steps, s => s.Message == "zava_UnmarkedExport")!.EntityLogicalName);
            Assert.Equal("account", Array.Find(steps, s => s.Message == "zava_BoundScore")!.EntityLogicalName);
        }

        [Fact]
        public void A_step_declared_with_a_table_matches_an_execution_on_table_none()
        {
            GlobalSteps plugin = new GlobalSteps();

            plugin.Execute(Context("zava_GlobalExport", ExecutionMode.Synchronous, "none"));
            // Before the fix: "no registered step matching message 'zava_UnmarkedExport' ... on entity 'none'".
            plugin.Execute(Context("zava_UnmarkedExport", ExecutionMode.Asynchronous, "none"));

            Assert.Equal(1, plugin.Marked);
            Assert.Equal(1, plugin.Unmarked);
        }

        [Fact]
        public void A_step_declared_with_a_table_still_does_not_match_another_table()
        {
            GlobalSteps plugin = new GlobalSteps();

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => plugin.Execute(Context("zava_BoundScore", ExecutionMode.Synchronous, "contact")));

            Assert.Contains("on entity 'contact'", ex.Message);
        }
    }
}
