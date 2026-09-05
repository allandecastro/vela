using System;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // Covers the per-message typed contexts (StepContext / I{Msg}Context): ctx.Request / ctx.Response are typed
    // and inferred (no manual type argument), and the whole ILocalPluginContext surface is forwarded to the
    // wrapped runtime context.
    public sealed class TypedContextTests
    {
        [Fact]
        public void MergeContext_ExposesTypedRequest()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid subordinate = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", Guid.NewGuid());
            inner.PluginExecutionContext.InputParameters["SubordinateId"] = subordinate;

            IMergeContext<TestAccount> ctx = new MergeContext<TestAccount>(inner);

            // Inferred - no type argument on Request.
            var req = ctx.Request;
            Assert.Equal(subordinate, req.SubordinateId);
            Assert.Equal("account", req.Target.LogicalName);
        }

        [Fact]
        public void CreateContext_ExposesTypedResponse()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.OutputParameters["id"] = id;

            ICreateContext<TestAccount> ctx = new CreateContext<TestAccount>(inner);

            Assert.Equal(id, ctx.Response.id);
        }

        [Fact]
        public void TypedContext_ForwardsTheLocalPluginContextSurface()
        {
            LocalPluginContext inner = new PluginContextBuilder()
                .OnUpdate()
                .Stage(Vela.Constants.PluginStages.PostOperation)
                .Build();

            IUpdateContext<TestAccount> ctx = new UpdateContext<TestAccount>(inner);

            // Delegated members reflect the wrapped context.
            Assert.Equal(inner.MessageName, ctx.MessageName);
            Assert.Equal(inner.Stage, ctx.Stage);
            Assert.Same(inner.PluginExecutionContext, ctx.PluginExecutionContext);
            ctx.Trace("no throw");
        }

        [Fact]
        public void StepContext_NullInner_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MergeContext<TestAccount>(null!));
        }
    }
}
