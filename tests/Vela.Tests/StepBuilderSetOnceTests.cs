using System;
using System.Linq;
using Vela.Constants;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Runtime backstop for the "set a single-value modifier at most once" rule. The VELA003 analyzer catches the
    // inline-chain case at build time; these exercise the runtime guard through a chain split across a local (the
    // case the analyzer deliberately leaves to the runtime), so the test code itself stays analyzer-clean.
    public class StepBuilderSetOnceTests
    {
        private sealed class DuplicateExecutionOrderPlugin : EntityPluginBase<TestAccount>
        {
            public DuplicateExecutionOrderPlugin() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                var step = RegisterStep().OnUpdate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .WithExecutionOrder(10);
                step.WithExecutionOrder(20).Handle(ctx => { }); // throws at the second WithExecutionOrder
            }
        }

        private sealed class DuplicateFilterOnPlugin : EntityPluginBase<TestAccount>
        {
            public DuplicateFilterOnPlugin() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                var step = RegisterStep().OnUpdate().PreOperation().Sync()
                    .FilterOn(a => a.Name);
                step.FilterOn(a => a.AccountId).Handle(ctx => { }); // throws at the second FilterOn
            }
        }

        private sealed class DisabledTwicePlugin : EntityPluginBase<TestAccount>
        {
            public DisabledTwicePlugin() : base(string.Empty, string.Empty) { }

            // Disabled() is a flag toggle, exempt from set-once: calling it twice is a harmless no-op.
            protected override void RegisterPluginSteps() =>
                RegisterStep().OnUpdate().PreOperation().Sync()
                    .FilterOn(a => a.Name)
                    .Disabled()
                    .Disabled()
                    .Handle(ctx => { });
        }

        [Fact]
        public void Setting_WithExecutionOrder_twice_throws()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => new DuplicateExecutionOrderPlugin().GetStepDefinitions());
            Assert.Contains("WithExecutionOrder", ex.Message);
            Assert.Contains("at most once", ex.Message);
        }

        [Fact]
        public void Setting_FilterOn_twice_throws()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => new DuplicateFilterOnPlugin().GetStepDefinitions());
            Assert.Contains("FilterOn", ex.Message);
            Assert.Contains("at most once", ex.Message);
        }

        [Fact]
        public void Disabled_twice_is_allowed()
        {
            StepDefinition def = Assert.Single(new DisabledTwicePlugin().GetStepDefinitions());
            Assert.False(def.Enabled);
        }
    }
}
