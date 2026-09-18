using System;
using Vela.Constants;
using Vela.Context;
using Vela.DI;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // Proves the Shape B vertical slice: a multi-step plugin binds an OnMerge step to a typed handler
    // (IMergePostContext<TestAccount>), and the base dispatch builds the typed context and hands it over - so the
    // handler reads ctx.Request.SubordinateId (typed, inferred) at run time.
    public sealed class TypedMergeHandlerTests
    {
        private sealed class MergeProbe : EntityPluginBase<TestAccount>
        {
            public Guid CapturedSubordinate;

            public MergeProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnMerge().PostOperation().Sync().Handle(OnMerged);
            }

            private void OnMerged(IMergePostContext<TestAccount> ctx)
            {
                CapturedSubordinate = ctx.Request.SubordinateId;   // inferred, typed
            }
        }

        private sealed class UpdateProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedName;

            public UpdateProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate().PostOperation().Sync()
                    .WithFiltering(a => a.Name).Handle(OnUpdated);
            }

            private void OnUpdated(IUpdatePostContext<TestAccount> ctx)
            {
                CapturedName = ctx.Request.Target.Name;   // typed, inferred, no cast
            }
        }

        // A PreOperation step receives the PRE context: ctx.Request is available; ctx.Response is not
        // even on the type (IUpdatePreContext has no Response), so reading it in a pre-stage does not compile.
        private sealed class UpdatePreProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedName;

            public UpdatePreProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PreOperation().Sync().WithFiltering(a => a.Name).Handle(OnUpdating);

            private void OnUpdating(IUpdatePreContext<TestAccount> ctx)
                => CapturedName = ctx.Request.Target.Name;   // Request-side data, available in pre
        }

        [Fact]
        public void Pre_stage_handler_receives_the_pre_context()
        {
            UpdatePreProbe probe = new UpdatePreProbe();

            PluginContext.For<TestAccount>()
                .OnUpdate().Stage(PluginStages.PreOperation).Sync()
                .WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Renamed" })
                .Execute(probe);

            Assert.Equal("Renamed", probe.CapturedName);
        }

        [Fact]
        public void Update_step_handler_receives_the_typed_update_context()
        {
            UpdateProbe probe = new UpdateProbe();

            PluginContext.For<TestAccount>()
                .OnUpdate()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Renamed" })
                .Execute(probe);

            Assert.Equal("Renamed", probe.CapturedName);
        }

        [Fact]
        public void Merge_step_handler_receives_the_typed_merge_context()
        {
            Guid subordinate = Guid.NewGuid();
            MergeProbe probe = new MergeProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(PluginMessages.Merge)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter("Target", new EntityReference(TestAccount.EntityLogicalNameValue, Guid.NewGuid()))
                .WithInputParameter("SubordinateId", subordinate)
                .Execute(probe);

            Assert.Equal(subordinate, probe.CapturedSubordinate);
        }

        private interface IGreeter { string Greet(); }

        private sealed class Greeter : IGreeter { public string Greet() => "hi"; }

        // Registers a service in ConfigureServices and resolves it inside a TYPED handler (ICreateContext) - the
        // exact shape of the scaffolded ContactRouterPlugin. Regression: this used to throw "does not expose the
        // execution service resolver" because the typed context did not forward the resolver.
        private sealed class DiProbe : EntityPluginBase<TestAccount>
        {
            public string? Greeted;

            public DiProbe() : base(string.Empty, string.Empty) { }

            protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
                => services.AddScoped<IGreeter>(_ => new Greeter());

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PostOperation().Sync().WithFiltering(a => a.Name).Handle(OnCreated);

            private void OnCreated(ICreatePostContext<TestAccount> ctx)
                => Greeted = ctx.GetRequiredService<IGreeter>().Greet();
        }

        [Fact]
        public void Typed_handler_resolves_a_service_registered_in_ConfigureServices()
        {
            DiProbe probe = new DiProbe();

            PluginContext.For<TestAccount>()
                .OnCreate().Stage(PluginStages.PostOperation).Sync()
                .WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Zava" })
                .Execute(probe);

            Assert.Equal("hi", probe.Greeted);
        }
    }
}
