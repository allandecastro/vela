using System;
using Vela.Constants;
using Vela.Context;
using Vela.Messages;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // Typed-requests design: a Vela-owned request (a TypedRequest) exposes an early-bound ctx.Request.Target
    // end-to-end through the in-memory pipeline - the single typed accessor for the primary record. It rides the
    // OnMessage<TMessage>().Handle<TRequest>() path, whose request names the marker's message.
    public sealed class TypedRequestsPocTests
    {
        private const string Message = "vela_TypedReqPoc";

        [CustomMessage(Message)]
        private sealed class TypedReqPocMessage
        {
        }

        private sealed class TypedReqPocRequest : TypedRequest
        {
            public TypedReqPocRequest() => RequestName = Message;

            public TestAccount Target { get => Get<TestAccount>(); set => Set(value); }
        }

        private sealed class Probe : EntityPluginBase<TestAccount>
        {
            public string? TargetName;

            public Probe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnMessage<TypedReqPocMessage>().PostOperation().Sync().Handle<TypedReqPocRequest>(OnMessageReceived);

            private void OnMessageReceived(IMessagePostContext<TypedReqPocRequest> ctx)
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
