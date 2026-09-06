using System;
using Vela.Caching;
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

        [Fact]
        public void TypedContext_ResolvesServices_ThroughTheWrappedResolver()
        {
            // Regression: dependency injection (ctx.GetService / ctx.GetRequiredService) must work from a typed
            // handler, not only the plain LocalPluginContext. The StepContext wrapper forwards the resolver.
            LocalPluginContext inner = new PluginContextBuilder().Build();
            ICreateContext<TestAccount> ctx = new CreateContext<TestAccount>(inner);

            // IPluginCacheService is a built-in service, always registered.
            Assert.NotNull(ctx.GetService<IPluginCacheService>());
            Assert.NotNull(ctx.GetRequiredService<IPluginCacheService>());
        }

        [Fact]
        public void EntityTargetContext_ExposesTheTypedTarget()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new TestAccount { Id = id, Name = "Zava" };

            IUpdateContext<TestAccount> ctx = new UpdateContext<TestAccount>(inner);

            // ctx.Target is the early-bound entity - no cast, no type argument.
            TestAccount target = ctx.Target;
            Assert.Equal(id, target.Id);
            Assert.Equal("Zava", target.Name);
        }

        [Fact]
        public void ReferenceTargetContext_ExposesTheTargetReference()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", id);

            IDeleteContext<TestAccount> ctx = new DeleteContext<TestAccount>(inner);

            Assert.Equal(id, ctx.TargetReference.Id);
            Assert.Equal("account", ctx.TargetReference.LogicalName);
        }

        [Fact]
        public void SendEmailFromTemplateContext_ExposesTheTypedTarget()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new TestAccount { Id = id, Name = "Regarding" };

            ISendEmailFromTemplateContext<TestAccount> ctx = new SendEmailFromTemplateContext<TestAccount>(inner);

            Assert.Equal(id, ctx.Target.Id);
            Assert.Equal("Regarding", ctx.Target.Name);
        }

        [Fact]
        public void AddToQueueContext_ExposesTheTargetReference()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", id);

            IAddToQueueContext<TestAccount> ctx = new AddToQueueContext<TestAccount>(inner);

            Assert.Equal(id, ctx.TargetReference.Id);
        }

        [Fact]
        public void CollectionTargetContext_ExposesTheTargetsCollection()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            EntityCollection targets = new EntityCollection();
            targets.Entities.Add(new TestAccount { Id = Guid.NewGuid(), Name = "A" });
            targets.Entities.Add(new TestAccount { Id = Guid.NewGuid(), Name = "B" });
            inner.PluginExecutionContext.InputParameters["Targets"] = targets;

            ICreateMultipleContext<TestAccount> ctx = new CreateMultipleContext<TestAccount>(inner);

            Assert.Equal(2, ctx.TargetCollection.Entities.Count);
        }
    }
}
