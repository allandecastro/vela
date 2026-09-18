using System;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // The curated Create/Update contexts expose an early-bound ctx.Request.Target (ctx.Request is Vela's
    // CreateRequest<TEntity>/UpdateRequest<TEntity>, not the SDK request). This asserts the typed request path
    // end-to-end through the in-memory pipeline.
    public sealed class TypedRequestCuratedTargetTests
    {
        private sealed class CreateProbe : EntityPluginBase<TestAccount>
        {
            public string? ViaRequest;
            public string? ViaTarget;
            public CreateProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);

            private void OnCreated(ICreatePostContext<TestAccount> ctx)
            {
                ViaRequest = ctx.Request.Target.Name;   // early-bound via ctx.Request.Target
                ViaTarget = ctx.Request.Target.Name;    // the single typed target accessor
            }
        }

        private sealed class UpdateProbe : EntityPluginBase<TestAccount>
        {
            public string? ViaRequest;
            public UpdateProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdated);

            private void OnUpdated(IUpdatePostContext<TestAccount> ctx)
                => ViaRequest = ctx.Request.Target.Name;
        }

        [Fact]
        public void Create_curated_context_exposes_early_bound_ctx_Request_Target()
        {
            CreateProbe probe = new CreateProbe();

            PluginContext.For<TestAccount>()
                .OnCreate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Acme" })
                .Execute(probe);

            Assert.Equal("Acme", probe.ViaRequest);
            Assert.Equal("Acme", probe.ViaTarget);
        }

        [Fact]
        public void Update_curated_context_exposes_early_bound_ctx_Request_Target()
        {
            UpdateProbe probe = new UpdateProbe();

            PluginContext.For<TestAccount>()
                .OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Zava" })
                .Execute(probe);

            Assert.Equal("Zava", probe.ViaRequest);
        }
    }
}
