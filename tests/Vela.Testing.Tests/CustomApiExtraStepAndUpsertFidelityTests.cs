using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    [Vela.Constants.CustomMessage("vela_XsGlobal"), Vela.Messages.GlobalCustomApi]
    public sealed class XsGlobalMessage { }

    // A global API's message declared without [GlobalCustomApi]: the step keeps the plugin's table.
    [Vela.Constants.CustomMessage("vela_XsGlobal")]
    public sealed class XsGlobalUnmarkedMessage { }

    [Vela.Constants.CustomMessage("vela_XsBound")]
    public sealed class XsBoundMessage { }

    [Vela.Constants.CustomMessage("vela_XsCollection")]
    public sealed class XsCollectionMessage { }

    // Verified live on 2026-11-16 (dyn-premium-dev, recorder plugin): what the extra steps of a Custom API see, by
    // binding, stage and mode; which tables a step on its message can be registered on; and the Upsert facts (the
    // Upsert message has filters for elastic tables only, the inner Create/Update PreValidation is outside the
    // transaction).
    public sealed class CustomApiExtraStepAndUpsertFidelityTests
    {
        public CustomApiExtraStepAndUpsertFidelityTests()
        {
            XsTrace.Lines.Clear();
        }

        [Fact]
        public void The_extra_steps_of_a_global_api_see_table_none_and_an_empty_id_at_every_stage()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsGlobalApi>().Register<XsGlobalSteps>();

            pipeline.Service.Execute(new OrganizationRequest("vela_XsGlobal"));

            Assert.Equal(
                new[]
                {
                    $"10/sync none {Guid.Empty} tx=False",
                    $"20/sync none {Guid.Empty} tx=True",
                    $"40/sync none {Guid.Empty} tx=True",
                    $"40/async none {Guid.Empty} tx=False",
                },
                XsTrace.Lines);
        }

        [Fact]
        public void The_extra_steps_of_an_entity_bound_api_see_the_target_id_except_the_async_one()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsBoundApi>().Register<XsBoundSteps>();

            pipeline.Service.Execute(new OrganizationRequest("vela_XsBound") { ["Target"] = new EntityReference("account", id) });

            Assert.Equal(
                new[]
                {
                    $"10/sync account {id} tx=False",
                    $"20/sync account {id} tx=True",
                    $"40/sync account {id} tx=True",
                    $"40/async account {Guid.Empty} tx=False",
                },
                XsTrace.Lines);
        }

        [Fact]
        public void The_extra_steps_of_a_collection_bound_api_see_the_table_and_an_empty_id()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsCollectionApi>().Register<XsCollectionSteps>();

            pipeline.Service.Execute(new OrganizationRequest("vela_XsCollection"));

            Assert.Equal(new[] { $"40/sync account {Guid.Empty} tx=True" }, XsTrace.Lines);
        }

        [Fact]
        public void A_step_with_a_table_on_a_global_api_fails_the_call()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsGlobalApi>().Register<XsGlobalUnmarkedStep>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => pipeline.Service.Execute(new OrganizationRequest("vela_XsGlobal")));

            Assert.Contains("Custom API 'vela_XsGlobal' is global", error.Message);
            Assert.Contains("'account'", error.Message);
            Assert.Contains("GlobalCustomApi", error.Message);
            Assert.Empty(XsTrace.Lines);
        }

        [Fact]
        public void A_step_on_another_table_than_the_bound_one_fails_the_call()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsBoundApi>().Register<XsBoundContactStep>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => pipeline.Service.Execute(new OrganizationRequest("vela_XsBound") { ["Target"] = new EntityReference("account", Guid.NewGuid()) }));

            Assert.Contains("is bound to 'account'", error.Message);
            Assert.Contains("'task'", error.Message);
        }

        [Fact]
        public void An_Upsert_step_never_fires_for_a_standard_table()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsUpsertSteps>();

            pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = Guid.NewGuid(), Name = "u" } });

            Assert.DoesNotContain(XsTrace.Lines, line => line.StartsWith("Upsert", StringComparison.Ordinal));
            Assert.Contains("Create/10 tx=False", XsTrace.Lines);
        }

        [Fact]
        public void An_Upsert_step_fires_for_a_table_declared_elastic()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().AsElastic(FakeAccount.EntityLogicalNameValue).Register<XsUpsertSteps>();

            pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = Guid.NewGuid(), Name = "u" } });

            Assert.Contains("Upsert/20 tx=True", XsTrace.Lines);
            Assert.Contains("Upsert/40 tx=True", XsTrace.Lines);
        }

        [Theory]
        [InlineData(false, "Create")]
        [InlineData(true, "Update")]
        public void The_inner_Create_or_Update_PreValidation_of_an_Upsert_is_outside_the_transaction(bool exists, string inner)
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsUpsertSteps>();
            if (exists)
            {
                pipeline.Seed(new FakeAccount { Id = id, Name = "old" });
            }

            pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = id, Name = "u" } });

            Assert.Equal(new[] { $"{inner}/10 tx=False", $"{inner}/20 tx=True", $"{inner}/40 tx=True" }, XsTrace.Lines);
        }
    }

    internal static class XsTrace
    {
        public static List<string> Lines { get; } = new List<string>();

        public static void Record(ILocalPluginContext ctx)
        {
            IPluginExecutionContext c = ctx.PluginExecutionContext;
            Lines.Add($"{c.Stage}/{(c.Mode == 1 ? "async" : "sync")} {c.PrimaryEntityName} {c.PrimaryEntityId} tx={c.IsInTransaction}");
        }

        public static void RecordMessage(ILocalPluginContext ctx)
        {
            IPluginExecutionContext c = ctx.PluginExecutionContext;
            Lines.Add($"{c.MessageName}/{c.Stage} tx={c.IsInTransaction}");
        }
    }

    public sealed class XsGlobalApi : CustomApiBase
    {
        public XsGlobalApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_XsGlobal", ctx => { }).AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync);
    }

    public sealed class XsBoundApi : CustomApiBase
    {
        public XsBoundApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_XsBound", ctx => { }).AsAction().BoundTo("account")
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync);
    }

    public sealed class XsCollectionApi : CustomApiBase
    {
        public XsCollectionApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_XsCollection", ctx => { }).AsAction().BoundToCollection("account")
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync);
    }

    // Declared from an account plugin: the [GlobalCustomApi] message registers the steps with no table.
    public sealed class XsGlobalSteps : EntityPluginBase<FakeAccount>
    {
        public XsGlobalSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMessage<XsGlobalMessage>().PreValidation().Sync().Handle(ctx => XsTrace.Record(ctx));
            RegisterStep().OnMessage<XsGlobalMessage>().PreOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
            RegisterStep().OnMessage<XsGlobalMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
            RegisterStep().OnMessage<XsGlobalMessage>().PostOperation().Async().Handle(ctx => XsTrace.Record(ctx));
        }
    }

    public sealed class XsGlobalUnmarkedStep : EntityPluginBase<FakeAccount>
    {
        public XsGlobalUnmarkedStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<XsGlobalUnmarkedMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
    }

    public sealed class XsBoundSteps : EntityPluginBase<FakeAccount>
    {
        public XsBoundSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMessage<XsBoundMessage>().PreValidation().Sync().Handle(ctx => XsTrace.Record(ctx));
            RegisterStep().OnMessage<XsBoundMessage>().PreOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
            RegisterStep().OnMessage<XsBoundMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
            RegisterStep().OnMessage<XsBoundMessage>().PostOperation().Async().Handle(ctx => XsTrace.Record(ctx));
        }
    }

    public sealed class XsBoundContactStep : EntityPluginBase<FakeTask>
    {
        public XsBoundContactStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<XsBoundMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
    }

    public sealed class XsCollectionSteps : EntityPluginBase<FakeAccount>
    {
        public XsCollectionSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<XsCollectionMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
    }

    public sealed class XsUpsertSteps : EntityPluginBase<FakeAccount>
    {
        public XsUpsertSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMessage<UpsertRequest>().PreOperation().Sync().Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnMessage<UpsertRequest>().PostOperation().Sync().Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnUpdate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
        }
    }
}
