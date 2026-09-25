using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Typed step contexts (Option 2 B) end-to-end through the real pipeline. The nested *Context classes are
    // hand-written here exactly as the source generator would emit them (nested in the plugin, deriving from the
    // concrete step context, shadowing PreImage/PostImage with the typed projection). The DEV writes only the
    // registration and `void OnX(OnXContext ctx)`; `.Handle(OnX)` binds via the Handle(Delegate) overload and the
    // runtime builds the declared context type.
    public sealed class NameImage
    {
        [AttributeLogicalName("name")] public string? Name { get; set; }
    }

    public sealed class TypedContextUpdate : EntityPluginBase<FakeAccount>
    {
        public TypedContextUpdate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(OnUpdatedContext ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = $"{ctx.PreImage?.Name}->{ctx.PostImage?.Name}" });

        public sealed class OnUpdatedContext : UpdatePostContext<FakeAccount>
        {
            public OnUpdatedContext(ILocalPluginContext inner) : base(inner) { }
            public new NameImage? PreImage => this.PreImageAs<NameImage>();
            public new NameImage? PostImage => this.PostImageAs<NameImage>();
        }
    }

    public sealed class TypedContextCreate : EntityPluginBase<FakeAccount>
    {
        public TypedContextCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnCreated);

        private void OnCreated(OnCreatedContext ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = ctx.PostImage?.Name });

        public sealed class OnCreatedContext : CreatePostContext<FakeAccount>
        {
            public OnCreatedContext(ILocalPluginContext inner) : base(inner) { }
            public new NameImage? PostImage => this.PostImageAs<NameImage>();
        }
    }

    public sealed class TypedContextDelete : EntityPluginBase<FakeAccount>
    {
        public TypedContextDelete(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnDeleting);

        private void OnDeleting(OnDeletingContext ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = ctx.PreImage?.Name ?? "(none)" });

        public sealed class OnDeletingContext : DeletePreContext<FakeAccount>
        {
            public OnDeletingContext(ILocalPluginContext inner) : base(inner) { }
            public new NameImage? PreImage => this.PreImageAs<NameImage>();
        }
    }

    public sealed class TypedStepContextRuntimeTests
    {
        [Fact]
        public void Update_post_context_exposes_typed_pre_and_post_images()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TypedContextUpdate>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Old->New", task.Subject);
        }

        [Fact]
        public void Create_post_context_exposes_the_typed_post_image()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TypedContextCreate>();

            pipeline.Service.Create(new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Zava", task.Subject);
        }

        [Fact]
        public void Delete_pre_context_exposes_the_typed_pre_image()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TypedContextDelete>()
                .Seed(new FakeAccount { Id = id, Name = "Zava" });

            pipeline.Service.Delete("account", id);

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Zava", task.Subject);
        }
    }
}
