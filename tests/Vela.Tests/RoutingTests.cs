using System;
using Vela.Context;
using Vela.Constants;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Covers StepBuilder<TEntity> capture and the Build() backstop. The fluent transitions are Vela.Steps
    // interface members, so the builder is driven through IStepTrigger<T> while a reference to the concrete
    // StepBuilder<T> is kept to call Build() (the chain terminal is a void Handle(...) and cannot reach Build()).
    public sealed class StepBuilderTests
    {
        private static StepBuilder<TestAccount> New() => new StepBuilder<TestAccount>();

        private static IStepTrigger<TestAccount> Trigger(StepBuilder<TestAccount> builder) => builder;

        [Fact]
        public void StepBuilder_CapturesMessage()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name);

            var definition = builder.Build();

            Assert.Equal(PluginMessages.Update, definition.Message);
            Assert.Equal(PluginStages.PostOperation, definition.Stage);
            Assert.Equal(ExecutionMode.Synchronous, definition.Mode);
            Assert.Equal("account", definition.EntityLogicalName);
        }

        [Fact]
        public void StepBuilder_CapturesFilteringAttributes()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name);

            var definition = builder.Build();

            Assert.Single(definition.FilteringAttributes);
            Assert.Equal("name", definition.FilteringAttributes[0]);
        }

        [Fact]
        public void StepBuilder_CapturesImages()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Name);

            var definition = builder.Build();

            Assert.Single(definition.PreImages);
            Assert.Equal("PreImage", definition.PreImages[0].Alias);
            Assert.Contains("name", definition.PreImages[0].Attributes);

            Assert.Single(definition.PostImages);
            Assert.Equal("PostImage", definition.PostImages[0].Alias);
            Assert.Contains("name", definition.PostImages[0].Attributes);
        }

        [Fact]
        public void StepBuilder_CapturesDescription()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithDescription("Test description");

            var definition = builder.Build();

            Assert.Equal("Test description", definition.Description);
        }

        [Fact]
        public void StepBuilder_CapturesHandlerMethod_FromMethodGroup()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .Handle(TestHandler);

            var definition = builder.Build();

            Assert.Equal(nameof(TestHandler), definition.HandlerMethodName);
        }

        [Fact]
        public void Sync_and_Async_set_the_mode()
        {
            StepBuilder<TestAccount> syncBuilder = New();
            Trigger(syncBuilder).OnDelete().PostOperation().Sync();
            Assert.Equal(ExecutionMode.Synchronous, syncBuilder.Build().Mode);

            StepBuilder<TestAccount> asyncBuilder = New();
            Trigger(asyncBuilder).OnDelete().PostOperation().Async();
            Assert.Equal(ExecutionMode.Asynchronous, asyncBuilder.Build().Mode);
        }

        // The typed stage chain now prevents an invalid image at compile time (a pre-image in PreValidation,
        // a post-image on Delete, a pre-image on Create, etc. do not compile). These positive tests confirm
        // the valid combinations the chain does expose.

        [Fact]
        public void StepBuilder_AllowsPreImageForPreOperationUpdate()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate()
                .PreOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PreImages);
        }

        [Fact]
        public void StepBuilder_AllowsPostImageForPostOperationCreate()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnCreate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PostImages);
        }

        [Fact]
        public void StepBuilder_AllowsPreImageForPostOperationDelete()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnDelete()
                .PostOperation()
                .Sync()
                .WithPreImage(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PreImages);
        }

        // Runtime backstop: the permissive OnMessage(...) escape hatch exposes both images at PostOperation, so
        // a step assembled with a modelled message that the platform does not support at that stage is still
        // caught at Build() (regardless of how it was assembled). A non-modelled message is left permissive.

        [Fact]
        public void StepBuilder_Build_RejectsPostImageOnDelete_ViaPermissiveMessage()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnMessage(PluginMessages.Delete)
                .PostOperation()
                .Sync()
                .WithPostImage();

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void StepBuilder_Build_AllowsImagesOnUnmodelledMessage()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnMessage("acme_DoThing")
                .PostOperation()
                .Sync()
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Name);

            StepDefinition definition = builder.Build();

            Assert.Single(definition.PreImages);
            Assert.Single(definition.PostImages);
        }

        // A filtering-required message reached through the permissive OnMessage(...) escape hatch cannot supply
        // its filtering attribute (the permissive terminal has no FilterOn), so Build() rejects it and
        // the error points at the typed OnUpdate()/OnCreate()/... methods instead.
        [Fact]
        public void StepBuilder_Build_RejectsFilteringMessageReachedViaOnMessage_AndSignpostsTypedMethods()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnMessage(PluginMessages.Update)
                .PostOperation()
                .Sync();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("OnUpdate()", ex.Message);
        }

        private void TestHandler(IUpdatePostContext<TestAccount> ctx)
        {
        }
    }
}
