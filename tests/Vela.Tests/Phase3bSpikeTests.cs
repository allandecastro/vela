using System;
using Vela.Constants;
using Vela.Context;
using Vela.Messages;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Phase 3b spike (.context/phase3b-compile-time-design.md): proves compile-time entity-applicability for a
    // GENERATED message step, with minimal generated code. The generator would emit, per applicable entity, an
    // On<Message>() extension constrained to IStepBuilder<ThatEntity> that just delegates to On<TRequest>().

    // Stand-in for a generated request (message applies to 'testaccount' here).
    public sealed class SpikeQualifyRequest : TypedRequest
    {
        public SpikeQualifyRequest() => RequestName = "vela_spike_qualify";
        public Guid LeadId { get => Get<Guid>(); set => Set(value); }
    }

    // GENERATED-style: OnSpikeQualify exists ONLY on IStepBuilder<TestAccount>. On any other entity's builder it is
    // not in scope -> the call does not compile. That is the compile-time entity-applicability guarantee, achieved
    // with a one-line extension delegating to the existing On<TRequest>() plumbing (no core builder change).
    public static class SpikeStepExtensions
    {
        public static ITypedMessageStepStage<TestAccount, SpikeQualifyRequest> OnSpikeQualify(this IStepBuilder<TestAccount> builder)
            => builder.On<SpikeQualifyRequest>();
    }

    public sealed class Phase3bSpikeTests
    {
        private sealed class Probe : EntityPluginBase<TestAccount>
        {
            public Guid Captured;
            public Probe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnSpikeQualify().PostOperation().Sync().Handler(OnQualify);   // reads like a curated On<Message>()

            private void OnQualify(IMessagePostContext<SpikeQualifyRequest> ctx)
                => Captured = ctx.Request.LeadId;   // typed
        }

        [Fact]
        public void Generated_entity_constrained_On_extension_registers_and_types_the_request()
        {
            Probe probe = new Probe();
            Guid lead = Guid.NewGuid();

            PluginContext.For<TestAccount>()
                .OnMessage("vela_spike_qualify")
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithInputParameter(nameof(SpikeQualifyRequest.LeadId), lead)
                .Execute(probe);

            Assert.Equal(lead, probe.Captured);
        }

        [Fact]
        public void On_extension_receiver_is_constrained_to_the_applicable_entity()
        {
            // The extension's receiver is IStepBuilder<TestAccount>; C# extension resolution therefore makes
            // OnSpikeQualify() unavailable on IStepBuilder<AnyOtherEntity> (a compile error) - proving the
            // compile-time entity-applicability without any runtime check.
            System.Reflection.ParameterInfo receiver =
                typeof(SpikeStepExtensions).GetMethod(nameof(SpikeStepExtensions.OnSpikeQualify))!.GetParameters()[0];
            Assert.Equal(typeof(IStepBuilder<TestAccount>), receiver.ParameterType);
        }
    }
}
