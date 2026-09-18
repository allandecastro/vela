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
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdate);
        private void OnUpdate(IUpdatePostContext<TestAccount> ctx) { Seen = ctx.GetTarget<TestAccount>(); Ran = true; }
    }

    // Two handler-backed steps (distinct triggers) so an unmatched message hits the "multiple steps" guard.
    private sealed class TwoHandlerSteps(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        public TwoHandlerSteps() : this(string.Empty, string.Empty) { }
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(HandleA);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(HandleB);
        }
        public void HandleA(ILocalPluginContext ctx) { }
        public void HandleB(ILocalPluginContext ctx) { }
    }

    [Fact]
    public void No_matching_step_throws()
    {
        Probe plugin = new Probe();
        // The step is on Update; run a Create context -> 0 matches -> a clear error (no Execute fallback).
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnCreate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount { Name = "Acme" })
            .BuildServiceProvider();

        Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));
        Assert.False(plugin.Ran);
    }

    [Fact]
    public void No_matching_step_throws_even_with_multiple_steps()
    {
        TwoHandlerSteps plugin = new TwoHandlerSteps();
        // Both steps are on Update; a Delete context matches neither.
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnDelete().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithInputParameter("Target", new EntityReference("account", Guid.NewGuid()))
            .BuildServiceProvider();

        Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));
    }
}
