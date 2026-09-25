using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Guard-rail behavior for the permissive Handle(Delegate) overload + the generated-context dispatch:
    // a wrong-arity handler and a step-context type that cannot be constructed must fail with a clear message
    // instead of an opaque crash mid-pipeline.

    // A handler bound through Handle(Delegate) with the wrong arity (zero parameters).
    public sealed class WrongArityPlugin : EntityPluginBase<FakeAccount>
    {
        public WrongArityPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle((Delegate)new Action(() => { }));
    }

    // A handler whose context parameter derives from a step context but is abstract, so it cannot be built.
    public sealed class AbstractContextPlugin : EntityPluginBase<FakeAccount>
    {
        public AbstractContextPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(AbstractCtx ctx) { }

        public abstract class AbstractCtx : UpdatePostContext<FakeAccount>
        {
            protected AbstractCtx(ILocalPluginContext inner) : base(inner) { }
        }
    }

    public sealed class TypedStepContextGuardTests
    {
        [Fact]
        public void Wrong_arity_handler_fails_with_a_clear_message()
        {
            Guid id = Guid.NewGuid();
            Exception ex = Assert.ThrowsAny<Exception>(() =>
            {
                PluginPipeline pipeline = PluginPipeline.Empty().Register<WrongArityPlugin>()
                    .Seed(new FakeAccount { Id = id, Name = "Old" });
                pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });
            });

            Assert.Contains("exactly one context parameter", ex.ToString());
        }

        [Fact]
        public void Abstract_context_type_fails_with_a_clear_message()
        {
            Guid id = Guid.NewGuid();
            Exception ex = Assert.ThrowsAny<Exception>(() =>
            {
                PluginPipeline pipeline = PluginPipeline.Empty().Register<AbstractContextPlugin>()
                    .Seed(new FakeAccount { Id = id, Name = "Old" });
                pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });
            });

            Assert.Contains("abstract", ex.ToString());
        }
    }
}
