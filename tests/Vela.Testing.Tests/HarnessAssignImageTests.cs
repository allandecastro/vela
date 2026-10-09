using System;
using System.Collections.Concurrent;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // #475: an Assign step reads its images through the typed ctx.PreImage / ctx.PostImage. Verified live on
    // 2026-10-09 (#439): the pre-image holds the old owner at every stage, the post-image the new one.

    public sealed class AssignImageRecorder : EntityPluginBase<FakeOwnable>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public AssignImageRecorder(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnAssign().PreValidation().Sync()
                .WithPreImage(o => o.OwnerId)
                .Handle(ctx => Seen.Enqueue($"PreValidation pre={Owner(ctx.PreImage)}"));
            RegisterStep().OnAssign().PreOperation().Sync()
                .WithPreImage(o => o.OwnerId)
                .Handle(OnAssigning);
            RegisterStep().OnAssign().PostOperation().Sync()
                .WithPreImage(o => o.OwnerId)
                .WithPostImage(o => o.OwnerId, o => o.Name)
                .Handle(OnAssigned);
        }

        private void OnAssigning(IAssignPreContext<FakeOwnable> ctx) =>
            Seen.Enqueue($"PreOperation pre={Owner(ctx.PreImage)}");

        private void OnAssigned(IAssignPostContext<FakeOwnable> ctx) =>
            Seen.Enqueue($"PostOperation pre={Owner(ctx.PreImage)} post={Owner(ctx.PostImage)} name={ctx.PostImage?.Name}");

        private static string Owner(FakeOwnable? image) => image?.OwnerId?.Id.ToString().Substring(32) ?? "none";
    }

    public sealed class HarnessAssignImageTests
    {
        [Fact]
        public void An_assign_step_reads_the_old_and_new_owner_through_typed_images()
        {
            while (AssignImageRecorder.Seen.TryDequeue(out _))
            {
            }

            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AssignImageRecorder>()
                .Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id)
                {
                    ["name"] = "Zava",
                    ["ownerid"] = new EntityReference("systemuser", new Guid("47500000-0000-0000-0000-00000000a001")),
                });

            pipeline.Service.Execute(new AssignRequest
            {
                Target = new EntityReference(FakeOwnable.EntityLogicalNameValue, id),
                Assignee = new EntityReference("systemuser", new Guid("47500000-0000-0000-0000-00000000a002")),
            });

            Assert.Equal(
                new[]
                {
                    "PreValidation pre=a001",
                    "PreOperation pre=a001",
                    "PostOperation pre=a001 post=a002 name=Zava",
                },
                AssignImageRecorder.Seen.ToArray());
        }
    }
}
