using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Hand-written stand-in for a generated image projection: only the captured column, annotated with its
    // logical name so ImageProjector can populate it. The build-time generator emits exactly this shape.
    public sealed class AccountNameImage
    {
        [AttributeLogicalName("name")]
        public string? Name { get; set; }
    }

    // Uses the typed-image terminal: the handler receives projections of the captured pre/post images instead of
    // the full entity. Mirrors the untyped CaptureImages double, but through Handle<TPre, TPost>.
    public sealed class CaptureTypedImages : EntityPluginBase<FakeAccount>
    {
        public CaptureTypedImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle((IUpdatePostContext<FakeAccount> ctx, AccountNameImage? pre, AccountNameImage? post) =>
                    ctx.SystemService.Create(new FakeTask { Subject = $"{pre?.Name}->{post?.Name}" }));
    }

    public sealed class TypedImageHandlerRuntimeTests
    {
        [Fact]
        public void Two_image_typed_handler_receives_projections_of_the_captured_columns()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CaptureTypedImages>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Old->New", task.Subject);
        }
    }
}
