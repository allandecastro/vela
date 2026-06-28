using System;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Drives EntityPluginBase<TEntity> through plugin.Execute(...) to cover the runtime target-resolution and
// step-matching branches.
public sealed class EntityPluginBaseRuntimeCoverageTests
{
    // Single step on Update; records the typed target it received.
    private sealed class Probe(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public Probe() : this(string.Empty, string.Empty) { }
        public TestAccount? Seen { get; private set; }
        public bool Ran { get; private set; }
        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name);
        protected override void Execute(ILocalPluginContext ctx, TestAccount target) { Seen = target; Ran = true; }
    }

    // SetState carries an EntityMoniker rather than a Target.
    private sealed class SetStateProbe(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public SetStateProbe() : this(string.Empty, string.Empty) { }
        public Guid SeenId { get; private set; }
        protected override void RegisterPluginSteps()
            => RegisterSteps().OnMessage(PluginMessages.SetState).Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous);
        protected override void Execute(ILocalPluginContext ctx, TestAccount target) => SeenId = target.Id;
    }

    // Two handler-backed steps (distinct triggers) so an unmatched message hits the "multiple steps" guard.
    private sealed class TwoHandlerSteps(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public TwoHandlerSteps() : this(string.Empty, string.Empty) { }
        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate().Stage(PluginStages.PreOperation).Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(nameof(HandleA));
            RegisterSteps().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(nameof(HandleB));
        }
        public void HandleA(ILocalPluginContext ctx, TestAccount target) { }
        public void HandleB(ILocalPluginContext ctx, TestAccount target) { }
    }

    [Fact]
    public void Single_step_falls_back_to_Execute_when_no_step_matches_the_message()
    {
        Probe plugin = new Probe();
        // The step is on Update; run a Create context -> 0 matches -> Execute(ctx, target) still runs.
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnCreate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount { Name = "Acme" })
            .BuildServiceProvider();

        plugin.Execute(provider);
        Assert.True(plugin.Ran);
    }

    [Fact]
    public void Target_is_built_from_an_entity_moniker_for_set_state()
    {
        Guid id = Guid.NewGuid();
        SetStateProbe plugin = new SetStateProbe();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnMessage(PluginMessages.SetState).Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithInputParameter("EntityMoniker", new EntityReference("account", id))
            .BuildServiceProvider();

        plugin.Execute(provider);
        Assert.Equal(id, plugin.SeenId);
    }

    [Fact]
    public void Target_reference_with_blank_logical_name_falls_back_to_the_entity_type_name()
    {
        Guid id = Guid.NewGuid();
        SetStateProbe plugin = new SetStateProbe();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnMessage(PluginMessages.SetState).Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithInputParameter("EntityMoniker", new EntityReference(string.Empty, id)) // blank logical name
            .BuildServiceProvider();

        plugin.Execute(provider);
        Assert.Equal(id, plugin.SeenId);
    }

    [Fact]
    public void Multiple_steps_with_no_matching_step_raise_the_multi_step_guard()
    {
        TwoHandlerSteps plugin = new TwoHandlerSteps();
        // Both steps are on Update; a Delete context matches neither, leaving >1 definitions unmatched.
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnDelete().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithInputParameter("Target", new EntityReference("account", Guid.NewGuid()))
            .BuildServiceProvider();

        Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));
    }
}
