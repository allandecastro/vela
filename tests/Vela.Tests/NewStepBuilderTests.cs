using System;
using System.Linq;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Smoke tests for the redesigned RegisterStep().On<Msg>().<Stage>().<Mode>().With...().Handle(...) builder.
    // It must produce a correct StepDefinition shape (message, stage, mode, filtering, images, description).
    public class NewStepBuilderTests
    {
        private sealed class NewApiPlugin : EntityPluginBase<TestAccount>
        {
            public NewApiPlugin() : base(string.Empty, string.Empty) { }

            public string Last { get; private set; } = string.Empty;

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate().PostOperation().Async()
                    .FilterOn(a => a.Name)
                    .WithPreImage(a => a.Name)
                    .WithPostImage("Snapshot", a => a.Name)
                    .WithDescription("audit")
                    .Handle(ctx => Last = "update");

                RegisterStep().OnDelete().PreOperation().Sync()
                    .WithPreImage(a => a.Name)
                    .Handle(ctx => Last = "delete");
            }
        }

        [Fact]
        public void Produces_correct_step_definitions()
        {
            StepDefinition[] defs = new NewApiPlugin().GetStepDefinitions();
            Assert.Equal(2, defs.Length);

            StepDefinition update = defs.Single(d => d.Message == PluginMessages.Update);
            Assert.Equal(PluginStages.PostOperation, update.Stage);
            Assert.Equal(ExecutionMode.Asynchronous, update.Mode);
            Assert.Equal(new[] { "name" }, update.FilteringAttributes);
            Assert.Single(update.PreImages);
            StepImage_Post_Snapshot(update);
            Assert.Equal("audit", update.Description);

            StepDefinition delete = defs.Single(d => d.Message == PluginMessages.Delete);
            Assert.Equal(PluginStages.PreOperation, delete.Stage);
            Assert.Equal(ExecutionMode.Synchronous, delete.Mode);
            Assert.Single(delete.PreImages);
            Assert.Empty(delete.PostImages);
        }

        private sealed class ExecutionOrderPlugin : EntityPluginBase<TestAccount>
        {
            public ExecutionOrderPlugin() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .WithExecutionOrder(20)
                    .Handle(ctx => { });

                // No WithExecutionOrder() -> the rank defaults to 1.
                RegisterStep().OnCreate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .Handle(ctx => { });
            }
        }

        [Fact]
        public void WithExecutionOrder_sets_the_rank_and_defaults_to_one_when_omitted()
        {
            StepDefinition[] defs = new ExecutionOrderPlugin().GetStepDefinitions();

            StepDefinition update = defs.Single(d => d.Message == PluginMessages.Update);
            Assert.Equal(20, update.ExecutionOrder);

            StepDefinition create = defs.Single(d => d.Message == PluginMessages.Create);
            Assert.Equal(1, create.ExecutionOrder);
        }

        private static void StepImage_Post_Snapshot(StepDefinition update)
        {
            ImageDefinition post = Assert.Single(update.PostImages);
            Assert.Equal("Snapshot", post.Alias);
            Assert.Equal(new[] { "name" }, post.Attributes);
        }
    }
}
