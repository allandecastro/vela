using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // #462: a Merge step takes images of the master (Target) and of the subordinate (SubordinateId). Verified live on
    // 2026-10-09: both kinds on one step at every stage; the same alias on both is accepted by Dataverse but only one
    // image is then delivered, so Vela rejects it.
    public sealed class MergeSubordinateImageTests
    {
        private sealed class MergeImages : EntityPluginBase<TestAccount>
        {
            public string? Seen;

            public MergeImages() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnMerge().PreValidation().Sync()
                    .WithPreImage(a => a.Name)
                    .WithSubordinatePreImage(a => a.Name)
                    .Handle(ctx => { });
                RegisterStep().OnMerge().PostOperation().Sync()
                    .WithPreImage(a => a.Name)
                    .WithPostImage(a => a.Name)
                    .WithSubordinatePreImage("dup", a => a.Name)
                    .WithSubordinatePostImage(a => a.Name)
                    .Handle(OnMerged);
            }

            private void OnMerged(IMergePostContext<TestAccount> ctx)
                => Seen = $"{ctx.PreImage?.Name}|{ctx.PostImage?.Name}|{ctx.SubordinatePreImage?.Name}|{ctx.SubordinatePostImage?.Name}";
        }

        [Fact]
        public void Subordinate_images_are_registered_against_SubordinateId()
        {
            StepDefinition[] defs = new MergeImages().GetStepDefinitions();

            StepDefinition preValidation = defs.Single(d => d.Stage == PluginStages.PreValidation);
            Assert.Equal(PluginMessages.Merge, preValidation.Message);
            Assert.Equal(
                new[] { (PluginImageAliases.PreImage, ""), (PluginImageAliases.SubordinatePreImage, "SubordinateId") },
                preValidation.PreImages.Select(i => (i.Alias, i.MessagePropertyName)).ToArray());

            StepDefinition post = defs.Single(d => d.Stage == PluginStages.PostOperation);
            Assert.Equal(
                new[] { (PluginImageAliases.PreImage, ""), ("dup", "SubordinateId") },
                post.PreImages.Select(i => (i.Alias, i.MessagePropertyName)).ToArray());
            Assert.Equal(
                new[] { (PluginImageAliases.PostImage, ""), (PluginImageAliases.SubordinatePostImage, "SubordinateId") },
                post.PostImages.Select(i => (i.Alias, i.MessagePropertyName)).ToArray());
            Assert.Equal(new[] { "name" }, post.PostImages[1].Attributes);
        }

        private sealed class SameAlias : EntityPluginBase<TestAccount>
        {
            public SameAlias() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnMerge().PreOperation().Sync()
                    .WithPreImage("img", a => a.Name)
                    .WithSubordinatePreImage("img", a => a.Name)
                    .Handle(ctx => { });
        }

        [Fact]
        public void The_same_alias_on_the_master_and_the_subordinate_is_rejected()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new SameAlias().GetStepDefinitions());

            Assert.Contains("Duplicate image alias: 'img'", error.Message);
        }

        [Fact]
        public void The_typed_merge_context_reads_each_image_under_its_alias()
        {
            Guid master = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            MergeImages plugin = new MergeImages();

            PluginContext.For<TestAccount>()
                .OnMessage(PluginMessages.Merge)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, master)
                .WithInputParameter("Target", new EntityReference(TestAccount.EntityLogicalNameValue, master))
                .WithInputParameter("SubordinateId", subordinate)
                .WithPreImage(new TestAccount { Id = master, Name = "Master" })
                .WithPostImage(new TestAccount { Id = master, Name = "Master after" })
                .WithPreImage(new TestAccount { Id = subordinate, Name = "Dup" }, "dup")
                .WithPostImage(new TestAccount { Id = subordinate, Name = "Dup after" }, PluginImageAliases.SubordinatePostImage)
                .Execute(plugin);

            // The step captured its subordinate pre-image under "dup", so ctx.SubordinatePreImage (default alias) is null.
            Assert.Equal("Master|Master after||Dup after", plugin.Seen);
        }
    }
}
