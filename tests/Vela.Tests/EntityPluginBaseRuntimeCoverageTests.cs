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
            => RegisterStep().OnUpdate().PostOperation().Sync().WithFiltering(a => a.Name);
        protected override void Execute(ILocalPluginContext ctx) { Seen = ctx.GetTarget<TestAccount>(); Ran = true; }
    }

    // Two handler-backed steps (distinct triggers) so an unmatched message hits the "multiple steps" guard.
    private sealed class TwoHandlerSteps(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public TwoHandlerSteps() : this(string.Empty, string.Empty) { }
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync().WithFiltering(a => a.Name).Handle(HandleA);
            RegisterStep().OnUpdate().PostOperation().Sync().WithFiltering(a => a.Name).Handle(HandleB);
        }
        public void HandleA(ILocalPluginContext ctx) { }
        public void HandleB(ILocalPluginContext ctx) { }
    }

    [Fact]
    public void Single_step_falls_back_to_Execute_when_no_step_matches_the_message()
    {
        Probe plugin = new Probe();
        // The step is on Update; run a Create context -> 0 matches -> Execute(ctx) still runs.
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnCreate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount { Name = "Acme" })
            .BuildServiceProvider();

        plugin.Execute(provider);
        Assert.True(plugin.Ran);
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
