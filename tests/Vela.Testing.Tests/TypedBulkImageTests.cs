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
    // Bulk step reading per-record images as generated projections via ctx.PreImagesAs<T>() / PostImagesAs<T>().
    // Reuses the AccountNameImage projection double declared in TypedImageHandlerRuntimeTests.
    public sealed class CaptureTypedBulkImages : EntityPluginBase<FakeAccount>
    {
        public CaptureTypedBulkImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdatedMultiple);

        private void OnUpdatedMultiple(IUpdateMultiplePostContext<FakeAccount> ctx)
        {
            var pre = ctx.PreImagesAs<AccountNameImage>();
            var post = ctx.PostImagesAs<AccountNameImage>();
            for (int i = 0; i < pre.Count; i++)
            {
                ctx.SystemService.Create(new FakeTask { Subject = $"{pre[i]?.Name}->{post[i]?.Name}" });
            }
        }
    }

    public sealed class TypedBulkImageTests
    {
        [Fact]
        public void Bulk_accessor_projects_each_record_image_aligned_with_targets()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CaptureTypedBulkImages>()
                .Seed(new FakeAccount { Id = a, Name = "A-old" }, new FakeAccount { Id = b, Name = "B-old" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Id = a, Name = "A-new" });
            targets.Entities.Add(new FakeAccount { Id = b, Name = "B-new" });
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });

            string[] transitions = pipeline.GetAll("task")
                .Select(t => t.GetAttributeValue<string>("subject"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "A-old->A-new", "B-old->B-new" }, transitions);
        }
    }
}
