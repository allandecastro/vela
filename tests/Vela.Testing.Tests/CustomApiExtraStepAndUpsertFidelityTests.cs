using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    [Vela.Constants.CustomMessage("vela_XsGlobal")]
    public sealed class XsGlobalMessage { }

    // A Custom API no Custom API plugin of the pipeline defines (deployed from another assembly).
    [Vela.Constants.CustomMessage("vela_XsElsewhere")]
    public sealed class XsElsewhereMessage { }

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
        public void A_step_on_a_global_api_keeps_its_plugin_table_and_fires_on_table_none()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsGlobalApi>().Register<XsGlobalSteps>().Register<XsGlobalTaskStep>();

            pipeline.Service.Execute(new OrganizationRequest("vela_XsGlobal"));

            // vela plan registers both without a table, so both fire, whatever their plugin's table (account, task).
            Assert.Equal("account", Assert.Single(new XsGlobalSteps(string.Empty, string.Empty).GetStepDefinitions(), s => s.Mode == Vela.Steps.ExecutionMode.Asynchronous).EntityLogicalName);
            Assert.Equal("task", Assert.Single(new XsGlobalTaskStep(string.Empty, string.Empty).GetStepDefinitions()).EntityLogicalName);
            Assert.Equal(2, XsTrace.Lines.FindAll(line => line.StartsWith("40/sync none", StringComparison.Ordinal)).Count);
        }

        [Fact]
        public void A_step_on_a_custom_api_the_pipeline_does_not_define_runs_as_on_a_global_api()
        {
            // No Custom API plugin defines vela_XsElsewhere: with no Target, the message runs on table "none", like a
            // global API, and the step declared from an account plugin fires.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<XsElsewhereStep>();

            pipeline.Service.Execute(new OrganizationRequest("vela_XsElsewhere"));

            Assert.Equal(new[] { $"40/sync none {Guid.Empty} tx=True" }, XsTrace.Lines);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_Upsert_on_an_elastic_table_fires_its_steps_outside_a_transaction_and_no_inner_step(bool exists)
        {
            // Verified live (msdyn_simulationtracker): the Upsert steps fire with IsInTransaction = false and
            // PrimaryEntityId = Guid.Empty; the inner Create or Update fires none of its steps.
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().AsElastic(FakeAccount.EntityLogicalNameValue).Register<XsUpsertSteps>().Register<XsUpsertIds>();
            if (exists)
            {
                pipeline.Seed(new FakeAccount { Id = id, Name = "old" });
            }

            UpsertResponse response = (UpsertResponse)pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = id, Name = "u" } });

            Assert.Equal(new[] { "id/10 " + Guid.Empty, "Upsert/20 tx=False", "id/20 " + Guid.Empty, "Upsert/40 tx=False", "id/40 " + Guid.Empty }, XsTrace.Lines);
            Assert.Equal(!exists, response.RecordCreated);
            Assert.Equal(id, response.Target.Id);
            Assert.Equal("u", pipeline.GetAll(FakeAccount.EntityLogicalNameValue).Single()["name"]);
        }

        [Theory]
        [InlineData(PluginStages.PostOperation, 1)]
        [InlineData(PluginStages.PreOperation, 0)]
        public void A_failing_Upsert_step_on_an_elastic_table_does_not_roll_back_the_write(PluginStages stage, int rows)
        {
            // Verified live: elastic writes are not transactional. A failing PostOperation step leaves the row written;
            // a failing PreOperation step stops the call before the write.
            XsUpsertThrows.Stage = stage;
            PluginPipeline pipeline = PluginPipeline.Empty().AsElastic(FakeAccount.EntityLogicalNameValue).Register<XsUpsertThrows>();

            Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = Guid.NewGuid(), Name = "u" } }));

            Assert.Equal(rows, pipeline.GetAll(FakeAccount.EntityLogicalNameValue).Count);
        }

        [Fact]
        public void A_plain_Create_or_Update_on_an_elastic_table_runs_outside_a_transaction()
        {
            // Verified live: IsInTransaction = false at every stage, and a failing PostOperation Create step leaves the
            // row written.
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().AsElastic(FakeAccount.EntityLogicalNameValue).Register<XsElasticWrites>();

            Assert.Throws<InvalidPluginExecutionException>(() => pipeline.Service.Create(new FakeAccount { Id = id, Name = "fail" }));
            pipeline.Service.Update(new FakeAccount { Id = id, Name = "u" });

            Assert.Equal("u", pipeline.GetAll(FakeAccount.EntityLogicalNameValue).Single()["name"]);
            Assert.Equal(new[] { "Create/10 tx=False", "Create/20 tx=False", "Create/40 tx=False", "Update/10 tx=False", "Update/20 tx=False", "Update/40 tx=False" }, XsTrace.Lines);
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

    // Declared from an account plugin: the steps keep the account table, and run on "none".
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

    public sealed class XsGlobalTaskStep : EntityPluginBase<FakeTask>
    {
        public XsGlobalTaskStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<XsGlobalMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
    }

    public sealed class XsElsewhereStep : EntityPluginBase<FakeAccount>
    {
        public XsElsewhereStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<XsElsewhereMessage>().PostOperation().Sync().Handle(ctx => XsTrace.Record(ctx));
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

    public sealed class XsUpsertIds : EntityPluginBase<FakeAccount>
    {
        public XsUpsertIds(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMessage<UpsertRequest>().PreValidation().Sync().Handle(ctx => XsTrace.Lines.Add("id/10 " + ctx.PluginExecutionContext.PrimaryEntityId));
            RegisterStep().OnMessage<UpsertRequest>().PreOperation().Sync().WithExecutionOrder(2).Handle(ctx => XsTrace.Lines.Add("id/20 " + ctx.PluginExecutionContext.PrimaryEntityId));
            RegisterStep().OnMessage<UpsertRequest>().PostOperation().Sync().WithExecutionOrder(2).Handle(ctx => XsTrace.Lines.Add("id/40 " + ctx.PluginExecutionContext.PrimaryEntityId));
        }
    }

    public sealed class XsUpsertThrows : EntityPluginBase<FakeAccount>
    {
        public XsUpsertThrows(string u, string s) : base(u, s) { }

        public static PluginStages Stage { get; set; } = PluginStages.PostOperation;

        protected override void RegisterPluginSteps()
        {
            if (Stage == PluginStages.PostOperation)
            {
                RegisterStep().OnMessage<UpsertRequest>().PostOperation().Sync().Handle(ctx => throw new InvalidPluginExecutionException("no"));
            }
            else
            {
                RegisterStep().OnMessage<UpsertRequest>().PreOperation().Sync().Handle(ctx => throw new InvalidPluginExecutionException("no"));
            }
        }
    }

    public sealed class XsElasticWrites : EntityPluginBase<FakeAccount>
    {
        public XsElasticWrites(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                XsTrace.RecordMessage(ctx);
                throw new InvalidPluginExecutionException("no");
            });
            RegisterStep().OnUpdate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => XsTrace.RecordMessage(ctx));
        }
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
