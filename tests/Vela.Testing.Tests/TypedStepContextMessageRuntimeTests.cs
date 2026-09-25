using System;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Typed step context on the typed On<TRequest>() path: the generated context derives from the generic message
    // context MessagePreContext<TRequest, TEntity> (hand-written here as the generator would emit it). Reuses NameImage
    // from TypedStepContextRuntimeTests.
    public sealed class TypedContextTypedDelete : EntityPluginBase<FakeAccount>
    {
        public TypedContextTypedDelete(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnDeleting);

        private void OnDeleting(OnDeletingContext ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = ctx.PreImage?.Name ?? "(none)" });

        public sealed class OnDeletingContext : MessagePreContext<DeleteRequest, FakeAccount>
        {
            public OnDeletingContext(ILocalPluginContext inner) : base(inner) { }
            public new NameImage? PreImage => this.PreImageAs<NameImage>();
        }
    }

    public sealed class TypedStepContextMessageRuntimeTests
    {
        [Fact]
        public void Typed_on_request_context_exposes_the_typed_pre_image()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TypedContextTypedDelete>()
                .Seed(new FakeAccount { Id = id, Name = "Zava" });

            pipeline.Service.Delete("account", id);

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Zava", task.Subject);
        }
    }
}
