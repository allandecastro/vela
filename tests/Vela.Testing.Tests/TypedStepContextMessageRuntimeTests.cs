using System;
using Vela.Constants;
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
            => RegisterStep().On<RecalculateRequest>().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnRecalculating);

        private void OnRecalculating(OnRecalculatingContext ctx)
            => ctx.Trace("pre:" + (ctx.PreImage?.Name ?? "(none)"));

        public sealed class OnRecalculatingContext : MessagePreContext<RecalculateRequest, FakeAccount>
        {
            public OnRecalculatingContext(ILocalPluginContext inner) : base(inner) { }
            public new NameImage? PreImage => this.PreImageAs<NameImage>();
        }
    }

    public sealed class TypedStepContextMessageRuntimeTests
    {
        [Fact]
        public void Typed_on_request_context_exposes_the_typed_pre_image()
        {
            Guid id = Guid.NewGuid();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .OnMessage(RecalculateRequest.Name).Stage(PluginStages.PreOperation).Sync()
                .WithPrimaryEntity(FakeAccount.EntityLogicalNameValue, id)
                .WithPreImage(new FakeAccount { Id = id, Name = "Zava" });

            builder.Execute(new TypedContextTypedDelete(null!, null!));

            Assert.Contains("pre:Zava", builder.TraceCapture.Output);
        }
    }
}
