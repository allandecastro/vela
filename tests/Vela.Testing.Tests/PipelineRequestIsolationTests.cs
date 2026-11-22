using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    [Vela.Constants.CustomMessage("vela_RiApi")]
    public sealed class RiApiMessage { }

    // Review follow-ups on #432: steps work on a deep copy of the request, their changes (removals included) flow to
    // the next phase, Upsert honours a pre-step's Target, and a nested read's steps report IsInTransaction = true.
    public sealed class PipelineRequestIsolationTests
    {
        public PipelineRequestIsolationTests()
        {
            RiProbe.Events.Clear();
        }

        [Fact]
        public void An_Upsert_pre_step_that_edits_the_Target_in_place_reaches_the_store_but_not_the_callers_entity()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().AsElastic(RiRecord.LogicalNameValue).Register<RiUpsertEditsTarget>();
            RiRecord target = new RiRecord { Id = Guid.NewGuid(), Name = "original" };

            pipeline.Service.Execute(new UpsertRequest { Target = target });

            Assert.Equal("original (edited)", pipeline.GetAll(RiRecord.LogicalNameValue).Single()["vela_name"]);
            Assert.Equal("original", target.Name);
        }

        [Fact]
        public void An_Upsert_pre_step_that_replaces_the_Target_is_honoured_by_the_main_operation()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().AsElastic(RiRecord.LogicalNameValue).Register<RiUpsertReplacesTarget>();
            Guid id = Guid.NewGuid();

            UpsertResponse response = (UpsertResponse)pipeline.Service.Execute(new UpsertRequest { Target = new RiRecord { Id = id, Name = "original" } });

            Assert.True(response.RecordCreated);
            Assert.Equal("replaced", pipeline.GetAll(RiRecord.LogicalNameValue).Single()["vela_name"]);
        }

        [Fact]
        public void A_custom_api_pre_step_editing_input_values_in_place_does_not_change_the_callers_request()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiApi>().Register<RiApiSteps>();
            EntityReference reference = new EntityReference("account") { KeyAttributes = { ["accountnumber"] = "A-1" } };
            Entity payload = new Entity("account") { ["name"] = "Zava" };
            OrganizationRequest request = new OrganizationRequest("vela_RiApi") { ["Ref"] = reference, ["Payload"] = payload, ["Nickname"] = "z" };

            OrganizationResponse response = pipeline.Service.Execute(request);

            // The main operation saw the step's in-place edits and the removed input...
            Assert.Equal("A-2|Zava!|nickname:absent", response["Seen"]);
            // ...and a Post step's removal of an output reached the caller.
            Assert.False(response.Results.Contains("Internal"));
            // The caller's objects are untouched.
            Assert.Equal("A-1", reference.KeyAttributes["accountnumber"]);
            Assert.Equal("Zava", payload["name"]);
            Assert.Equal("z", request["Nickname"]);
        }

        [Fact]
        public void An_Assign_pre_step_editing_the_request_does_not_change_the_callers_request_and_reaches_the_main_operation()
        {
            Guid id = Guid.NewGuid();
            Guid assignee = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiAssignEdits>()
                .Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id) { ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()) });
            EntityReference target = new EntityReference(FakeOwnable.EntityLogicalNameValue, id);
            AssignRequest request = new AssignRequest { Target = target, Assignee = new EntityReference("systemuser", assignee) };

            pipeline.Service.Execute(request);

            // The step's replacement of the Assignee is the owner written...
            Assert.Equal(RiAssignEdits.Replacement, pipeline.GetAll(FakeOwnable.EntityLogicalNameValue).Single().GetAttributeValue<EntityReference>("ownerid").Id);
            // ...and the caller's request and its objects are untouched.
            Assert.Equal(assignee, request.Assignee.Id);
            Assert.Null(target.Name);
        }

        [Fact]
        public void Read_pre_steps_editing_the_ColumnSet_or_Query_in_place_do_not_change_the_callers_objects()
        {
            Guid a = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiReadEdits>().Seed(
                new Entity(RiRecord.LogicalNameValue, a) { ["vela_name"] = "Alpha", ["vela_code"] = "1" },
                new Entity(RiRecord.LogicalNameValue, Guid.NewGuid()) { ["vela_name"] = "Beta", ["vela_code"] = "2" });
            ColumnSet columns = new ColumnSet("vela_name");
            QueryExpression query = new QueryExpression(RiRecord.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name") };

            Entity read = pipeline.Service.Retrieve(RiRecord.LogicalNameValue, a, columns);
            EntityCollection rows = pipeline.Service.RetrieveMultiple(query);

            Assert.Equal("1", read["vela_code"]);
            Assert.Equal(new[] { "vela_name" }, columns.Columns.ToArray());
            Assert.Equal(new[] { "Alpha" }, rows.Entities.Select(e => (string)e["vela_name"]).ToArray());
            Assert.Empty(query.Criteria.Conditions);
        }

        [Fact]
        public void A_copied_query_returns_the_same_rows_as_the_callers_query()
        {
            // The steps receive a deep copy of the query; the copy must keep every part of it (filters, links, orders,
            // paging, QueryByAttribute values), so the rows match a direct read of the store.
            Guid parent = Guid.NewGuid();
            Entity[] seed =
            {
                new Entity(RiRecord.LogicalNameValue, parent) { ["vela_name"] = "Alpha", ["vela_code"] = "1" },
                new Entity(RiRecord.LogicalNameValue, Guid.NewGuid()) { ["vela_name"] = "Beta", ["vela_code"] = "2", ["vela_parentid"] = new EntityReference(RiRecord.LogicalNameValue, parent) },
                new Entity(RiRecord.LogicalNameValue, Guid.NewGuid()) { ["vela_name"] = "Gamma", ["vela_code"] = "2", ["vela_parentid"] = new EntityReference(RiRecord.LogicalNameValue, parent) },
            };
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiReadLogsMultiple>().Seed(seed);
            InMemoryOrganizationService direct = new InMemoryOrganizationService().Seed(seed);

            QueryExpression query = new QueryExpression(RiRecord.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name"), Distinct = true, PageInfo = new PagingInfo { Count = 5, PageNumber = 1 } };
            FilterExpression or = query.Criteria.AddFilter(LogicalOperator.Or);
            or.AddCondition("vela_code", ConditionOperator.Equal, "2");
            or.AddCondition("vela_code", ConditionOperator.In, "3", "4");
            LinkEntity link = query.AddLink(RiRecord.LogicalNameValue, "vela_parentid", "vela_rirecordid", JoinOperator.Inner);
            link.EntityAlias = "p";
            link.Columns = new ColumnSet("vela_name");
            link.LinkCriteria.AddCondition("vela_code", ConditionOperator.Equal, "1");
            query.AddOrder("vela_name", OrderType.Descending);

            QueryByAttribute byAttribute = new QueryByAttribute(RiRecord.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name"), TopCount = 1 };
            byAttribute.AddAttributeValue("vela_code", "2");
            byAttribute.AddOrder("vela_name", OrderType.Ascending);

            string[] Names(EntityCollection rows) => rows.Entities.Select(e => (string)e["vela_name"] + "/" + ((AliasedValue?)e.GetAttributeValue<AliasedValue>("p.vela_name"))?.Value).ToArray();

            Assert.Equal(new[] { "Gamma/Alpha", "Beta/Alpha" }, Names(pipeline.Service.RetrieveMultiple(query)));
            Assert.Equal(Names(direct.RetrieveMultiple(query)), Names(pipeline.Service.RetrieveMultiple(query)));
            Assert.Equal(Names(direct.RetrieveMultiple(byAttribute)), Names(pipeline.Service.RetrieveMultiple(byAttribute)));
            Assert.Equal(3, RiProbe.Events.Count);
        }

        [Fact]
        public void A_read_issued_inside_a_writes_transaction_sees_its_data_and_its_steps_report_the_transaction()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiCreateReadsBack>().Register<RiReadLogs>();

            pipeline.Service.Create(new RiRecord { Name = "x" });

            // The read found the record the enclosing (uncommitted) Create wrote, and its steps reported true at every
            // stage (verified live).
            Assert.Equal(new[] { "write-step:True", "read-step:10:True", "read-step:20:True", "read-step:40:True", "found:True" }, RiProbe.Events);
        }

        [Fact]
        public void An_Update_by_alternate_key_reaches_the_steps_with_the_id_and_the_primary_id_attribute()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithAlternateKey(RiRecord.LogicalNameValue, "vela_code")
                .Register<RiUpdateLogsTarget>()
                .Seed(new RiRecord { Id = id, Name = "x", ["vela_code"] = "K1" });
            Entity target = new Entity(RiRecord.LogicalNameValue) { ["vela_name"] = "y" };
            target.KeyAttributes.Add("vela_code", "K1");

            pipeline.Service.Update(target);

            // Verified live: Target.Id set, no KeyAttributes, and the primary id attribute in the Target.
            Assert.Equal(new[] { $"10:{id}:keys=0:pk={id}", $"20:{id}:keys=0:pk={id}" }, RiProbe.Events);
            Assert.False(target.Contains("vela_rirecordid"));
        }

        [Fact]
        public void An_Update_by_id_reaches_the_steps_with_the_primary_id_attribute()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiUpdateLogsTarget>().Seed(new RiRecord { Id = id, Name = "x" });
            Entity target = new Entity(RiRecord.LogicalNameValue, id) { ["vela_name"] = "y" };

            pipeline.Service.Update(target);

            // Verified live (SDK Update and UpdateRequest by id): the Target carries the primary id attribute.
            Assert.Equal(new[] { $"10:{id}:keys=0:pk={id}", $"20:{id}:keys=0:pk={id}" }, RiProbe.Events);
            Assert.False(target.Contains("vela_rirecordid"));
        }

        [Fact]
        public void A_top_level_reads_steps_report_no_transaction()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RiReadLogs>();
            Guid id = Guid.NewGuid();
            pipeline.Seed(new RiRecord { Id = id, Name = "x" });

            pipeline.Service.Retrieve(RiRecord.LogicalNameValue, id, new ColumnSet("vela_name"));

            Assert.Equal(new[] { "read-step:10:False", "read-step:20:False", "read-step:40:False" }, RiProbe.Events);
        }
    }

    internal static class RiProbe
    {
        public static List<string> Events { get; } = new List<string>();
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class RiRecord : Entity
    {
        public const string LogicalNameValue = "vela_rirecord";

        public RiRecord() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    public sealed class RiUpsertEditsTarget : EntityPluginBase<RiRecord>
    {
        public RiUpsertEditsTarget(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<UpsertRequest>().PreOperation().Sync().Handle(ctx =>
            {
                Entity target = (Entity)ctx.PluginExecutionContext.InputParameters["Target"];
                target["vela_name"] = target["vela_name"] + " (edited)";
            });
    }

    public sealed class RiUpsertReplacesTarget : EntityPluginBase<RiRecord>
    {
        public RiUpsertReplacesTarget(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<UpsertRequest>().PreOperation().Sync().Handle(ctx =>
            {
                Entity target = (Entity)ctx.PluginExecutionContext.InputParameters["Target"];
                ctx.PluginExecutionContext.InputParameters["Target"] = new Entity(target.LogicalName, target.Id) { ["vela_name"] = "replaced" };
            });
    }

    public sealed class RiApi : CustomApiBase
    {
        public RiApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_RiApi", nameof(Handle)).AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .RequestParameter<EntityReference>("Ref").RequestParameter<Entity>("Payload")
                .RequestParameter<string>("Nickname", optional: true)
                .ResponseParameter<string>("Seen").ResponseParameter<string>("Internal");

        private void Handle(ILocalPluginContext ctx)
        {
            ParameterCollection inputs = ctx.PluginExecutionContext.InputParameters;
            EntityReference reference = (EntityReference)inputs["Ref"];
            Entity payload = (Entity)inputs["Payload"];
            ctx.SetOutputParameter("Seen", $"{reference.KeyAttributes["accountnumber"]}|{payload["name"]}|nickname:{(inputs.Contains("Nickname") ? "present" : "absent")}");
            ctx.SetOutputParameter("Internal", "x");
        }
    }

    public sealed class RiApiSteps : EntityPluginBase<RiRecord>
    {
        public RiApiSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMessage<RiApiMessage>().PreOperation().Sync().Handle(ctx =>
            {
                ParameterCollection inputs = ctx.PluginExecutionContext.InputParameters;
                ((EntityReference)inputs["Ref"]).KeyAttributes["accountnumber"] = "A-2";
                Entity payload = (Entity)inputs["Payload"];
                payload["name"] = payload["name"] + "!";
                inputs.Remove("Nickname");
            });
            RegisterStep().OnMessage<RiApiMessage>().PostOperation().Sync().Handle(ctx =>
                ctx.PluginExecutionContext.OutputParameters.Remove("Internal"));
        }
    }

    public sealed class RiAssignEdits : EntityPluginBase<FakeOwnable>
    {
        public static readonly Guid Replacement = new Guid("5a5a5a00-0000-0000-0000-000000000001");

        public RiAssignEdits(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssign().PreOperation().Sync().Handle(ctx =>
            {
                ParameterCollection inputs = ctx.PluginExecutionContext.InputParameters;
                ((EntityReference)inputs["Target"]).Name = "edited in place";
                inputs["Assignee"] = new EntityReference("systemuser", Replacement);
            });
    }

    public sealed class RiReadEdits : EntityPluginBase<RiRecord>
    {
        public RiReadEdits(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnRetrieve().PreOperation().Sync().Handle(ctx =>
                ((ColumnSet)ctx.PluginExecutionContext.InputParameters["ColumnSet"]).AddColumn("vela_code"));
            RegisterStep().OnRetrieveMultiple().PreOperation().Sync().Handle(ctx =>
                ((QueryExpression)ctx.PluginExecutionContext.InputParameters["Query"]).Criteria.AddCondition("vela_name", ConditionOperator.Equal, "Alpha"));
        }
    }

    public sealed class RiCreateReadsBack : EntityPluginBase<RiRecord>
    {
        public RiCreateReadsBack(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                RiProbe.Events.Add("write-step:" + ctx.PluginExecutionContext.IsInTransaction);
                Entity found = ctx.SystemService.Retrieve(RiRecord.LogicalNameValue, ctx.PluginExecutionContext.PrimaryEntityId, new ColumnSet("vela_name"));
                RiProbe.Events.Add("found:" + (found != null));
            });
    }

    public sealed class RiUpdateLogsTarget : EntityPluginBase<RiRecord>
    {
        public RiUpdateLogsTarget(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => Log(ctx.PluginExecutionContext));
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => Log(ctx.PluginExecutionContext));
        }

        private static void Log(IPluginExecutionContext context)
        {
            Entity target = (Entity)context.InputParameters["Target"];
            RiProbe.Events.Add($"{context.Stage}:{target.Id}:keys={target.KeyAttributes.Count}:pk={target.GetAttributeValue<Guid>("vela_rirecordid")}");
        }
    }

    public sealed class RiReadLogsMultiple : EntityPluginBase<RiRecord>
    {
        public RiReadLogsMultiple(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnRetrieveMultiple().PreOperation().Sync().Handle(ctx => RiProbe.Events.Add("read"));
    }

    public sealed class RiReadLogs : EntityPluginBase<RiRecord>
    {
        public RiReadLogs(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnRetrieve().PreValidation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
            RegisterStep().OnRetrieve().PreOperation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
            RegisterStep().OnRetrieve().PostOperation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
        }

        private static void Log(IPluginExecutionContext context)
            => RiProbe.Events.Add("read-step:" + context.Stage + ":" + context.IsInTransaction);
    }
}
