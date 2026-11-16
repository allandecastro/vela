using System;
using System.Collections.Concurrent;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // #462: a Merge step captures images of the master (Target) and of the subordinate (SubordinateId). Verified live
    // on 2026-10-09: both kinds on one step, at every stage; the subordinate post-image is the deactivated record with
    // masterid pointing at the master.

    public sealed class MergeImageRecorder : EntityPluginBase<FakeAccount>
    {
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        public MergeImageRecorder(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMerge().PreValidation().Sync()
                .WithPreImage(a => a.Name)
                .WithSubordinatePreImage(a => a.Name)
                .Handle(ctx => Seen.Enqueue($"PreValidation master={ctx.PreImage?.Name} sub={ctx.SubordinatePreImage?.Name}"));
            RegisterStep().OnMerge().PreOperation().Sync()
                .WithSubordinatePreImage("dup", a => a.Name, a => a.NumberOfEmployees)
                .Handle(ctx =>
                {
                    FakeAccount? dup = ctx.GetPreImage<FakeAccount>("dup");
                    Seen.Enqueue($"PreOperation dup={dup?.Name}:{dup?.NumberOfEmployees}:{dup?.Id == ctx.Request.SubordinateId}");
                });
            RegisterStep().OnMerge().PostOperation().Sync()
                .WithPreImage(a => a.Name, a => a.NumberOfEmployees)
                .WithPostImage(a => a.NumberOfEmployees)
                .WithSubordinatePostImage()
                .Handle(ctx =>
                {
                    Entity? sub = ctx.SubordinatePostImage;
                    Seen.Enqueue(
                        $"PostOperation master={ctx.PreImage?.NumberOfEmployees}->{ctx.PostImage?.NumberOfEmployees} " +
                        $"sub state={sub?.GetAttributeValue<OptionSetValue>("statecode")?.Value} master={sub?.GetAttributeValue<EntityReference>("masterid")?.Id == ctx.Request.Target.Id}");
                });
        }
    }

    public sealed class HarnessMergeImageTests
    {
        [Fact]
        public void A_merge_step_receives_the_master_and_subordinate_images_at_each_stage()
        {
            while (MergeImageRecorder.Seen.TryDequeue(out _))
            {
            }

            Guid master = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<MergeImageRecorder>()
                .Seed(
                    new FakeAccount { Id = master, Name = "Master" },
                    new FakeAccount { Id = subordinate, Name = "Dup", NumberOfEmployees = 7 });

            pipeline.Service.Execute(new MergeRequest
            {
                Target = new EntityReference("account", master),
                SubordinateId = subordinate,
                UpdateContent = new Entity("account"),
                PerformParentingChecks = false,
            });

            Assert.Equal(
                new[]
                {
                    "PreValidation master=Master sub=Dup",
                    "PreOperation dup=Dup:7:True",
                    // The master had no employee count and keeps none: Merge does not copy the subordinate's columns
                    // (verified live).
                    "PostOperation master=-> sub state=1 master=True",
                },
                MergeImageRecorder.Seen.ToArray());
        }
    }
}
