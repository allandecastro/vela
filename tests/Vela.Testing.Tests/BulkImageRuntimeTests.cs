using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Verified live (Dataverse 9.2.26094): images register on CreateMultiple (post, "Ids") and UpdateMultiple (pre,
    // "Targets", pre and post) and reach the plugin only through the per-target image collections. The harness
    // populates them the same way and leaves the classic PreEntityImages / PostEntityImages empty.
    public sealed class BulkUpdateImages : EntityPluginBase<FakeAccount>
    {
        public BulkUpdateImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage("Before", a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdateMultiplePostContext<FakeAccount> ctx)
        {
            var before = ctx.PreImagesAs<AccountNameImage>("Before");
            var after = ctx.PostImagesAs<AccountNameImage>();
            for (int i = 0; i < before.Count; i++)
            {
                ctx.SystemService.Create(new FakeTask { Subject = $"{before[i]?.Name}->{after[i]?.Name}" });
            }

            // The classic image dictionaries stay empty on a bulk step, as on Dataverse.
            if (ctx.PluginExecutionContext.PreEntityImages.Count + ctx.PluginExecutionContext.PostEntityImages.Count > 0)
            {
                throw new InvalidPluginExecutionException("classic images are populated on a bulk step");
            }
        }
    }

    public sealed class BulkCreateImages : EntityPluginBase<FakeAccount>
    {
        public BulkCreateImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnCreated);

        private void OnCreated(ICreateMultiplePostContext<FakeAccount> ctx)
        {
            foreach (AccountNameImage? created in ctx.PostImagesAs<AccountNameImage>())
            {
                ctx.SystemService.Create(new FakeTask { Subject = "created:" + created?.Name });
            }
        }
    }

    public sealed class BulkImageRuntimeTests
    {
        [Fact]
        public void UpdateMultiple_step_reads_each_targets_images_by_alias()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<BulkUpdateImages>()
                .Seed(new FakeAccount { Id = a, Name = "A-old" }, new FakeAccount { Id = b, Name = "B-old" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Id = a, Name = "A-new" });
            targets.Entities.Add(new FakeAccount { Id = b, Name = "B-new" });
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });

            string[] transitions = pipeline.GetAll("task").Select(t => t.GetAttributeValue<string>("subject")).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "A-old->A-new", "B-old->B-new" }, transitions);
        }

        [Fact]
        public void A_single_update_fires_the_UpdateMultiple_step_with_one_target()
        {
            Guid a = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<BulkUpdateImages>()
                .Seed(new FakeAccount { Id = a, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = a, Name = "New" });

            Assert.Equal("Old->New", Assert.Single(pipeline.GetAll("task")).GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void CreateMultiple_step_reads_each_created_records_post_image()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<BulkCreateImages>();

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Name = "One" });
            targets.Entities.Add(new FakeAccount { Name = "Two" });
            pipeline.Service.Execute(new CreateMultipleRequest { Targets = targets });

            string[] subjects = pipeline.GetAll("task").Select(t => t.GetAttributeValue<string>("subject")).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "created:One", "created:Two" }, subjects);
        }

        [Fact]
        public void The_bulk_step_definitions_carry_their_images()
        {
            StepDefinition update = Assert.Single(new BulkUpdateImages(null!, null!).GetStepDefinitions());
            Assert.Equal("Before", Assert.Single(update.PreImages).Alias);
            Assert.Single(update.PostImages);
            StepDefinition create = Assert.Single(new BulkCreateImages(null!, null!).GetStepDefinitions());
            Assert.Single(create.PostImages);
        }
    }
}
