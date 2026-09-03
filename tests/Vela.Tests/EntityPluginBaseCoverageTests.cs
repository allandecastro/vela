using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers EntityPluginBase<TEntity> step-definition assembly branches: the no-step guard, auto step-key
// generation, the duplicate-trigger and missing-handler validations, the type-name fallback, and the
// RegisterSteps()-outside-RegisterPluginSteps guard.
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
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name); // no .Key() -> auto-generated
        protected override void Execute(ILocalPluginContext ctx, TestAccount target) { }
    }

    private sealed class DuplicateTriggerSteps(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public DuplicateTriggerSteps() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name);
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.AccountId);
        }
        protected override void Execute(ILocalPluginContext ctx, TestAccount target) { }
    }

    private sealed class MultiStepMissingHandler(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public MultiStepMissingHandler() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
        {
            // Distinct triggers (different stages) pass the trigger check, but neither declares a Handler.
            RegisterSteps().OnUpdate().PreOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name);
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name);
        }
        protected override void Execute(ILocalPluginContext ctx, TestAccount target) { }
    }

    private sealed class NoNamePlugin(string u, string s) : EntityPluginBase<NoNameEntity>(u, s)
    {
        public NoNamePlugin() : this(null!, null!) { }
        protected override void RegisterPluginSteps()
            => RegisterSteps().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous);
        protected override void Execute(ILocalPluginContext ctx, NoNameEntity target) { }
    }

    private sealed class RegisterOutside(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public RegisterOutside() : this(null!, null!) { }
        public void CallRegisterStepsOutside() => RegisterSteps(); // illegal: not inside RegisterPluginSteps()
        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name);
        protected override void Execute(ILocalPluginContext ctx, TestAccount target) { }
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
    public void Two_steps_with_the_same_trigger_are_rejected()
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
        Assert.Contains("missing Handler", ex.Message);
    }

    [Fact]
    public void Entity_logical_name_falls_back_to_the_type_name()
    {
        StepDefinition definition = new NoNamePlugin().GetStepDefinition();
        Assert.Equal("nonameentity", definition.EntityLogicalName);
    }

    [Fact]
    public void RegisterSteps_outside_RegisterPluginSteps_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new RegisterOutside().CallRegisterStepsOutside());
    }
}
