using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // A-25: two steps on one trigger, told apart by their filters, each running its own handler.
    public sealed class TwoStepsOnOneTrigger : EntityPluginBase<FakeAccount>
    {
        public TwoStepsOnOneTrigger(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(OnNameChanged);
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.NumberOfEmployees)
                .WithExecutionOrder(2)
                .Handle(OnHeadcountChanged);
        }

        private void OnNameChanged(IUpdatePostContext<FakeAccount> ctx)
        {
            ctx.Trace("ran:name");
            ctx.SystemService.Create(new FakeTask { Subject = "name" });
        }

        private void OnHeadcountChanged(IUpdatePostContext<FakeAccount> ctx)
        {
            ctx.Trace("ran:headcount");
            ctx.SystemService.Create(new FakeTask { Subject = "headcount" });
        }
    }

    // Two asynchronous steps on one trigger: Dataverse gives an async step's OwningExtension only its id (verified
    // live), so dispatch resolves the step name by reading the step row.
    public sealed class TwoAsyncStepsOnOneTrigger : EntityPluginBase<FakeAccount>
    {
        public TwoAsyncStepsOnOneTrigger(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Async()
                .FilterOn(a => a.Name)
                .Handle(OnNameChanged);
            RegisterStep().OnUpdate().PostOperation().Async(deleteJobOnSuccess: true)
                .FilterOn(a => a.NumberOfEmployees)
                .Handle(OnHeadcountChanged);
        }

        private void OnNameChanged(IUpdatePostContext<FakeAccount> ctx)
        {
            if (ctx.PluginExecutionContext.OwningExtension?.Name != null)
            {
                throw new InvalidPluginExecutionException("an async step's OwningExtension carries no name");
            }

            ctx.SystemService.Create(new FakeTask { Subject = "async-name" });
        }

        private void OnHeadcountChanged(IUpdatePostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = "async-headcount" });
    }

    // A-24: an Associate step scoped to one relationship.
    public sealed class ScopedAssociate : EntityPluginBase<FakeAccount>
    {
        public ScopedAssociate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssociate().PostOperation().Sync()
                .ForRelationship("account_contacts")
                .Handle(OnAssociated);

        private void OnAssociated(IAssociatePostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = "associated" });
    }

    // A-72: a step that is skipped past a depth of 2, instead of the plugin-level guard (8) throwing.
    public sealed class DepthLimitedStep : EntityPluginBase<FakeAccount>
    {
        public DepthLimitedStep(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .SkipWhenDepthAbove(2)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx) => ctx.Trace("ran:depth");
    }

    // A step whose own depth limit (20) is above the plugin-level MaxDepth (8).
    public sealed class DepthLimitedAboveMax : EntityPluginBase<FakeAccount>
    {
        public DepthLimitedAboveMax(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .SkipWhenDepthAbove(20)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx) => ctx.Trace("ran:deep");
    }

    public sealed class StepIdentityRuntimeTests
    {
        private static PluginContextBuilder<FakeAccount> UpdateContext(string? owningStepName = null, int depth = 1)
        {
            Guid id = Guid.NewGuid();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .OnUpdate().Stage(PluginStages.PostOperation).Sync().Depth(depth)
                .WithPrimaryEntity(FakeAccount.EntityLogicalNameValue, id)
                .WithTarget(new FakeAccount { Id = id, Name = "Zava" })
                .WithOrganizationService(new InMemoryOrganizationService());
            if (owningStepName != null)
            {
                builder.Context.OwningExtension = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()) { Name = owningStepName };
            }

            return builder;
        }

        [Fact]
        public void Two_steps_on_one_trigger_get_distinct_keys_and_the_first_keeps_the_historical_key()
        {
            StepDefinition[] steps = new TwoStepsOnOneTrigger(null!, null!).GetStepDefinitions();

            Assert.Equal(2, steps.Length);
            Assert.NotEqual(steps[0].StepKey, steps[1].StepKey);
            // The first step's key is the one earlier versions derived (seed with rank 1), so it does not churn.
            Assert.Equal(HistoricalAutoKey(typeof(TwoStepsOnOneTrigger).FullName!, "Update", 40, 0, "account"), steps[0].StepKey);
        }

        [Theory]
        [InlineData(0, "ran:name", "ran:headcount")]
        [InlineData(1, "ran:headcount", "ran:name")]
        public void The_executing_step_dispatches_to_its_own_handler(int index, string expected, string notExpected)
        {
            StepDefinition step = new TwoStepsOnOneTrigger(null!, null!).GetStepDefinitions()[index];
            PluginContextBuilder<FakeAccount> builder = UpdateContext($"{step.PluginTypeName} - PostOpSync: {step.StepKey}");

            builder.Execute(new TwoStepsOnOneTrigger(null!, null!));

            Assert.Contains(expected, builder.TraceCapture.Output);
            Assert.DoesNotContain(notExpected, builder.TraceCapture.Output);
        }

        [Fact]
        public void Without_the_executing_step_two_steps_on_one_trigger_fail_with_a_clear_error()
        {
            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => UpdateContext().Execute(new TwoStepsOnOneTrigger(null!, null!)));
            Assert.Contains("vela apply", ex.Message);
        }

        [Fact]
        public void The_pipeline_runs_each_step_of_a_shared_trigger_once()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TwoStepsOnOneTrigger>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New", NumberOfEmployees = 5 });

            string[] subjects = pipeline.GetAll("task").Select(t => t.GetAttributeValue<string>("subject")).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "headcount", "name" }, subjects);
        }

        [Fact]
        public void Async_steps_on_one_trigger_dispatch_through_the_step_id()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TwoAsyncStepsOnOneTrigger>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });
            Assert.Equal("async-name", Assert.Single(pipeline.GetAll("task")).GetAttributeValue<string>("subject"));

            pipeline.Service.Update(new FakeAccount { Id = id, NumberOfEmployees = 3 });
            Assert.Contains("async-headcount", pipeline.GetAll("task").Select(t => t.GetAttributeValue<string>("subject")));
        }

        [Fact]
        public void ForRelationship_runs_only_for_its_relationship()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ScopedAssociate>()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .WithManyToMany("account_partners", "account_partner", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId });

            pipeline.Service.Associate("account", accountId, new Relationship("account_partners"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });
            Assert.Empty(pipeline.GetAll("task"));

            pipeline.Service.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });
            Assert.Single(pipeline.GetAll("task"));
        }

        [Fact]
        public void ForRelationship_is_recorded_on_the_definition()
        {
            StepDefinition step = Assert.Single(new ScopedAssociate(null!, null!).GetStepDefinitions());
            Assert.Equal(new[] { "account_contacts" }, step.Relationships);
        }

        [Fact]
        public void SkipWhenDepthAbove_skips_the_handler_past_its_depth_and_traces_it()
        {
            PluginContextBuilder<FakeAccount> deep = UpdateContext(depth: 3);
            deep.Execute(new DepthLimitedStep(null!, null!));
            Assert.DoesNotContain("ran:depth", deep.TraceCapture.Output);
            Assert.Contains("SkipWhenDepthAbove", deep.TraceCapture.Output);

            PluginContextBuilder<FakeAccount> shallow = UpdateContext(depth: 2);
            shallow.Execute(new DepthLimitedStep(null!, null!));
            Assert.Contains("ran:depth", shallow.TraceCapture.Output);
        }

        [Fact]
        public void SkipWhenDepthAbove_replaces_the_plugin_level_guard_for_its_step()
        {
            // Depth 10 is past the plugin-level MaxDepth (8), which would throw; the step's own limit (20) applies.
            PluginContextBuilder<FakeAccount> builder = UpdateContext(depth: 10);
            builder.Execute(new DepthLimitedAboveMax(null!, null!));
            Assert.Contains("ran:deep", builder.TraceCapture.Output);
        }

        [Fact]
        public void SkipWhenDepthAbove_is_runtime_only_and_validated()
        {
            StepDefinition step = Assert.Single(new DepthLimitedStep(null!, null!).GetStepDefinitions());
            Assert.Equal(2, step.SkipWhenDepthAbove);
            Assert.Throws<ArgumentOutOfRangeException>(() => new BadDepth(null!, null!).GetStepDefinitions());
        }

        private sealed class BadDepth : EntityPluginBase<FakeAccount>
        {
            public BadDepth(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).SkipWhenDepthAbove(0).Handle(OnUpdated);

            private void OnUpdated(IUpdatePostContext<FakeAccount> ctx) { }
        }

        private static string HistoricalAutoKey(string plugin, string message, int stage, int mode, string entity)
        {
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("|", plugin, message, stage, mode, entity, 1)));
            return "dv-" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 12);
        }
    }
}
