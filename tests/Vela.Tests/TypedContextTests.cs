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

            IMergePostContext<TestAccount> ctx = new MergePostContext<TestAccount>(inner);

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

            ICreatePostContext<TestAccount> ctx = new CreatePostContext<TestAccount>(inner);

            Assert.Equal(id, ctx.Response.id);
        }

        [Fact]
        public void TypedContext_ForwardsTheLocalPluginContextSurface()
        {
            LocalPluginContext inner = new PluginContextBuilder()
                .OnUpdate()
                .Stage(Vela.Constants.PluginStages.PostOperation)
                .Build();

            IUpdatePostContext<TestAccount> ctx = new UpdatePostContext<TestAccount>(inner);

            // Delegated members reflect the wrapped context.
            Assert.Equal(inner.MessageName, ctx.MessageName);
            Assert.Equal(inner.Stage, ctx.Stage);
            Assert.Same(inner.PluginExecutionContext, ctx.PluginExecutionContext);
            ctx.Trace("no throw");
        }

        [Fact]
        public void StepContext_NullInner_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MergePostContext<TestAccount>(null!));
        }

        [Fact]
        public void TypedContext_ResolvesServices_ThroughTheWrappedResolver()
        {
            // Regression: dependency injection (ctx.GetService / ctx.GetRequiredService) must work from a typed
            // handler, not only the plain LocalPluginContext. The StepContext wrapper forwards the resolver.
            LocalPluginContext inner = new PluginContextBuilder().Build();
            ICreatePostContext<TestAccount> ctx = new CreatePostContext<TestAccount>(inner);

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

            IUpdatePostContext<TestAccount> ctx = new UpdatePostContext<TestAccount>(inner);

            // ctx.Request.Target is the early-bound entity - no cast, no type argument.
            TestAccount target = ctx.Request.Target;
            Assert.Equal(id, target.Id);
            Assert.Equal("Zava", target.Name);
        }

        [Fact]
        public void DeleteContext_ExposesTheReferenceTargetViaRequest()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", id);

            IDeletePostContext<TestAccount> ctx = new DeletePostContext<TestAccount>(inner);

            Assert.Equal(id, ctx.Request.Target.Id);
            Assert.Equal("account", ctx.Request.Target.LogicalName);
        }

        [Fact]
        public void SendEmailFromTemplateContext_ExposesTheTypedTarget()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new TestAccount { Id = id, Name = "Regarding" };

            ISendEmailFromTemplatePostContext<TestAccount> ctx = new SendEmailFromTemplatePostContext<TestAccount>(inner);

            // ctx.Request.Target is the base SDK Entity; read the record early-bound via the GetTarget<T> extension.
            TestAccount target = ctx.GetTarget<TestAccount>();
            Assert.Equal(id, target.Id);
            Assert.Equal("Regarding", target.Name);
        }

        [Fact]
        public void AddToQueueContext_ExposesTheReferenceTargetViaRequest()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            Guid id = Guid.NewGuid();
            inner.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", id);

            IAddToQueuePostContext<TestAccount> ctx = new AddToQueuePostContext<TestAccount>(inner);

            Assert.Equal(id, ctx.Request.Target.Id);
        }

        [Fact]
        public void CreateMultipleContext_ExposesTheTargetsViaRequest()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            EntityCollection targets = new EntityCollection();
            targets.Entities.Add(new TestAccount { Id = Guid.NewGuid(), Name = "A" });
            targets.Entities.Add(new TestAccount { Id = Guid.NewGuid(), Name = "B" });
            inner.PluginExecutionContext.InputParameters["Targets"] = targets;

            ICreateMultiplePostContext<TestAccount> ctx = new CreateMultiplePostContext<TestAccount>(inner);

            Assert.Equal(2, ctx.Request.Targets.Entities.Count);
        }

        [Fact]
        public void CreatePostContext_ExposesTheTypedPostImage()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            inner.PluginExecutionContext.PostEntityImages["PostImage"] =
                new TestAccount { Id = Guid.NewGuid(), Name = "Created" };

            ICreatePostContext<TestAccount> ctx = new CreatePostContext<TestAccount>(inner);

            // ctx.PostImage is the early-bound entity for the default alias - no type argument.
            Assert.Equal("Created", ctx.PostImage!.Name);
        }

        [Fact]
        public void UpdatePreContext_ExposesTheTypedPreImage()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            inner.PluginExecutionContext.PreEntityImages["PreImage"] =
                new TestAccount { Id = Guid.NewGuid(), Name = "Before" };

            IUpdatePreContext<TestAccount> ctx = new UpdatePreContext<TestAccount>(inner);

            Assert.Equal("Before", ctx.PreImage!.Name);
        }

        [Fact]
        public void UpdatePostContext_ExposesTheTypedPreImageAndPostImage()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            inner.PluginExecutionContext.PreEntityImages["PreImage"] =
                new TestAccount { Id = Guid.NewGuid(), Name = "Before" };
            inner.PluginExecutionContext.PostEntityImages["PostImage"] =
                new TestAccount { Id = Guid.NewGuid(), Name = "After" };

            IUpdatePostContext<TestAccount> ctx = new UpdatePostContext<TestAccount>(inner);

            Assert.Equal("Before", ctx.PreImage!.Name);
            Assert.Equal("After", ctx.PostImage!.Name);
        }

        [Fact]
        public void DeleteContexts_ExposeTheTypedPreImage()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            inner.PluginExecutionContext.PreEntityImages["PreImage"] =
                new TestAccount { Id = Guid.NewGuid(), Name = "Doomed" };

            IDeletePreContext<TestAccount> pre = new DeletePreContext<TestAccount>(inner);
            IDeletePostContext<TestAccount> post = new DeletePostContext<TestAccount>(inner);

            Assert.Equal("Doomed", pre.PreImage!.Name);
            Assert.Equal("Doomed", post.PreImage!.Name);
        }

        [Fact]
        public void TypedImage_IsNull_WhenNoImageRegistered()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();

            IUpdatePostContext<TestAccount> ctx = new UpdatePostContext<TestAccount>(inner);

            Assert.Null(ctx.PreImage);
            Assert.Null(ctx.PostImage);
        }
    }
}
