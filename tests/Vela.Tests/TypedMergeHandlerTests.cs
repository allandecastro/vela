using System;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // Proves the Shape B vertical slice: a multi-step plugin binds an OnMerge step to a typed handler
    // (IMergeContext<TestAccount>), and the base dispatch builds the typed context and hands it over - so the
    // handler reads ctx.Request.SubordinateId (typed, inferred) at run time.
    public sealed class TypedMergeHandlerTests
    {
        private sealed class MergeProbe : EntityPluginBase<TestAccount>
        {
            public Guid CapturedSubordinate;

            public MergeProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterSteps().OnMerge().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnMerged);
            }

            private void OnMerged(IMergeContext<TestAccount> ctx)
            {
                CapturedSubordinate = ctx.Request.SubordinateId;   // inferred, typed
            }
        }

        [Fact]
        public void Merge_step_handler_receives_the_typed_merge_context()
        {
            Guid subordinate = Guid.NewGuid();
            MergeProbe probe = new MergeProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(PluginMessages.Merge)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter("Target", new EntityReference(TestAccount.EntityLogicalNameValue, Guid.NewGuid()))
                .WithInputParameter("SubordinateId", subordinate)
                .Execute(probe);

            Assert.Equal(subordinate, probe.CapturedSubordinate);
        }
    }
}
