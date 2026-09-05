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
    // Proves the runtime dispatch builds the right per-message context for every modelled message (each arm of
    // EntityPluginBase.BuildTypedContext), and that a permissive OnMessage(...) step binds and runs an untyped
    // handler. One plugin registers a step for all 13 modelled messages plus a custom message, each bound to a
    // single ILocalPluginContext handler (assignable to every typed context by contravariance); the test runs it
    // once per message and asserts the matching step fired.
    public sealed class TypedContextDispatchTests
    {
        private sealed class AllMessagesProbe : EntityPluginBase<TestAccount>
        {
            public const string CustomMessage = "vela_custom";

            public string? Seen { get; private set; }

            public AllMessagesProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnCreateMultiple().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnUpdateMultiple().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnRetrieve().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRetrieveMultiple().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnAssociate().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnDisassociate().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnMerge().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGrantAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnModifyAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRevokeAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnMessage(CustomMessage).PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
            }

            // Bound to every step: contravariantly assignable to each Action<I{Msg}Context<TestAccount>> and to
            // the permissive Action<ILocalPluginContext>.
            private void OnAny(ILocalPluginContext ctx) => Seen = ctx.MessageName;
        }

        [Theory]
        [InlineData(PluginMessages.Create)]
        [InlineData(PluginMessages.Update)]
        [InlineData(PluginMessages.Delete)]
        [InlineData(PluginMessages.CreateMultiple)]
        [InlineData(PluginMessages.UpdateMultiple)]
        [InlineData(PluginMessages.Retrieve)]
        [InlineData(PluginMessages.RetrieveMultiple)]
        [InlineData(PluginMessages.Associate)]
        [InlineData(PluginMessages.Disassociate)]
        [InlineData(PluginMessages.Merge)]
        [InlineData(PluginMessages.GrantAccess)]
        [InlineData(PluginMessages.ModifyAccess)]
        [InlineData(PluginMessages.RevokeAccess)]
        [InlineData(AllMessagesProbe.CustomMessage)]
        public void Each_message_dispatches_to_its_handler(string message)
        {
            AllMessagesProbe probe = new AllMessagesProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(message)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .Execute(probe);

            Assert.Equal(message, probe.Seen);
        }
    }
}
