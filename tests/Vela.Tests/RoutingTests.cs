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
                .Stage(PluginStages.PostOperation)
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
                .Stage(PluginStages.PostOperation)
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
                .Stage(PluginStages.PostOperation)
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
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Description("Test description");

            var definition = builder.Build();

            Assert.Equal("Test description", definition.Description);
        }

        [Fact]
        public void StepBuilder_CapturesHandlerMethod()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(nameof(StepBuilder_CapturesHandlerMethod));

            var definition = builder.Build();

            Assert.Equal(nameof(StepBuilder_CapturesHandlerMethod), definition.HandlerMethodName);
        }

        [Fact]
        public void StepBuilder_CapturesHandlerMethod_FromMethodGroup()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(TestHandler);

            var definition = builder.Build();

            Assert.Equal(nameof(TestHandler), definition.HandlerMethodName);
        }

        [Fact]
        public void StepBuilder_DisallowsImagesInPreValidation()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .Stage(PluginStages.PreValidation)
                .Mode(ExecutionMode.Synchronous);

            Assert.Throws<InvalidOperationException>(() => builder.PreImage());
            Assert.Throws<InvalidOperationException>(() => builder.PostImage());
        }

        [Fact]
        public void StepBuilder_DisallowsMainOperationStage()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate();
            Assert.Throws<InvalidOperationException>(() => builder.Stage(PluginStages.MainOperation));
        }

        [Fact]
        public void StepBuilder_DisallowsPreImageForPreOperationCreate()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnCreate()
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous);

            Assert.Throws<InvalidOperationException>(() => builder.PreImage());
        }

        [Fact]
        public void StepBuilder_AllowsPreImageForPreOperationUpdate()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PreImage()
                .Attributes(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PreImages);
        }

        [Fact]
        public void StepBuilder_DisallowsPostImageForPreOperation()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate()
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous);

            Assert.Throws<InvalidOperationException>(() => builder.PostImage());
        }

        [Fact]
        public void StepBuilder_AllowsOnlyPostImageForPostOperationCreate()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnCreate()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

            Assert.Throws<InvalidOperationException>(() => builder.PreImage());
            builder.PostImage().Attributes(a => a.Name);

            var definition = builder.Build();
            Assert.Single(definition.PostImages);
        }

        [Fact]
        public void StepBuilder_AllowsOnlyPreImageForPostOperationDelete()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnDelete()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .PreImage()
                .Attributes(a => a.Name);

            Assert.Throws<InvalidOperationException>(() => builder.PostImage());
            var definition = builder.Build();
            Assert.Single(definition.PreImages);
        }

        private void TestHandler(ILocalPluginContext ctx, TestAccount target)
        {
        }
    }
}
