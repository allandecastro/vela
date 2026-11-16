using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // The SharedVariables, ParentContext and Depth of each step, recorded the way the live probe recorded them on a
    // Dataverse environment (2026-11-16): each step records its context, then writes "w_{message}_{stage}_d{depth}" into
    // its own SharedVariables. Every expectation below is the live trace, less the platform's own variables.
    public sealed class LiveProbePipelineContextTests
    {
        public LiveProbePipelineContextTests()
        {
            LpTrace.Lines.Clear();
        }

        [Fact]
        public void A_single_Create_shares_one_collection_between_its_Create_and_CreateMultiple_steps()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpAccountRecorder>();

            pipeline.Service.Create(new FakeAccount { Name = "single" });

            Assert.Equal(
                new[]
                {
                    "Create/10/account d=1 own={}",
                    "Create/20/account d=1 own={} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                    "CreateMultiple/20/account d=1 own={w_Create_20_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                    "Create/40/account d=1 own={w_CreateMultiple_20_d1,w_Create_20_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                    "CreateMultiple/40/account d=1 own={w_CreateMultiple_20_d1,w_Create_20_d1,w_Create_40_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                },
                LpTrace.Lines);
        }

        [Fact]
        public void A_single_Update_shares_one_collection_between_its_Update_and_UpdateMultiple_steps()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpAccountRecorder>().Seed(new FakeAccount { Id = id, Name = "old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "new" });

            Assert.Equal(
                new[]
                {
                    "Update/10/account d=1 own={}",
                    "Update/20/account d=1 own={} | P1=Update/30/account d=1 sv={w_Update_10_d1}",
                    "UpdateMultiple/20/account d=1 own={w_Update_20_d1} | P1=Update/30/account d=1 sv={w_Update_10_d1}",
                    "Update/40/account d=1 own={w_UpdateMultiple_20_d1,w_Update_20_d1} | P1=Update/30/account d=1 sv={w_Update_10_d1}",
                    "UpdateMultiple/40/account d=1 own={w_UpdateMultiple_20_d1,w_Update_20_d1,w_Update_40_d1} | P1=Update/30/account d=1 sv={w_Update_10_d1}",
                },
                LpTrace.Lines);
        }

        [Theory]
        [InlineData("Create")]
        [InlineData("Update")]
        public void Each_row_of_a_bulk_request_has_its_own_collections_under_the_bulk_stage(string single)
        {
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpBulkFirstRecorder>()
                .Seed(new FakeAccount { Id = first, Name = "old" }, new FakeAccount { Id = second, Name = "old" });
            EntityCollection targets = new EntityCollection { EntityName = "account" };
            if (single == "Create")
            {
                targets.Entities.Add(new FakeAccount { Name = "cm1" });
                targets.Entities.Add(new FakeAccount { Name = "cm2" });
                pipeline.Service.Execute(new CreateMultipleRequest { Targets = targets });
            }
            else
            {
                targets.Entities.Add(new FakeAccount { Id = first, Name = "um1" });
                targets.Entities.Add(new FakeAccount { Id = second, Name = "um2" });
                pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });
            }

            // Verified live: a row's Create s10 writes are seen nowhere downstream; its s20 and s40 share their own
            // collection; each sees the CreateMultiple context of its stage, then CreateMultiple/30, as parents.
            string bulk = single + "Multiple";
            string[] expected =
            {
                "S/10/account d=1 own={} | P1=M/10/account d=1 sv={}",
                "S/10/account d=1 own={} | P1=M/10/account d=1 sv={}",
                "M/20/account d=1 own={} | P1=M/30/account d=1 sv={}",
                "S/20/account d=1 own={} | P1=M/20/account d=1 sv={w_M_20_d1} | P2=M/30/account d=1 sv={}",
                "S/20/account d=1 own={} | P1=M/20/account d=1 sv={w_M_20_d1} | P2=M/30/account d=1 sv={}",
                "M/40/account d=1 own={w_M_20_d1} | P1=M/30/account d=1 sv={}",
                "S/40/account d=1 own={w_S_20_d1} | P1=M/40/account d=1 sv={w_M_20_d1,w_M_40_d1} | P2=M/30/account d=1 sv={}",
                "S/40/account d=1 own={w_S_20_d1} | P1=M/40/account d=1 sv={w_M_20_d1,w_M_40_d1} | P2=M/30/account d=1 sv={}",
            };
            Assert.Equal(expected.Select(line => line.Replace("M/", bulk + "/").Replace("w_M_", "w_" + bulk + "_").Replace("S/", single + "/").Replace("w_S_", "w_" + single + "_")), LpTrace.Lines);
        }

        [Fact]
        public void An_Upsert_that_creates_runs_the_Create_at_its_depth_under_the_Upsert_main_operation()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpAccountRecorder>();

            pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = Guid.NewGuid(), Name = "upsert" } });

            Assert.Equal(
                new[]
                {
                    "Create/10/account d=1 own={} | P1=Upsert/30/none d=1 sv={}",
                    "Create/20/account d=1 own={} | P1=Create/30/account d=1 sv={w_Create_10_d1} | P2=Upsert/30/none d=1 sv={}",
                    "CreateMultiple/20/account d=1 own={w_Create_20_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1} | P2=Upsert/30/none d=1 sv={}",
                    "Create/40/account d=1 own={w_CreateMultiple_20_d1,w_Create_20_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1} | P2=Upsert/30/none d=1 sv={}",
                    "CreateMultiple/40/account d=1 own={w_CreateMultiple_20_d1,w_Create_20_d1,w_Create_40_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1} | P2=Upsert/30/none d=1 sv={}",
                },
                LpTrace.Lines);
        }

        [Fact]
        public void An_Upsert_that_updates_runs_the_Update_at_its_depth_under_the_Upsert_main_operation()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpAccountRecorder>().Seed(new FakeAccount { Id = id, Name = "old" });

            pipeline.Service.Execute(new UpsertRequest { Target = new FakeAccount { Id = id, Name = "upsert" } });

            Assert.Equal(
                new[]
                {
                    "Update/10/account d=1 own={} | P1=Upsert/30/none d=1 sv={}",
                    "Update/20/account d=1 own={} | P1=Update/30/account d=1 sv={w_Update_10_d1} | P2=Upsert/30/none d=1 sv={}",
                    "UpdateMultiple/20/account d=1 own={w_Update_20_d1} | P1=Update/30/account d=1 sv={w_Update_10_d1} | P2=Upsert/30/none d=1 sv={}",
                    "Update/40/account d=1 own={w_UpdateMultiple_20_d1,w_Update_20_d1} | P1=Update/30/account d=1 sv={w_Update_10_d1} | P2=Upsert/30/none d=1 sv={}",
                    "UpdateMultiple/40/account d=1 own={w_UpdateMultiple_20_d1,w_Update_20_d1,w_Update_40_d1} | P1=Update/30/account d=1 sv={w_Update_10_d1} | P2=Upsert/30/none d=1 sv={}",
                },
                LpTrace.Lines);
        }

        [Fact]
        public void A_nested_call_sees_the_calling_steps_variables_as_they_were_before_it_ran()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpAccountRecorder>().Register<LpContactRecorder>();

            pipeline.Service.Create(new FakeAccount { Name = "NEST" });

            // Verified live: the child has its own collections; its ParentContext chain shows the calling step without
            // the variables that step set (w_Create_40_d1, before_call), and nothing the child sets comes back.
            Assert.Equal(
                new[]
                {
                    "Create/10/account d=1 own={}",
                    "Create/20/account d=1 own={} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                    "CreateMultiple/20/account d=1 own={w_Create_20_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                    "Create/40/account d=1 own={w_CreateMultiple_20_d1,w_Create_20_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                    "Create/10/contact d=2 own={} | P1=Create/40/account d=1 sv={w_CreateMultiple_20_d1,w_Create_20_d1} | P2=Create/30/account d=1 sv={w_Create_10_d1}",
                    "Create/20/contact d=2 own={} | P1=Create/30/contact d=2 sv={w_Create_10_d2} | P2=Create/40/account d=1 sv={w_CreateMultiple_20_d1,w_Create_20_d1} | P3=Create/30/account d=1 sv={w_Create_10_d1}",
                    "Create/40/contact d=2 own={w_Create_20_d2} | P1=Create/30/contact d=2 sv={w_Create_10_d2} | P2=Create/40/account d=1 sv={w_CreateMultiple_20_d1,w_Create_20_d1} | P3=Create/30/account d=1 sv={w_Create_10_d1}",
                    "CreateMultiple/40/account d=1 own={before_call,w_CreateMultiple_20_d1,w_Create_20_d1,w_Create_40_d1} | P1=Create/30/account d=1 sv={w_Create_10_d1}",
                },
                LpTrace.Lines);
        }

        [Fact]
        public void A_parental_cascade_runs_the_childs_Delete_under_the_roots_main_operation_without_PreValidation()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LpAccountRecorder>().Register<LpContactRecorder>()
                .WithParentalRelationship("account", LpContact.LogicalNameValue, "parentcustomerid")
                .Seed(
                    new FakeAccount { Id = accountId, Name = "root" },
                    new Entity(LpContact.LogicalNameValue, contactId) { ["lastname"] = "child", ["parentcustomerid"] = new EntityReference("account", accountId) });

            pipeline.Service.Delete("account", accountId);

            Assert.Equal(
                new[]
                {
                    "Delete/10/account d=1 own={}",
                    "Delete/20/contact d=1 own={} | P1=Delete/30/account d=1 sv={w_Delete_10_d1}",
                    "Delete/40/contact d=1 own={w_Delete_20_d1} | P1=Delete/30/account d=1 sv={w_Delete_10_d1}",
                    "Delete/20/account d=1 own={} | P1=Delete/30/account d=1 sv={w_Delete_10_d1}",
                    "Delete/40/account d=1 own={w_Delete_20_d1} | P1=Delete/30/account d=1 sv={w_Delete_10_d1}",
                },
                LpTrace.Lines);
            Assert.Empty(pipeline.GetAll(LpContact.LogicalNameValue));
        }
    }

    internal static class LpTrace
    {
        public static readonly List<string> Lines = new List<string>();

        private static string Sv(ParameterCollection variables) => "{" + string.Join(",", variables.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "}";

        // Records the context, then writes this step's variable, as the live probe's recorder did.
        public static void Record(ILocalPluginContext ctx)
        {
            IPluginExecutionContext context = ctx.PluginExecutionContext;
            StringBuilder line = new StringBuilder($"{context.MessageName}/{context.Stage}/{context.PrimaryEntityName} d={context.Depth} own={Sv(context.SharedVariables)}");
            int level = 1;
            for (IPluginExecutionContext parent = context.ParentContext; parent != null; parent = parent.ParentContext)
            {
                line.Append($" | P{level++}={parent.MessageName}/{parent.Stage}/{parent.PrimaryEntityName} d={parent.Depth} sv={Sv(parent.SharedVariables)}");
            }

            Lines.Add(line.ToString());
            context.SharedVariables[$"w_{context.MessageName}_{context.Stage}_d{context.Depth}"] = 1;
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class LpContact : Entity
    {
        public const string LogicalNameValue = "contact";

        public LpContact() : base(LogicalNameValue) { }

        [AttributeLogicalName("lastname")]
        public string? LastName
        {
            get => GetAttributeValue<string>("lastname");
            set => SetAttributeValue("lastname", value);
        }
    }

    // Every Create/Update/Delete stage of account, the bulk messages registered after the single ones (same rank).
    // The account Create/40 of a record named "NEST" creates a contact, as the probe's nested call did.
    public sealed class LpAccountRecorder : EntityPluginBase<FakeAccount>
    {
        public LpAccountRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)RecordAndNest);
            RegisterStep().OnCreateMultiple().PreOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PreValidation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdateMultiple().PreOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnDelete().PreValidation().Sync().Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnDelete().PreOperation().Sync().Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnDelete().PostOperation().Sync().Handle((Action<ILocalPluginContext>)Record);
        }

        private void Record(ILocalPluginContext ctx) => LpTrace.Record(ctx);

        private void RecordAndNest(ILocalPluginContext ctx)
        {
            LpTrace.Record(ctx);
            if (((Entity)ctx.PluginExecutionContext.InputParameters["Target"]).GetAttributeValue<string>("name") == "NEST")
            {
                ctx.PluginExecutionContext.SharedVariables["before_call"] = 1;
                ctx.Service.Create(new LpContact { LastName = "nested" });
            }
        }
    }

    // The bulk-request probe: the CreateMultiple/UpdateMultiple steps rank before the Create/Update ones, so the order
    // matches the live trace (the bulk step, then each row's step).
    public sealed class LpBulkFirstRecorder : EntityPluginBase<FakeAccount>
    {
        public LpBulkFirstRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreateMultiple().PreOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreateMultiple().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PreValidation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdateMultiple().PreOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1).Handle((Action<ILocalPluginContext>)Record);
        }

        private void Record(ILocalPluginContext ctx) => LpTrace.Record(ctx);
    }

    public sealed class LpContactRecorder : EntityPluginBase<LpContact>
    {
        public LpContactRecorder(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(c => c.LastName).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(c => c.LastName).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(c => c.LastName).Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnDelete().PreValidation().Sync().Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnDelete().PreOperation().Sync().Handle((Action<ILocalPluginContext>)Record);
            RegisterStep().OnDelete().PostOperation().Sync().Handle((Action<ILocalPluginContext>)Record);
        }

        private void Record(ILocalPluginContext ctx) => LpTrace.Record(ctx);
    }
}
