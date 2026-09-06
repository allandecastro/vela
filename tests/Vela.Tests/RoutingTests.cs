using System;
using Vela.Context;
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    public sealed class StepBuilderTests
    {
        [Fact]
        public void StepBuilder_CapturesMessage()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

            var definition = builder.Build();

            Assert.Equal(PluginMessages.Update, definition.Message);
            Assert.Equal(PluginStages.PostOperation, definition.Stage);
            Assert.Equal(ExecutionMode.Synchronous, definition.Mode);
            Assert.Equal("account", definition.EntityLogicalName);
        }

        [Fact]
        public void StepBuilder_CapturesFilteringAttributes()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

            var definition = builder.Build();

            Assert.Single(definition.FilteringAttributes);
            Assert.Equal("name", definition.FilteringAttributes[0]);
        }

        [Fact]
        public void StepBuilder_CapturesImages()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PreImage()
                    .Attributes(a => a.Name)
                .PostImage()
                    .Attributes(a => a.Name);

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
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Description("Test description");

            var definition = builder.Build();

            Assert.Equal("Test description", definition.Description);
        }

        [Fact]
        public void StepBuilder_CapturesHandlerMethod_FromMethodGroup()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(TestHandler);

            var definition = builder.Build();

            Assert.Equal(nameof(TestHandler), definition.HandlerMethodName);
        }

        [Fact]
        public void Sync_and_Async_are_shortcuts_for_Mode()
        {
            StepDefinition sync = new StepBuilder<TestAccount>().OnDelete().PostOperation().Sync().Build();
            Assert.Equal(ExecutionMode.Synchronous, sync.Mode);

            StepDefinition async = new StepBuilder<TestAccount>().OnDelete().PostOperation().Async().Build();
            Assert.Equal(ExecutionMode.Asynchronous, async.Mode);
        }

        // The typed stage chain now prevents an invalid image at compile time (a pre-image in PreValidation,
        // a post-image on Delete, a pre-image on Create, etc. do not compile). These positive tests confirm
        // the valid combinations the chain does expose.

        [Fact]
        public void StepBuilder_AllowsPreImageForPreOperationUpdate()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .PreOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PreImage()
                .Attributes(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PreImages);
        }

        [Fact]
        public void StepBuilder_AllowsPostImageForPostOperationCreate()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PostImage().Attributes(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PostImages);
        }

        [Fact]
        public void StepBuilder_AllowsPreImageForPostOperationDelete()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnDelete()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .PreImage()
                .Attributes(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PreImages);
        }

        // Runtime backstop: the permissive OnMessage(...) escape hatch exposes both images at PostOperation, so
        // a step assembled with a modelled message that the platform does not support at that stage is still
        // caught at Build() (regardless of how it was assembled). A non-modelled message is left permissive.

        [Fact]
        public void StepBuilder_Build_RejectsPostImageOnDelete_ViaPermissiveMessage()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new StepBuilder<TestAccount>().OnMessage(PluginMessages.Delete)
                    .PostOperation()
                    .Mode(ExecutionMode.Synchronous)
                    .PostImage()
                    .Build());
        }

        [Fact]
        public void StepBuilder_Build_AllowsImagesOnUnmodelledMessage()
        {
            StepDefinition definition = new StepBuilder<TestAccount>().OnMessage("acme_DoThing")
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .PreImage().Attributes(a => a.Name)
                .PostImage().Attributes(a => a.Name)
                .Build();

            Assert.Single(definition.PreImages);
            Assert.Single(definition.PostImages);
        }

        // A filtering-required message reached through the permissive OnMessage(...) escape hatch cannot supply
        // its filtering attribute (the permissive terminal has no FilteringAttributes), so Build() rejects it and
        // the error points at the typed OnUpdate()/OnCreate()/... methods instead.
        [Fact]
        public void StepBuilder_Build_RejectsFilteringMessageReachedViaOnMessage_AndSignpostsTypedMethods()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                new StepBuilder<TestAccount>().OnMessage(PluginMessages.Update)
                    .PostOperation()
                    .Mode(ExecutionMode.Synchronous)
                    .Build());

            Assert.Contains("OnUpdate()", ex.Message);
        }

        private void TestHandler(ILocalPluginContext ctx)
        {
        }
    }
}
