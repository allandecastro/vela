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
    public sealed class GlobalExportRequest : CustomApiRequest
    {
        public GlobalExportRequest() : base("zava_GlobalExport") { }
    }

    public sealed class BoundScoreRequest : CustomApiRequest
    {
        public BoundScoreRequest() : base("zava_BoundScore") { }
    }

    // Verified live (2026-11-16): a global Custom API has one message filter, with no table, and the steps on its
    // message see PrimaryEntityName "none" and an empty PrimaryEntityId at every stage and mode. The step definition keeps
    // the plugin's table (vela plan registers it without one); the runtime matches it on "none".
    public sealed class GlobalCustomApiStepTests
    {
        private sealed class GlobalSteps : EntityPluginBase<TestAccount>
        {
            public int Sync;
            public int Async;

            public GlobalSteps() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().On<GlobalExportRequest>().PostOperation().Sync().Handle(ctx => Sync++);
                RegisterStep().On<GlobalExportRequest>().PostOperation().Async().Handle(ctx => Async++);
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
        public void A_step_on_a_custom_api_keeps_the_plugin_table_in_its_definition()
        {
            StepDefinition[] steps = new GlobalSteps().GetStepDefinitions();

            Assert.All(steps, step => Assert.Equal("account", step.EntityLogicalName));
        }

        [Fact]
        public void A_step_declared_with_a_table_matches_an_execution_on_table_none()
        {
            GlobalSteps plugin = new GlobalSteps();

            // Before the fix: "no registered step matching message 'zava_GlobalExport' ... on entity 'none'".
            plugin.Execute(Context("zava_GlobalExport", ExecutionMode.Synchronous, "none"));
            plugin.Execute(Context("zava_GlobalExport", ExecutionMode.Asynchronous, "none"));

            Assert.Equal(1, plugin.Sync);
            Assert.Equal(1, plugin.Async);
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
