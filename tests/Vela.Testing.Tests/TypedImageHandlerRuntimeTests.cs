using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
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

    // Typed On<TRequest>() path: the handler keeps its generic message context (IMessagePreContext<DeleteRequest>)
    // and additionally receives the projection of the captured pre-image, via the unified delegate-driven dispatch.
    public sealed class TypedDeletePreImage : EntityPluginBase<FakeAccount>
    {
        public TypedDeletePreImage(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle((IMessagePreContext<DeleteRequest> ctx, AccountNameImage? pre) =>
                    ctx.SystemService.Create(new FakeTask { Subject = pre?.Name ?? "(none)" }));
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

        [Fact]
        public void Typed_on_request_handler_receives_a_projection_of_the_captured_image()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TypedDeletePreImage>()
                .Seed(new FakeAccount { Id = id, Name = "Zava" });

            pipeline.Service.Delete("account", id);

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Zava", task.Subject);
        }
    }
}
