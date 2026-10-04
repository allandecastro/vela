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

        private sealed class RelationshipLinkPlugin : EntityPluginBase<TestAccount>
        {
            public RelationshipLinkPlugin() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnAssociate().PostOperation().Sync().Handle(ctx => { });
                RegisterStep().OnDisassociate().PostOperation().Sync().Handle(ctx => { });
                // Control: a normal entity-bound step keeps its primary entity.
                RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => { });
            }
        }

        [Fact]
        public void Associate_and_disassociate_register_with_no_primary_entity()
        {
            StepDefinition[] defs = new RelationshipLinkPlugin().GetStepDefinitions();

            // Dataverse registers Associate/Disassociate against primary entity "none" (no per-entity filter; the
            // handler discriminates on ctx.Request.Relationship), so the builder must clear TEntity's logical name.
            Assert.Equal(string.Empty, defs.Single(d => d.Message == PluginMessages.Associate).EntityLogicalName);
            Assert.Equal(string.Empty, defs.Single(d => d.Message == PluginMessages.Disassociate).EntityLogicalName);

            // Control: a normal Update step still carries its primary entity, so the blanking is scoped to the two
            // relationship messages (fails if the fix blanks everything, or if it skips Associate/Disassociate).
            Assert.Equal("account", defs.Single(d => d.Message == PluginMessages.Update).EntityLogicalName);
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

        private static readonly System.Guid ImpersonatedUser = System.Guid.Parse("11111111-1111-1111-1111-111111111111");

        private sealed class ImpersonationPlugin : EntityPluginBase<TestAccount>
        {
            public ImpersonationPlugin() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .WithImpersonation(ImpersonatedUser)
                    .Handle(ctx => { });

                // No WithImpersonation -> runs as the calling user (ImpersonatingUserId stays null).
                RegisterStep().OnCreate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .Handle(ctx => { });
            }
        }

        [Fact]
        public void WithImpersonation_sets_the_systemuser_id_and_defaults_to_null_when_omitted()
        {
            StepDefinition[] defs = new ImpersonationPlugin().GetStepDefinitions();

            StepDefinition update = defs.Single(d => d.Message == PluginMessages.Update);
            Assert.Equal(ImpersonatedUser, update.ImpersonatingUserId);

            StepDefinition create = defs.Single(d => d.Message == PluginMessages.Create);
            Assert.Null(create.ImpersonatingUserId);
        }

        private sealed class EmptyImpersonationPlugin : EntityPluginBase<TestAccount>
        {
            public EmptyImpersonationPlugin() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .WithImpersonation(System.Guid.Empty)
                    .Handle(ctx => { });
        }

        [Fact]
        public void WithImpersonation_rejects_an_empty_systemuser_id()
        {
            System.ArgumentException ex = Assert.Throws<System.ArgumentException>(
                () => new EmptyImpersonationPlugin().GetStepDefinitions());
            Assert.Contains("non-empty systemuser id", ex.Message);
        }

        private static void StepImage_Post_Snapshot(StepDefinition update)
        {
            ImageDefinition post = Assert.Single(update.PostImages);
            Assert.Equal("Snapshot", post.Alias);
            Assert.Equal(new[] { "name" }, post.Attributes);
        }
    }
}
