using System;
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    /// <summary>
    /// Covers the Create/Update filtering requirement and the duplicate-detection guards in
    /// <see cref="StepBuilder{TEntity}"/>. The compile-time type-state forces filtering only when the fluent
    /// chain is consumed; the framework registers steps by side-effect and calls <c>Build()</c> itself, so the
    /// hard guarantee lives in <c>Build()</c> - which is what these tests exercise. The transitions are
    /// Vela.Steps interface members, so the builder is driven through <see cref="IStepTrigger{TEntity}"/> while
    /// a reference to the concrete builder is kept to call <c>Build()</c>.
    /// </summary>
    public sealed class FilteringEnforcementTests
    {
        private static StepBuilder<TestAccount> New() => new StepBuilder<TestAccount>();

        private static IStepTrigger<TestAccount> Trigger(StepBuilder<TestAccount> builder) => builder;

        [Fact]
        public void Update_WithoutFiltering_ThrowsOnBuild()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate().PostOperation().Sync();

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("filtering attribute", ex.Message);
        }

        [Fact]
        public void Create_WithoutFiltering_ThrowsOnBuild()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnCreate().PostOperation().Sync();

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("filtering attribute", ex.Message);
        }

        [Fact]
        public void Update_WithFiltering_Builds()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name);

            StepDefinition definition = builder.Build();

            Assert.Equal(new[] { "name" }, definition.FilteringAttributes);
        }

        [Fact]
        public void Delete_WithoutFiltering_Builds()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnDelete().PostOperation().Sync();

            StepDefinition definition = builder.Build();

            Assert.Empty(definition.FilteringAttributes);
            Assert.Equal(PluginMessages.Delete, definition.Message);
        }

        [Fact]
        public void DuplicateFilteringAttribute_ThrowsOnBuild()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name, a => a.Name);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("Duplicate filtering attribute", ex.Message);
        }

        [Fact]
        public void DuplicateImageAttribute_ThrowsOnBuild()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage("img", a => a.Name, a => a.Name);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("Duplicate attribute in image 'img'", ex.Message);
        }

        [Fact]
        public void DuplicateImageAlias_ThrowsOnBuild()
        {
            StepBuilder<TestAccount> builder = New();
            Trigger(builder).OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage("dup", a => a.Name)
                .WithPreImage("dup", a => a.AccountId);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("Duplicate image alias", ex.Message);
        }
    }
}
