using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // A-58: the harness's transaction model. Dataverse runs the PreValidation stage of the initial operation outside
    // the database transaction, PreOperation/PostOperation inside it, nested operations inside the caller's
    // transaction, and asynchronous steps after the transaction commits (never when it rolls back). A plugin that
    // catches the failure of one of its service calls cannot carry on: the transaction has already ended.
    public sealed class PipelineTransactionTests
    {
        // The platform's fault codes (Dataverse plug-in troubleshooting, "Transaction errors").
        private const int IsvReducedTransactionCount = -2146893812;
        private const int NoActiveTransaction = -2147220911;

        public PipelineTransactionTests()
        {
            TxProbe.Reset();
        }

        private static string[] Logs(PluginPipeline pipeline)
            => pipeline.GetAll(TxProbe.LogEntity).Select(e => (string)e["vela_name"]).OrderBy(n => n).ToArray();

        [Fact]
        public void A_write_made_in_the_initial_PreValidation_stage_survives_a_later_failure()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TxParentPreValidation>()
                .Register<TxParentPreOperation>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new TxParent { Name = "write fail-pre" }));

            // The PreValidation plugin's write ran before the transaction, so it committed on its own.
            Assert.Equal(new[] { "parent-prevalidation" }, Logs(pipeline));
            Assert.Empty(pipeline.GetAll(TxParent.LogicalNameValue));
        }

        [Fact]
        public void A_nested_PreValidation_write_is_part_of_the_callers_transaction_and_rolls_back()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TxParentPostCreatesChild>()
                .Register<TxParentPostFails>()
                .Register<TxChildPreValidation>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new TxParent { Name = "child fail-post" }));

            Assert.Empty(pipeline.GetAll(TxParent.LogicalNameValue));
            Assert.Empty(pipeline.GetAll(TxChild.LogicalNameValue));
            Assert.Empty(Logs(pipeline));
        }

        [Fact]
        public void A_plugin_that_swallows_a_failed_nested_write_fails_the_operation()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TxParentPostSwallows>()
                .Register<TxChildPreOperationFails>();

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Create(new TxParent { Name = "swallow" }));

            Assert.Equal(IsvReducedTransactionCount, fault.Detail.ErrorCode);
            Assert.Contains("boom", fault.Detail.Message);
            Assert.Empty(pipeline.GetAll(TxParent.LogicalNameValue));
            Assert.Empty(pipeline.GetAll(TxChild.LogicalNameValue));
        }

        [Fact]
        public void A_service_call_after_a_swallowed_failure_is_refused_with_no_active_transaction()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TxParentPostSwallows>()
                .Register<TxChildPreOperationFails>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new TxParent { Name = "swallow retry" }));

            Assert.Contains("retry:" + NoActiveTransaction, TxProbe.Events);
            Assert.Empty(Logs(pipeline));
        }

        [Fact]
        public void A_failed_write_swallowed_in_the_initial_PreValidation_stage_does_not_fail_the_operation()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TxParentPreValidation>()
                .Register<TxChildPreOperationFails>();

            Guid id = pipeline.Service.Create(new TxParent { Name = "prevalidation-swallow" });

            // Outside the transaction a failed call only rolls back its own operation; the plugin may carry on.
            Assert.NotEqual(Guid.Empty, id);
            Assert.Single(pipeline.GetAll(TxParent.LogicalNameValue));
            Assert.Empty(pipeline.GetAll(TxChild.LogicalNameValue));
        }

        [Fact]
        public void A_cascading_Delete_fires_the_deleted_records_PreValidation_before_its_children_and_outside_the_transaction()
        {
            Guid parentId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TxParentDeletePreValidation>()
                .Register<TxChildDeletePreOperation>()
                .WithParentalRelationship(TxParent.LogicalNameValue, TxChild.LogicalNameValue, "vela_parentid")
                .Seed(
                    new Entity(TxParent.LogicalNameValue, parentId) { ["vela_name"] = "p" },
                    new Entity(TxChild.LogicalNameValue, Guid.NewGuid()) { ["vela_name"] = "c", ["vela_parentid"] = new EntityReference(TxParent.LogicalNameValue, parentId) });

            pipeline.Service.Delete(TxParent.LogicalNameValue, parentId);

            Assert.Equal(new[] { "parent-delete-prevalidation:False", "child-delete-preoperation:True" }, TxProbe.Events);
            Assert.Empty(pipeline.GetAll(TxChild.LogicalNameValue));
        }
    }

    internal static class TxProbe
    {
        public const string LogEntity = "vela_txlog";

        public static List<string> Events { get; } = new List<string>();

        public static void Reset() => Events.Clear();

        public static void Log(IOrganizationService service, string name)
            => service.Create(new Entity(LogEntity) { ["vela_name"] = name });

        public static string Name(Entity target) => target.GetAttributeValue<string>("vela_name") ?? string.Empty;
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class TxParent : Entity
    {
        public const string LogicalNameValue = "vela_txparent";

        public TxParent() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class TxChild : Entity
    {
        public const string LogicalNameValue = "vela_txchild";

        public TxChild() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    public sealed class TxParentPreValidation : EntityPluginBase<TxParent>
    {
        public TxParentPreValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                TxProbe.Events.Add("parent-prevalidation:" + ctx.PluginExecutionContext.IsInTransaction);
                string name = TxProbe.Name(ctx.GetTarget<TxParent>());
                if (name.Contains("write"))
                {
                    TxProbe.Log(ctx.SystemService, "parent-prevalidation");
                }

                if (name.Contains("prevalidation-swallow"))
                {
                    try
                    {
                        ctx.SystemService.Create(new TxChild { Name = "boom" });
                    }
                    catch (Exception)
                    {
                        // Outside the transaction: allowed.
                    }
                }
            });
    }

    public sealed class TxParentPreOperation : EntityPluginBase<TxParent>
    {
        public TxParentPreOperation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                TxProbe.Events.Add("parent-preoperation:" + ctx.PluginExecutionContext.IsInTransaction);
                if (TxProbe.Name(ctx.GetTarget<TxParent>()).Contains("fail-pre"))
                {
                    throw new InvalidPluginExecutionException("fail-pre");
                }
            });
    }

    public sealed class TxParentPostCreatesChild : EntityPluginBase<TxParent>
    {
        public TxParentPostCreatesChild(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().WithExecutionOrder(1).FilterOn(a => a.Name).Handle(ctx =>
            {
                TxProbe.Events.Add("parent-post:" + ctx.PluginExecutionContext.IsInTransaction);
                if (TxProbe.Name(ctx.GetTarget<TxParent>()).Contains("child"))
                {
                    ctx.SystemService.Create(new TxChild { Name = "child of " + TxProbe.Name(ctx.GetTarget<TxParent>()) });
                }
            });
    }

    public sealed class TxParentPostFails : EntityPluginBase<TxParent>
    {
        public TxParentPostFails(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().WithExecutionOrder(2).FilterOn(a => a.Name).Handle(ctx =>
            {
                bool asyncRan = ctx.SystemService.RetrieveMultiple(new Microsoft.Xrm.Sdk.Query.QueryExpression(TxProbe.LogEntity)).Entities.Count > 0;
                TxProbe.Events.Add("parent-post-2:" + (asyncRan ? "async-ran" : "async-pending"));
                if (TxProbe.Name(ctx.GetTarget<TxParent>()).Contains("fail-post"))
                {
                    throw new InvalidPluginExecutionException("fail-post");
                }
            });
    }

    public sealed class TxParentPostSwallows : EntityPluginBase<TxParent>
    {
        public TxParentPostSwallows(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().WithExecutionOrder(3).FilterOn(a => a.Name).Handle(ctx =>
            {
                try
                {
                    ctx.SystemService.Create(new TxChild { Name = "boom" });
                }
                catch (Exception)
                {
                    // Swallowed: the anti-pattern the platform rejects inside a transaction.
                }

                if (TxProbe.Name(ctx.GetTarget<TxParent>()).Contains("retry"))
                {
                    try
                    {
                        TxProbe.Log(ctx.SystemService, "after-swallow");
                    }
                    catch (FaultException<OrganizationServiceFault> fault)
                    {
                        TxProbe.Events.Add("retry:" + fault.Detail.ErrorCode);
                    }
                }
            });
    }

    public sealed class TxChildPreValidation : EntityPluginBase<TxChild>
    {
        public TxChildPreValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                TxProbe.Events.Add("child-prevalidation:" + ctx.PluginExecutionContext.IsInTransaction);
                TxProbe.Log(ctx.SystemService, "child-prevalidation");
            });
    }

    public sealed class TxChildPreOperationFails : EntityPluginBase<TxChild>
    {
        public TxChildPreOperationFails(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                if (TxProbe.Name(ctx.GetTarget<TxChild>()).Contains("boom"))
                {
                    throw new InvalidPluginExecutionException("boom");
                }
            });
    }

    public sealed class TxParentDeletePreValidation : EntityPluginBase<TxParent>
    {
        public TxParentDeletePreValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreValidation().Sync().Handle(ctx =>
                TxProbe.Events.Add("parent-delete-prevalidation:" + ctx.PluginExecutionContext.IsInTransaction));
    }

    public sealed class TxChildDeletePreOperation : EntityPluginBase<TxChild>
    {
        public TxChildDeletePreOperation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreOperation().Sync().Handle(ctx =>
                TxProbe.Events.Add("child-delete-preoperation:" + ctx.PluginExecutionContext.IsInTransaction));
    }
}
