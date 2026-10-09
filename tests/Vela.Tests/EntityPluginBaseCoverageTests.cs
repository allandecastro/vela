using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers EntityPluginBase<TEntity> step-definition assembly branches: the no-step guard, auto step-key
// generation, the duplicate-trigger and missing-handler validations, the type-name fallback, and the
// RegisterStep()-outside-RegisterPluginSteps guard.
public sealed class EntityPluginBaseCoverageTests
{
    private sealed class NoNameEntity : Entity
    {
        public NoNameEntity() : base("placeholder") { }
    }

    private sealed class RegistersNothing(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public RegistersNothing() : this(null!, null!) { }
        protected override void RegisterPluginSteps() { } // registers no steps
    }

    private sealed class SingleAutoKeyStep(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public SingleAutoKeyStep() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name) // no .WithStepKey() -> auto-generated
                .Handle(OnUpdate);
        private void OnUpdate(IUpdatePostContext<TestAccount> ctx) { }
    }

    private sealed class DuplicateTriggerSteps(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public DuplicateTriggerSteps() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name);
        }
    }

    private sealed class MultiStepMissingHandler(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public MultiStepMissingHandler() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
        {
            // Distinct triggers (different stages) pass the trigger check, but neither binds a handler.
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name);
        }
    }

    private sealed class NoNamePlugin(string u, string s) : EntityPluginBase<NoNameEntity>(u, s)
    {
        public NoNamePlugin() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PostOperation().Sync().Handle(OnDelete);
        private void OnDelete(IDeletePostContext<NoNameEntity> ctx) { }
    }

    private sealed class RegisterOutside(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public RegisterOutside() : this(null!, null!) { }
        public void CallRegisterStepsOutside() => RegisterStep(); // illegal: not inside RegisterPluginSteps()
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdate);
        private void OnUpdate(IUpdatePostContext<TestAccount> ctx) { }
    }

    [Fact]
    public void A_plugin_that_registers_no_steps_throws()
    {
        Assert.Throws<InvalidPluginExecutionException>(() => new RegistersNothing().GetStepDefinitions());
    }

    [Fact]
    public void A_step_without_an_explicit_key_gets_an_auto_generated_one()
    {
        StepDefinition definition = new SingleAutoKeyStep().GetStepDefinition();
        Assert.StartsWith("dv-", definition.StepKey);
        Assert.Equal(15, definition.StepKey.Length); // "dv-" + 12 hex chars
    }

    [Fact]
    public void Two_indistinguishable_steps_on_one_trigger_are_rejected()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new DuplicateTriggerSteps().GetStepDefinitions());
        Assert.Contains("same trigger", ex.Message);
    }

    [Fact]
    public void Multiple_steps_each_require_a_handler()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new MultiStepMissingHandler().GetStepDefinitions());
        Assert.Contains("no handler", ex.Message);
    }

    [Fact]
    public void Entity_logical_name_falls_back_to_the_type_name()
    {
        StepDefinition definition = new NoNamePlugin().GetStepDefinition();
        Assert.Equal("nonameentity", definition.EntityLogicalName);
    }

    [Fact]
    public void RegisterStep_outside_RegisterPluginSteps_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new RegisterOutside().CallRegisterStepsOutside());
    }
}
