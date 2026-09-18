using System.Linq;
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
    // Stand-in for a generated request (`vela models --custom-apis` would emit `zava_RecalcRequest`).
    internal sealed class RecalcRequest : TypedRequest
    {
        public RecalcRequest() => RequestName = "zava_Recalc";
    }

    // Stand-in for the generated, entity-constrained, async-only step extension. Extension methods must live in a
    // top-level static class, exactly as the generator emits `{UniqueName}StepExtensions`.
    internal static class RecalcStepExtensions
    {
        public static Vela.Steps.IAsyncOnlyTypedStages<TestAccount, RecalcRequest> OnRecalc(this Vela.Steps.IStepTrigger<TestAccount> builder)
            => builder.OnAsyncOnly<RecalcRequest>();
    }

    // Phase 3c: a Custom API whose allowedcustomprocessingsteptype is AsyncOnly generates an On<Message>() step
    // extension that delegates to IStepTrigger<T>.OnAsyncOnly<TRequest>() and returns an async-only stage. This
    // proves the framework plumbing end to end: the extension shape the generator emits compiles, registers, and
    // dispatches; and IAsyncMode exposes only Async() (the compile-time guarantee that .Sync() cannot
    // be written on an async-only Custom API).
    public sealed class CustomApiAsyncOnlyStepTests
    {
        private sealed class RecalcProbe : EntityPluginBase<TestAccount>
        {
            public bool Ran;

            public RecalcProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnRecalc().PostOperation().Async().Handle(OnRecalc);

            private void OnRecalc(IMessagePostContext<RecalcRequest> ctx) => Ran = true;
        }

        [Fact]
        public void AsyncOnly_extension_registers_and_dispatches_as_async()
        {
            RecalcProbe probe = new RecalcProbe();

            PluginContext.For<TestAccount>()
                .OnMessage("zava_Recalc")
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Asynchronous)
                .WithTarget(new TestAccount { Name = "Zava" })
                .Execute(probe);

            Assert.True(probe.Ran);
        }

        [Fact]
        public void AsyncOnlyModeSelector_exposes_only_Async()
        {
            var methods = typeof(Vela.Steps.IAsyncMode<>).GetMethods().Select(m => m.Name).ToArray();

            Assert.Contains("Async", methods);
            Assert.DoesNotContain("Sync", methods);
            Assert.DoesNotContain("Mode", methods);
        }
    }
}
