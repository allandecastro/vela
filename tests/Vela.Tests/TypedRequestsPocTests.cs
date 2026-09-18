using System;
using Vela.Constants;
using Vela.Context;
using Vela.Messages;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // Typed-requests design (.context/typed-requests-design.md): a Vela-owned generic request
    // (Vela.Messages.CreateRequest<TEntity>) exposes an early-bound ctx.Request.Target end-to-end through the
    // in-memory pipeline - the single typed accessor for the primary record. It rides the existing
    // OnMessage(...).Handler<TRequest>() path.
    public sealed class TypedRequestsPocTests
    {
        private const string Message = "vela_TypedReqPoc";

        private sealed class Probe : EntityPluginBase<TestAccount>
        {
            public string? TargetName;

            public Probe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnMessage(Message).PostOperation().Sync().Handle<CreateRequest<TestAccount>>(OnMessageReceived);

            private void OnMessageReceived(IMessagePostContext<CreateRequest<TestAccount>> ctx)
                => TargetName = ctx.Request.Target.Name;   // typed Request.Target
        }

        [Fact]
        public void Vela_typed_request_exposes_early_bound_target_via_ctx_Request()
        {
            Probe probe = new Probe();

            PluginContext.For<TestAccount>()
                .OnMessage(Message)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Acme" })
                .Execute(probe);

            Assert.Equal("Acme", probe.TargetName);
        }
    }
}
