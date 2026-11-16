using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // Pipeline review fixes: the correlation id of an async step's writes, the stage-30 ParentContext per record and
    // per platform message (Assign, SetState, Merge), the Merge's nested Update, the pipeline's context values, the
    // bulk image collection of a disabled sibling, duplicate registration and the constructor configuration.
    public sealed class PipelineContextFidelityTests
    {
        public PipelineContextFidelityTests()
        {
            CfProbe.Events.Clear();
        }

        [Fact]
        public void A_write_made_by_an_async_step_keeps_the_operations_correlation_id()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfAsyncWriter>().Register<CfContactRecorder>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // Verified live: the cascade and its async steps share one CorrelationId, the async step's own writes too.
            Assert.Equal(new[] { "account-sync", "account-async", "contact" }, CfProbe.Events.Select(e => e.Split('|')[0]).ToArray());
            string[] ids = CfProbe.Events.Select(e => e.Split('|')[1]).Distinct().ToArray();
            Assert.Single(ids);
        }

        [Fact]
        public void A_cascaded_delete_gives_the_root_record_its_own_stage_30_parent()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfDeleteRecorder>().Register<CfContactDeleteRecorder>()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(
                    new FakeAccount { Id = accountId, Name = "Zava" },
                    new Entity(CfContact.LogicalNameValue, contactId) { ["parentcustomerid"] = new EntityReference("account", accountId) });

            pipeline.Service.Delete("account", accountId);

            Assert.Contains($"contact s20 parent=Delete/30 contact {contactId}", CfProbe.Events);
            Assert.Contains($"contact s40 parent=Delete/30 contact {contactId}", CfProbe.Events);
            Assert.Contains($"account s20 parent=Delete/30 account {accountId}", CfProbe.Events);
            Assert.Contains($"account s40 parent=Delete/30 account {accountId}", CfProbe.Events);
        }

        [Fact]
        public void Each_row_of_a_batch_gets_its_own_stage_30_parent()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfCreateParentRecorder>();
            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Name = "first" });
            targets.Entities.Add(new FakeAccount { Name = "second" });

            pipeline.Service.Execute(new CreateMultipleRequest { Targets = targets });

            Assert.Equal(new[] { "first<-first", "second<-second" }, CfProbe.Events.ToArray());
        }

        [Fact]
        public void The_Update_inside_an_Assign_has_the_Assign_main_operation_as_parent()
        {
            Guid id = Guid.NewGuid();
            Guid newOwner = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfOwnableUpdateRecorder>()
                .Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id) { ["name"] = "Zava", ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()) });

            pipeline.Service.Execute(new AssignRequest { Target = new EntityReference(FakeOwnable.EntityLogicalNameValue, id), Assignee = new EntityReference("systemuser", newOwner) });

            // Verified live: the inner Update's ParentContext is Assign/30 (at the Assign's depth).
            Assert.Equal(new[] { "Update s20 d1 parent=Assign/30 d1 assignee=True", "Update s40 d1 parent=Assign/30 d1 assignee=True" }, CfProbe.Events.ToArray());
        }

        [Fact]
        public void The_Update_inside_a_SetState_has_the_SetStateDynamicEntity_main_operation_as_parent()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfOwnableUpdateRecorder>()
                .Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id) { ["name"] = "Zava", ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) });

            pipeline.Service.Execute(new SetStateRequest { EntityMoniker = new EntityReference(FakeOwnable.EntityLogicalNameValue, id), State = new OptionSetValue(1), Status = new OptionSetValue(-1) });

            Assert.Equal(new[] { "Update s20 d1 parent=SetStateDynamicEntity/30 d1 assignee=False", "Update s40 d1 parent=SetStateDynamicEntity/30 d1 assignee=False" }, CfProbe.Events.ToArray());
        }

        [Fact]
        public void A_Merge_fires_the_Update_steps_on_the_master_under_the_Merge_main_operation()
        {
            Guid master = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfMasterUpdateRecorder>().Seed(
                new FakeAccount { Id = master, Name = "Master" },
                new FakeAccount { Id = subordinate, Name = "Dup" });

            pipeline.Service.Execute(new MergeRequest
            {
                Target = new EntityReference("account", master),
                SubordinateId = subordinate,
                UpdateContent = new Entity("account") { ["accountnumber"] = "A-1" },
                PerformParentingChecks = false,
            });

            // Verified live: Merge fires a nested Update on the master, under Merge/30.
            Assert.Equal(new[] { $"s20 {master} accountnumber=A-1 parent=Merge/30", $"s40 {master} accountnumber=A-1 parent=Merge/30" }, CfProbe.Events.ToArray());
            Assert.Equal("A-1", pipeline.GetAll("account").Single(a => a.Id == master)["accountnumber"]);
        }

        [Fact]
        public void The_pipeline_stamps_the_environment_tenant_and_application_user_on_every_step()
        {
            Guid tenant = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfContextRecorder>()
                .WithEnvironmentId("605cb617-5fe9-e292-9c45-cc5a5adf3519").WithTenantId(tenant).AsApplicationUser();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal(new[] { $"605cb617-5fe9-e292-9c45-cc5a5adf3519|{tenant}|True" }, CfProbe.Events.ToArray());
        }

        [Fact]
        public void A_bulk_steps_image_collection_leaves_out_the_images_of_a_disabled_sibling_step()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfBulkWithDisabledSibling>().Seed(new FakeAccount { Id = id, Name = "old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "new" });

            Assert.Equal(new[] { "Before,PreBusinessEntity" }, CfProbe.Events.ToArray());
        }

        [Fact]
        public void Registering_the_same_plugin_twice_throws()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CfContextRecorder>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => pipeline.Register<CfContextRecorder>());

            Assert.Contains("already registered", error.Message);
            Assert.Contains(typeof(CfContextRecorder).FullName!, error.Message);
        }

        [Fact]
        public void Registering_a_custom_api_twice_throws()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<GlobalProbeApi>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => pipeline.Register<GlobalProbeApi>());

            Assert.Contains("already registered", error.Message);
        }

        [Fact]
        public void PluginContext_constructs_a_plugin_with_null_configuration_as_the_pipeline_does()
        {
            CfConfigProbe.Seen.Clear();

            PluginContext.For<FakeAccount>().OnCreate().WithTarget(new FakeAccount { Name = "x" }).Execute<CfConfigProbe>();
            PluginPipeline.Empty().Register<CfConfigProbe>().Service.Create(new FakeAccount { Name = "y" });

            // No configuration on the step: Dataverse's step columns are null, and both harnesses pass null.
            Assert.NotEmpty(CfConfigProbe.Seen);
            Assert.All(CfConfigProbe.Seen, seen => Assert.Equal("null|null", seen));
        }
    }

    internal static class CfProbe
    {
        public static List<string> Events { get; } = new List<string>();
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class CfContact : Entity
    {
        public const string LogicalNameValue = "contact";

        public CfContact() : base(LogicalNameValue) { }

        [AttributeLogicalName("lastname")]
        public string? LastName
        {
            get => GetAttributeValue<string>("lastname");
            set => SetAttributeValue("lastname", value);
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class CfAccount : Entity
    {
        public const string LogicalNameValue = "account";

        public CfAccount() : base(LogicalNameValue) { }

        [AttributeLogicalName("accountnumber")]
        public string? AccountNumber
        {
            get => GetAttributeValue<string>("accountnumber");
            set => SetAttributeValue("accountnumber", value);
        }
    }

    public sealed class CfAsyncWriter : EntityPluginBase<FakeAccount>
    {
        public CfAsyncWriter(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
                CfProbe.Events.Add("account-sync|" + ctx.PluginExecutionContext.CorrelationId));
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).Handle(ctx =>
            {
                CfProbe.Events.Add("account-async|" + ctx.PluginExecutionContext.CorrelationId);
                ctx.SystemService.Create(new CfContact { LastName = "Child" });
            });
        }
    }

    public sealed class CfContactRecorder : EntityPluginBase<CfContact>
    {
        public CfContactRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(c => c.LastName).Handle(ctx =>
                CfProbe.Events.Add("contact|" + ctx.PluginExecutionContext.CorrelationId));
    }

    internal static class CfParent
    {
        public static string Describe(IPluginExecutionContext context)
        {
            IPluginExecutionContext parent = context.ParentContext;
            return $"parent={parent.MessageName}/{parent.Stage} {parent.PrimaryEntityName} {parent.PrimaryEntityId}";
        }
    }

    public sealed class CfDeleteRecorder : EntityPluginBase<FakeAccount>
    {
        public CfDeleteRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnDelete().PreOperation().Sync().Handle(ctx => CfProbe.Events.Add("account s20 " + CfParent.Describe(ctx.PluginExecutionContext)));
            RegisterStep().OnDelete().PostOperation().Sync().Handle(ctx => CfProbe.Events.Add("account s40 " + CfParent.Describe(ctx.PluginExecutionContext)));
        }
    }

    public sealed class CfContactDeleteRecorder : EntityPluginBase<CfContact>
    {
        public CfContactDeleteRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnDelete().PreOperation().Sync().Handle(ctx => CfProbe.Events.Add("contact s20 " + CfParent.Describe(ctx.PluginExecutionContext)));
            RegisterStep().OnDelete().PostOperation().Sync().Handle(ctx => CfProbe.Events.Add("contact s40 " + CfParent.Describe(ctx.PluginExecutionContext)));
        }
    }

    public sealed class CfCreateParentRecorder : EntityPluginBase<FakeAccount>
    {
        public CfCreateParentRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                IPluginExecutionContext context = ctx.PluginExecutionContext;
                Entity own = (Entity)context.InputParameters["Target"];
                Entity parents = (Entity)context.ParentContext.InputParameters["Target"];
                CfProbe.Events.Add($"{own["name"]}<-{parents["name"]}");
            });
    }

    public sealed class CfOwnableUpdateRecorder : EntityPluginBase<FakeOwnable>
    {
        public CfOwnableUpdateRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(o => o.OwnerId, o => o.StateCode).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(o => o.OwnerId, o => o.StateCode).Handle((Action<ILocalPluginContext>)Record);
        }

        private void Record(ILocalPluginContext ctx)
        {
            IPluginExecutionContext context = ctx.PluginExecutionContext;
            IPluginExecutionContext parent = context.ParentContext;
            CfProbe.Events.Add($"{context.MessageName} s{context.Stage} d{context.Depth} parent={parent.MessageName}/{parent.Stage} d{parent.Depth} assignee={parent.InputParameters.Contains("Assignee")}");
        }
    }

    public sealed class CfMasterUpdateRecorder : EntityPluginBase<CfAccount>
    {
        public CfMasterUpdateRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.AccountNumber).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.AccountNumber).Handle((Action<ILocalPluginContext>)Record);
        }

        private void Record(ILocalPluginContext ctx)
        {
            IPluginExecutionContext context = ctx.PluginExecutionContext;
            Entity target = (Entity)context.InputParameters["Target"];
            CfProbe.Events.Add($"s{context.Stage} {target.Id} accountnumber={target.GetAttributeValue<string>("accountnumber")} parent={context.ParentContext.MessageName}/{context.ParentContext.Stage}");
        }
    }

    public sealed class CfContextRecorder : EntityPluginBase<FakeAccount>
    {
        public CfContextRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                IPluginExecutionContext7 context = (IPluginExecutionContext7)ctx.PluginExecutionContext;
                CfProbe.Events.Add($"{context.EnvironmentId}|{context.TenantId}|{context.IsApplicationUser}");
            });
    }

    public sealed class CfBulkWithDisabledSibling : EntityPluginBase<FakeAccount>
    {
        public CfBulkWithDisabledSibling(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage("Before", a => a.Name).Handle(ctx =>
            {
                IPluginExecutionContext7 context = (IPluginExecutionContext7)ctx.PluginExecutionContext;
                CfProbe.Events.Add(string.Join(",", context.PreEntityImagesCollection[0].Keys.OrderBy(k => k, StringComparer.Ordinal)));
            });
            RegisterStep().OnUpdateMultiple().PreOperation().Sync().FilterOn(a => a.Name).WithPreImage("Hidden", a => a.Name).Disabled().Handle(ctx => { });
        }
    }

    public sealed class CfConfigProbe : EntityPluginBase<FakeAccount>
    {
        public static readonly List<string> Seen = new List<string>();

        public CfConfigProbe(string unsecure, string secure) : base(unsecure, secure)
        {
            Seen.Add($"{unsecure ?? "null"}|{secure ?? "null"}");
        }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => { });
    }
}
