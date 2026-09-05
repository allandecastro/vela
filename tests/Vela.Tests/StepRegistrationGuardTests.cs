using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class StepRegistrationGuardTests
{
    [Fact]
    public void Two_steps_with_the_same_trigger_throw_a_clear_error()
    {
        // Same message + stage + mode + entity can't be told apart at runtime; the build must reject it.
        DuplicateTriggerPlugin plugin = new DuplicateTriggerPlugin();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => plugin.GetStepDefinitions());
        Assert.Contains("same trigger", ex.Message);
    }

    [Fact]
    public void Steps_with_different_triggers_build_fine()
    {
        // The router pattern (distinct messages) stays valid.
        RouterPlugin plugin = new RouterPlugin();

        StepDefinition[] definitions = plugin.GetStepDefinitions();

        Assert.Equal(2, definitions.Length);
    }

    private sealed class DuplicateTriggerPlugin : EntityPluginBase<TestAccount>
    {
        public DuplicateTriggerPlugin() : base(string.Empty, string.Empty) { }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name).Handler(First);
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name).Handler(Second);
        }

        private void First(ILocalPluginContext ctx) { }

        private void Second(ILocalPluginContext ctx) { }
    }

    private sealed class RouterPlugin : EntityPluginBase<TestAccount>
    {
        public RouterPlugin() : base(string.Empty, string.Empty) { }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name).Handler(OnCreated);
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name).Handler(OnUpdated);
        }

        private void OnCreated(ILocalPluginContext ctx) { }

        private void OnUpdated(ILocalPluginContext ctx) { }
    }
}
