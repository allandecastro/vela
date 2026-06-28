using System;
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    /// <summary>
    /// Covers the Create/Update filtering requirement and the duplicate-detection guards in
    /// <see cref="StepBuilder{TEntity}"/>. The compile-time type-state forces filtering only when the fluent
    /// chain is consumed; the framework registers steps by side-effect and calls <c>Build()</c> itself, so the
    /// hard guarantee lives in <c>Build()</c> — which is what these tests exercise (via the discard pattern).
    /// </summary>
    public sealed class FilteringEnforcementTests
    {
        [Fact]
        public void Update_WithoutFiltering_ThrowsOnBuild()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("filtering attribute", ex.Message);
        }

        [Fact]
        public void Create_WithoutFiltering_ThrowsOnBuild()
        {
            var builder = new StepBuilder<TestAccount>();
            builder.OnCreate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("filtering attribute", ex.Message);
        }

        [Fact]
        public void Update_WithFiltering_Builds()
        {
            var definition = new StepBuilder<TestAccount>()
                .OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Build();

            Assert.Equal(new[] { "name" }, definition.FilteringAttributes);
        }

        [Fact]
        public void Delete_WithoutFiltering_Builds()
        {
            var definition = new StepBuilder<TestAccount>()
                .OnDelete().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
                .Build();

            Assert.Empty(definition.FilteringAttributes);
            Assert.Equal(PluginMessages.Delete, definition.Message);
        }

        [Fact]
        public void DuplicateFilteringAttribute_ThrowsOnBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new StepBuilder<TestAccount>()
                    .OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
                    .FilteringAttributes(a => a.Name, a => a.Name)
                    .Build());

            Assert.Contains("Duplicate filtering attribute", ex.Message);
        }

        [Fact]
        public void DuplicateImageAttribute_ThrowsOnBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new StepBuilder<TestAccount>()
                    .OnUpdate().Stage(PluginStages.PreOperation).Mode(ExecutionMode.Synchronous)
                    .FilteringAttributes(a => a.Name)
                    .PreImage("img").Attributes(a => a.Name, a => a.Name)
                    .Build());

            Assert.Contains("Duplicate attribute in image 'img'", ex.Message);
        }

        [Fact]
        public void DuplicateImageAlias_ThrowsOnBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new StepBuilder<TestAccount>()
                    .OnUpdate().Stage(PluginStages.PreOperation).Mode(ExecutionMode.Synchronous)
                    .FilteringAttributes(a => a.Name)
                    .PreImage("dup").Attributes(a => a.Name)
                    .PreImage("dup").Attributes(a => a.AccountId)
                    .Build());

            Assert.Contains("Duplicate image alias", ex.Message);
        }
    }
}
