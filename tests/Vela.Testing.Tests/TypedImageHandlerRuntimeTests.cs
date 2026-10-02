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

    // The team's convention is unchanged: declare the images with WithPreImage/WithPostImage, then bind a named
    // handler with Handle(...). The handler reads the projections via ctx.PreImageAs<T>() / ctx.PostImageAs<T>().
    public sealed class CaptureTypedImages : EntityPluginBase<FakeAccount>
    {
        public CaptureTypedImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx)
        {
            AccountNameImage? pre = ctx.PreImageAs<AccountNameImage>();
            AccountNameImage? post = ctx.PostImageAs<AccountNameImage>();
            ctx.SystemService.Create(new FakeTask { Subject = $"{pre?.Name}->{post?.Name}" });
        }
    }

    // Typed On<TRequest>() path: the handler keeps its IMessagePreContext<DeleteRequest> and reads the projection
    // through the same accessor.
    public sealed class TypedDeletePreImage : EntityPluginBase<FakeAccount>
    {
        public TypedDeletePreImage(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnDelete);

        private void OnDelete(IMessagePreContext<DeleteRequest> ctx)
        {
            AccountNameImage? pre = ctx.PreImageAs<AccountNameImage>();
            ctx.SystemService.Create(new FakeTask { Subject = pre?.Name ?? "(none)" });
        }
    }

    // Stand-in for a generated projection that also carries the source image (IImageProjection) and the explicit
    // widening cast the generator emits, so the handler can hand the whole early-bound image to an entity-typed
    // service without a silent-null reconstruction.
    public sealed class AccountNameSourceImage : IImageProjection
    {
        Entity? IImageProjection.Source { get; set; }

        [AttributeLogicalName("name")]
        public string? Name { get; set; }

        public static explicit operator FakeAccount(AccountNameSourceImage projection)
            => ((IImageProjection)projection).Source!.ToEntity<FakeAccount>();
    }

    public sealed class WidenPreImageToEntity : EntityPluginBase<FakeAccount>
    {
        public WidenPreImageToEntity(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx)
        {
            AccountNameSourceImage pre = ctx.PreImageAs<AccountNameSourceImage>()!;
            FakeAccount full = (FakeAccount)pre;   // explicit widening through the generated-style operator
            // Id is only present if the whole image was retained; a reconstruction from the mapped columns alone
            // (name) would drop it. Recording it proves the cast is lossless.
            ctx.SystemService.Create(new FakeTask { Subject = $"{full.Id}|{full.Name}" });
        }
    }

    public sealed class TypedImageHandlerRuntimeTests
    {
        [Fact]
        public void Explicit_cast_widens_the_projection_to_the_full_image_losslessly()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<WidenPreImageToEntity>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal($"{id}|Old", task.Subject);
        }

        [Fact]
        public void Accessor_projects_the_captured_columns_on_the_guided_path()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CaptureTypedImages>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Old->New", task.Subject);
        }

        [Fact]
        public void Accessor_projects_the_captured_image_on_the_typed_on_request_path()
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
