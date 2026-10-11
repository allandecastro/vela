using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // Pipeline behaviours checked against a live Dataverse environment (9.2.26094) and mirrored by the harness.
    public sealed class LiveVerifiedPipelineTests
    {
        public LiveVerifiedPipelineTests()
        {
            LvProbe.Events.Clear();
        }

        [Fact]
        public void Retrieve_and_RetrieveMultiple_steps_are_not_in_a_transaction()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LvReadSteps>().Seed(new Entity(LvRecord.LogicalNameValue, id) { ["vela_name"] = "a" });

            pipeline.Service.Retrieve(LvRecord.LogicalNameValue, id, new ColumnSet(true));
            pipeline.Service.RetrieveMultiple(new QueryExpression(LvRecord.LogicalNameValue) { ColumnSet = new ColumnSet(true) });

            Assert.Equal(new[] { "Retrieve/20:False", "Retrieve/40:False", "RetrieveMultiple/20:False", "RetrieveMultiple/40:False" }, LvProbe.Events);
        }

        [Fact]
        public void A_swallowed_failed_read_does_not_end_the_transaction()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LvUpdateSwallowsRead>().Seed(new Entity(LvRecord.LogicalNameValue, id) { ["vela_name"] = "a" });

            pipeline.Service.Update(new LvRecord { Id = id, Name = "b" });

            Assert.Equal("b", pipeline.GetAll(LvRecord.LogicalNameValue).Single()["vela_name"]);
            Assert.Contains("read-failed", LvProbe.Events);
        }

        [Fact]
        public void A_swallowed_failed_write_fails_the_outer_operation()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LvUpdateSwallowsWrite>().Seed(new Entity(LvRecord.LogicalNameValue, id) { ["vela_name"] = "a" });

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Update(new LvRecord { Id = id, Name = "b" }));

            Assert.Equal(unchecked((int)0x8009000C), fault.Detail.ErrorCode);
            Assert.Equal("a", pipeline.GetAll(LvRecord.LogicalNameValue).Single()["vela_name"]);
        }

        [Fact]
        public void A_nested_operations_PreValidation_gets_FromSandbox_and_is_in_the_transaction()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LvCreateValidation>().Register<LvCreatesChild>();

            pipeline.Service.Create(new LvRecord { Name = "parent" });

            // Observed live on the nested PreValidation only; the nested PostOperation step does not get it.
            Assert.Equal(
                new[] { "parent:10:depth1:tx=False:sandbox=False", "child:10:depth2:tx=True:sandbox=True", "child:40:sandbox=False" },
                LvProbe.Events);
        }

        [Fact]
        public void An_async_steps_context_has_an_unnamed_owning_extension_and_the_primary_name_image()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LvAsyncProbe>().Register<LvSyncProbe>()
                .WithEntityMetadata(FakeEntityMetadata.For(LvRecord.LogicalNameValue).PrimaryName("vela_name").Build());

            Guid id = pipeline.Service.Create(new LvRecord { Name = "Zava" });

            Assert.Contains("sync-owner-named:True", LvProbe.Events);
            Assert.Contains("async-owner-id:True", LvProbe.Events);
            Assert.Contains("async-owner-name:<null>", LvProbe.Events);
            Assert.Contains("async-image:Zava|" + id, LvProbe.Events);
        }

        [Fact]
        public void PreValidation_shared_variables_reach_later_stages_only_through_ParentContext()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LvSharedVariables>();

            pipeline.Service.Create(new LvRecord { Name = "x" });

            Assert.Equal(
                new[] { "own:False", "parent:True:Create/30", "post-own:True" },
                LvProbe.Events);
        }
    }

    internal static class LvProbe
    {
        public static List<string> Events { get; } = new List<string>();
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class LvRecord : Entity
    {
        public const string LogicalNameValue = "vela_lvrecord";

        public LvRecord() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    public sealed class LvReadSteps : EntityPluginBase<LvRecord>
    {
        public LvReadSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnRetrieve().PreOperation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
            RegisterStep().OnRetrieve().PostOperation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
            RegisterStep().OnRetrieveMultiple().PreOperation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
            RegisterStep().OnRetrieveMultiple().PostOperation().Sync().Handle(ctx => Log(ctx.PluginExecutionContext));
        }

        private static void Log(IPluginExecutionContext c) => LvProbe.Events.Add($"{c.MessageName}/{c.Stage}:{c.IsInTransaction}");
    }

    public sealed class LvUpdateSwallowsRead : EntityPluginBase<LvRecord>
    {
        public LvUpdateSwallowsRead(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                try
                {
                    ctx.SystemService.Retrieve(LvRecord.LogicalNameValue, Guid.NewGuid(), new ColumnSet(true));
                }
                catch (FaultException<OrganizationServiceFault>)
                {
                    LvProbe.Events.Add("read-failed");
                }
            });
    }

    public sealed class LvUpdateSwallowsWrite : EntityPluginBase<LvRecord>
    {
        public LvUpdateSwallowsWrite(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                try
                {
                    // An invalid lookup: the referenced record does not exist.
                    ctx.SystemService.Update(new Entity(LvRecord.LogicalNameValue, Guid.NewGuid()) { ["vela_name"] = "ghost" });
                }
                catch (FaultException<OrganizationServiceFault>)
                {
                }
            });
    }

    public sealed class LvCreateValidation : EntityPluginBase<LvRecord>
    {
        public LvCreateValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                IPluginExecutionContext c = ctx.PluginExecutionContext;
                bool sandbox = c.InputParameters.Contains("FromSandbox") && (bool)c.InputParameters["FromSandbox"];
                LvProbe.Events.Add($"{ctx.GetTarget<LvRecord>().Name}:{c.Stage}:depth{c.Depth}:tx={c.IsInTransaction}:sandbox={sandbox}");
            });
    }

    public sealed class LvCreatesChild : EntityPluginBase<LvRecord>
    {
        public LvCreatesChild(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                if (ctx.GetTarget<LvRecord>().Name == "parent")
                {
                    ctx.SystemService.Create(new LvRecord { Name = "child" });
                }
                else
                {
                    IPluginExecutionContext c = ctx.PluginExecutionContext;
                    bool sandbox = c.InputParameters.Contains("FromSandbox") && (bool)c.InputParameters["FromSandbox"];
                    LvProbe.Events.Add($"child:{c.Stage}:sandbox={sandbox}");
                }
            });
    }

    public sealed class LvAsyncProbe : EntityPluginBase<LvRecord>
    {
        public LvAsyncProbe(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).Handle(ctx =>
            {
                IPluginExecutionContext c = ctx.PluginExecutionContext;
                LvProbe.Events.Add("async-owner-id:" + (c.OwningExtension != null && c.OwningExtension.Id != Guid.Empty));
                LvProbe.Events.Add("async-owner-name:" + (c.OwningExtension?.Name ?? "<null>"));
                Entity image = c.PostEntityImages["AsynchronousStepPrimaryName"];
                LvProbe.Events.Add($"async-image:{image["vela_name"]}|{image["vela_lvrecordid"]}");
            });
    }

    public sealed class LvSyncProbe : EntityPluginBase<LvRecord>
    {
        public LvSyncProbe(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
                LvProbe.Events.Add("sync-owner-named:" + !string.IsNullOrEmpty(ctx.PluginExecutionContext.OwningExtension?.Name)));
    }

    public sealed class LvSharedVariables : EntityPluginBase<LvRecord>
    {
        public LvSharedVariables(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx =>
                ctx.PluginExecutionContext.SharedVariables["fromValidation"] = 1);
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                IPluginExecutionContext c = ctx.PluginExecutionContext;
                LvProbe.Events.Add("own:" + c.SharedVariables.Contains("fromValidation"));
                IPluginExecutionContext? parent = c.ParentContext;
                LvProbe.Events.Add($"parent:{parent?.SharedVariables.Contains("fromValidation")}:{parent?.MessageName}/{parent?.Stage}");
                c.SharedVariables["fromPreOperation"] = 1;
            });
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
                LvProbe.Events.Add("post-own:" + ctx.PluginExecutionContext.SharedVariables.Contains("fromPreOperation")));
        }
    }
}
