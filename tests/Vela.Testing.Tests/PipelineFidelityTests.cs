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
    // Fidelity: the pipeline must reproduce documented Dataverse pipeline behaviour — every stage fires in
    // order, shared variables flow forward (and into cascades), execution order (rank) is honoured, and each
    // pipeline-aware message (Create/Update/Delete/Associate/Disassociate) runs its registered steps. These add
    // the parity/edge cases the existing PluginPipelineTests do not already cover (which owns filtering, images,
    // bypass, depth-guard, CreateMultiple/UpdateMultiple merge, ExecuteMultiple/Transaction).
    public sealed class PipelineFidelityTests
    {
        // --- Stage ordering: PreValidation -> PreOperation -> PostOperation ------------------------------

        [Fact]
        public void Create_fires_every_stage_in_pipeline_order()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfCreatePreValidation>()
                .Register<PfCreatePreOperation>()
                .Register<PfCreatePostOperation>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal(
                new[] { PluginStages.PreValidation, PluginStages.PreOperation, PluginStages.PostOperation },
                pipeline.ExecutionLog.Select(e => e.Stage).ToArray());
        }

        [Fact]
        public void Update_fires_the_pre_and_post_stages()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfUpdatePreOperation>()
                .Register<PfUpdatePostOperation>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            Assert.Equal(
                new[] { PluginStages.PreOperation, PluginStages.PostOperation },
                pipeline.ExecutionLog.Select(e => e.Stage).ToArray());
        }

        // --- Sync vs async mode -------------------------------------------------------------------------

        [Fact]
        public void An_async_step_runs_after_the_synchronous_commit_with_async_mode()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfUpdateAsyncPost>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog);
            Assert.Equal(ExecutionMode.Asynchronous, entry.Mode);
            // The async step ran inline after the commit, so its write is observable.
            Assert.Single(pipeline.GetAll("annotation"));
        }

        // --- Shared variables flow forward across stages and into cascades ------------------------------

        [Fact]
        public void SharedVariables_set_in_a_pre_stage_are_visible_in_a_later_stage()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfSetSharedVarPre>()
                .Register<PfReadSharedVarPost>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity note = Assert.Single(pipeline.GetAll("annotation"));
            Assert.Equal("shared:hello", note.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void A_cascaded_operation_sees_the_callers_SharedVariables_only_through_ParentContext()
        {
            // A pre-stage sets a shared variable at the top level; a plugin fired by a cascaded (nested) write has its
            // own collection and finds the variable on the calling step's context, its ParentContext's parent
            // (verified live).
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfSetSharedVarPre>()
                .Register<PfCascadeCreateTaskPost>()
                .Register<PfReadSharedVarOnTask>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // The task-create plugin (depth 2): Create/30 of the task, then the account's Create/40 that wrote the task.
            Entity note = Assert.Single(pipeline.GetAll("annotation"));
            Assert.Equal("own:<none> Create/30 d2 -> Create/40 d1:hello", note.GetAttributeValue<string>("subject"));
        }

        // --- Execution order (rank) ---------------------------------------------------------------------

        [Fact]
        public void Steps_fire_in_execution_order_rank()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfRankTen>()   // registered first, but rank 10
                .Register<PfRankOne>();  // registered second, but rank 1

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            string[] order = pipeline.ExecutionLog.Select(e => e.PluginTypeName).ToArray();
            int rankOne = Array.FindIndex(order, n => n.Contains(nameof(PfRankOne)));
            int rankTen = Array.FindIndex(order, n => n.Contains(nameof(PfRankTen)));
            Assert.True(rankOne >= 0 && rankTen >= 0);
            Assert.True(rankOne < rankTen, "the lower rank must fire first regardless of registration order");
        }

        // --- Delete fires pre and post stages -----------------------------------------------------------

        [Fact]
        public void Delete_fires_the_pre_and_post_stages()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfDeletePre>()
                .Register<PfDeletePost>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Delete("account", id);

            Assert.Equal(
                new[] { PluginStages.PreOperation, PluginStages.PostOperation },
                pipeline.ExecutionLog.Select(e => e.Stage).ToArray());
            Assert.Empty(pipeline.GetAll("account"));
        }

        // --- Associate / Disassociate fire their steps --------------------------------------------------

        [Fact]
        public void Associate_and_Disassociate_fire_their_registered_steps()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfOnAssociate>()
                .Register<PfOnDisassociate>()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account", accountId), new Entity("contact", contactId));

            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("contact", contactId) };
            pipeline.Service.Associate("account", accountId, new Relationship("account_contacts"), related);
            pipeline.Service.Disassociate("account", accountId, new Relationship("account_contacts"), related);

            Assert.Contains(pipeline.ExecutionLog, e => e.Message == PluginMessages.Associate && e.PluginTypeName.Contains(nameof(PfOnAssociate)));
            Assert.Contains(pipeline.ExecutionLog, e => e.Message == PluginMessages.Disassociate && e.PluginTypeName.Contains(nameof(PfOnDisassociate)));
        }

        // --- Images: a pre-image at a PRE stage already reflects the stored (pre) values -----------------

        [Fact]
        public void A_pre_image_reflects_pre_values_at_the_pre_operation_stage()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PfPreImageAtPreOperation>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            // The pre-operation plugin saw the OLD value in its pre-image (the write had not yet been applied).
            Entity note = Assert.Single(pipeline.GetAll("annotation"));
            Assert.Equal("preimage:Old", note.GetAttributeValue<string>("subject"));
        }
    }

    // --- Plugin doubles for the pipeline fidelity suite (public + (string,string) ctor to be discoverable) ---

    public sealed class PfCreatePreValidation : EntityPluginBase<FakeAccount>
    {
        public PfCreatePreValidation(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class PfCreatePreOperation : EntityPluginBase<FakeAccount>
    {
        public PfCreatePreOperation(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class PfCreatePostOperation : EntityPluginBase<FakeAccount>
    {
        public PfCreatePostOperation(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class PfUpdatePreOperation : EntityPluginBase<FakeAccount>
    {
        public PfUpdatePreOperation(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class PfUpdatePostOperation : EntityPluginBase<FakeAccount>
    {
        public PfUpdatePostOperation(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class PfUpdateAsyncPost : EntityPluginBase<FakeAccount>
    {
        public PfUpdateAsyncPost(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Async().FilterOn(a => a.Name).Handle(OnUpdated);
        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "async" });
    }

    public sealed class PfSetSharedVarPre : EntityPluginBase<FakeAccount>
    {
        public PfSetSharedVarPre(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(OnPre);
        private void OnPre(ICreatePreContext<FakeAccount> ctx)
            => ctx.PluginExecutionContext.SharedVariables["greeting"] = "hello";
    }

    public sealed class PfReadSharedVarPost : EntityPluginBase<FakeAccount>
    {
        public PfReadSharedVarPost(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnPost);
        private void OnPost(ICreatePostContext<FakeAccount> ctx)
        {
            string value = ctx.PluginExecutionContext.SharedVariables.TryGetValue("greeting", out object? v) ? v?.ToString() ?? "?" : "<none>";
            ctx.SystemService.Create(new Entity("annotation") { ["subject"] = $"shared:{value}" });
        }
    }

    public sealed class PfCascadeCreateTaskPost : EntityPluginBase<FakeAccount>
    {
        public PfCascadeCreateTaskPost(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnPost);
        private void OnPost(ICreatePostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = "cascade" });
    }

    public sealed class PfReadSharedVarOnTask : EntityPluginBase<FakeTask>
    {
        public PfReadSharedVarOnTask(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Subject).Handle(OnPost);
        private void OnPost(ICreatePostContext<FakeTask> ctx)
        {
            static string Read(IPluginExecutionContext c) => c.SharedVariables.TryGetValue("greeting", out object? v) ? v?.ToString() ?? "?" : "<none>";
            IPluginExecutionContext main = ctx.PluginExecutionContext.ParentContext;
            IPluginExecutionContext caller = main.ParentContext;
            ctx.SystemService.Create(new Entity("annotation")
            {
                ["subject"] = $"own:{Read(ctx.PluginExecutionContext)} {main.MessageName}/{main.Stage} d{main.Depth} -> {caller.MessageName}/{caller.Stage} d{caller.Depth}:{Read(caller)}",
            });
        }
    }

    public sealed class PfRankOne : EntityPluginBase<FakeAccount>
    {
        public PfRankOne(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1).Handle(_ => { });
    }

    public sealed class PfRankTen : EntityPluginBase<FakeAccount>
    {
        public PfRankTen(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(10).Handle(_ => { });
    }

    public sealed class PfDeletePre : EntityPluginBase<FakeAccount>
    {
        public PfDeletePre(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreOperation().Sync().Handle(_ => { });
    }

    public sealed class PfDeletePost : EntityPluginBase<FakeAccount>
    {
        public PfDeletePost(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PostOperation().Sync().Handle(_ => { });
    }

    public sealed class PfOnAssociate : EntityPluginBase<FakeAccount>
    {
        public PfOnAssociate(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssociate().PostOperation().Sync().Handle(_ => { });
    }

    public sealed class PfOnDisassociate : EntityPluginBase<FakeAccount>
    {
        public PfOnDisassociate(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDisassociate().PostOperation().Sync().Handle(_ => { });
    }

    public sealed class PfPreImageAtPreOperation : EntityPluginBase<FakeAccount>
    {
        public PfPreImageAtPreOperation(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).WithPreImage(a => a.Name).Handle(OnPre);
        private void OnPre(IUpdatePreContext<FakeAccount> ctx)
        {
            Entity pre = ctx.PluginExecutionContext.PreEntityImages[PluginImageAliases.PreImage];
            ctx.SystemService.Create(new Entity("annotation") { ["subject"] = $"preimage:{pre.GetAttributeValue<string>("name")}" });
        }
    }
}
