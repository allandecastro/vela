using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // #436: a PreValidation step may capture a pre-image (Update, Delete, UpdateMultiple, and the unmodelled
    // messages). The harness builds it from the sandbox state at dispatch time, like the platform does.

    public sealed class PvUpdatePreImage : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public PvUpdatePreImage(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreValidation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name, a => a.NumberOfEmployees)
                .Handle(ctx => Seen.Enqueue($"d{ctx.Depth}:{ctx.PreImage?.Name}:{ctx.PreImage?.NumberOfEmployees}"));
    }

    // Records how many child tasks the deleted account still has at PreValidation and at PreOperation.
    public sealed class PvDeleteChildren : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public PvDeleteChildren(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnDelete().PreValidation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"PreValidation:{ctx.PreImage?.Name}:{Children(ctx)}"));
            RegisterStep().OnDelete().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"PreOperation:{ctx.PreImage?.Name}:{Children(ctx)}"));
        }

        private static int Children(ILocalPluginContext ctx)
        {
            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet(false) };
            query.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, ctx.PrimaryEntityId);
            return ctx.SystemService.RetrieveMultiple(query).Entities.Count;
        }
    }

    // A PostOperation step that writes the same row again: the nested Update runs at depth 2, inside the outer
    // transaction, before the outer operation commits.
    public sealed class PvNestedRename : EntityPluginBase<FakeAccount>
    {
        public PvNestedRename(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.NumberOfEmployees)
                .Handle(ctx => ctx.SystemService.Update(new FakeAccount { Id = ctx.PrimaryEntityId, Name = "Nested" }));
    }

    public sealed class PvBulkPreImages : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public PvBulkPreImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PreValidation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .Handle(ctx => Seen.Enqueue(string.Join("|", ctx.PreImagesAs<AccountNameImage>().Select(i => i?.Name))));
    }

    public sealed class PreValidationPreImageTests
    {
        private static void Drain(ConcurrentQueue<string> seen)
        {
            while (seen.TryDequeue(out _))
            {
            }
        }

        [Fact]
        public void An_update_prevalidation_step_receives_its_pre_image()
        {
            Guid id = Guid.NewGuid();
            Drain(PvUpdatePreImage.Seen);
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PvUpdatePreImage>()
                .Seed(new FakeAccount { Id = id, Name = "Old", NumberOfEmployees = 5 });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            Assert.Equal(new[] { "d1:Old:5" }, PvUpdatePreImage.Seen.ToArray());
        }

        // Delete at PreValidation runs before the cascades, so the step still sees the record's children; by
        // PreOperation they are gone (verified live on 2026-10-09: 2 children at PreValidation, 0 at PreOperation).
        [Fact]
        public void A_delete_prevalidation_step_sees_the_children_before_the_cascade()
        {
            Guid account = Guid.NewGuid();
            Drain(PvDeleteChildren.Seen);
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PvDeleteChildren>()
                .WithParentalRelationship("account", "task", "regardingobjectid")
                .Seed(
                    new FakeAccount { Id = account, Name = "Parent" },
                    new Entity("task", Guid.NewGuid()) { ["regardingobjectid"] = new EntityReference("account", account) },
                    new Entity("task", Guid.NewGuid()) { ["regardingobjectid"] = new EntityReference("account", account) });

            pipeline.Service.Delete("account", account);

            Assert.Equal(new[] { "PreValidation:Parent:2", "PreOperation:Parent:0" }, PvDeleteChildren.Seen.ToArray());
            Assert.Empty(pipeline.GetAll("task"));
            Assert.Empty(pipeline.GetAll("account"));
        }

        [Fact]
        public void A_delete_prevalidation_step_fires_once_for_the_deleted_record()
        {
            Guid account = Guid.NewGuid();
            Drain(PvDeleteChildren.Seen);
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PvDeleteChildren>()
                .Seed(new FakeAccount { Id = account, Name = "Lonely" });

            pipeline.Service.Delete("account", account);

            Assert.Equal(new[] { "PreValidation:Lonely:0", "PreOperation:Lonely:0" }, PvDeleteChildren.Seen.ToArray());
            Assert.Equal(
                new[] { Vela.Constants.PluginStages.PreValidation, Vela.Constants.PluginStages.PreOperation },
                pipeline.ExecutionLog.Select(e => e.Stage).ToArray());
        }

        [Fact]
        public void Deleting_a_missing_record_fires_no_prevalidation_step()
        {
            Drain(PvDeleteChildren.Seen);
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PvDeleteChildren>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Delete("account", Guid.NewGuid()));

            Assert.Empty(PvDeleteChildren.Seen);
        }

        // A nested PreValidation step runs inside the parent transaction: its pre-image is read from the sandbox
        // state, so it includes the outer operation's uncommitted write (NumberOfEmployees = 42), not the seed.
        [Fact]
        public void A_nested_prevalidation_pre_image_reflects_the_parent_transaction_writes()
        {
            Guid id = Guid.NewGuid();
            Drain(PvUpdatePreImage.Seen);
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PvUpdatePreImage>()
                .Register<PvNestedRename>()
                .Seed(new FakeAccount { Id = id, Name = "Old", NumberOfEmployees = 5 });

            pipeline.Service.Update(new FakeAccount { Id = id, NumberOfEmployees = 42 });

            Assert.Equal(new[] { "d2:Old:42" }, PvUpdatePreImage.Seen.ToArray());
            Assert.Equal("Nested", Assert.Single(pipeline.GetAll("account")).GetAttributeValue<string>("name"));
        }

        // UpdateMultiple at PreValidation delivers one pre-image per Targets entry (verified live on 2026-10-09).
        [Fact]
        public void An_update_multiple_prevalidation_step_receives_one_pre_image_per_target()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            Drain(PvBulkPreImages.Seen);
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<PvBulkPreImages>()
                .Seed(new FakeAccount { Id = a, Name = "A-old" }, new FakeAccount { Id = b, Name = "B-old" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Id = a, Name = "A-new" });
            targets.Entities.Add(new FakeAccount { Id = b, Name = "B-new" });
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });

            Assert.Equal(new[] { "A-old|B-old" }, PvBulkPreImages.Seen.ToArray());
        }
    }
}
