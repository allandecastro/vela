using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Xunit;

namespace Vela.Testing.Tests
{
    /// <summary>
    /// Fidelity regressions (Lot C): the harness must report the same execution-context identity, parent context
    /// and not-found fault a real Dataverse org does, or a green test can hide a production failure.
    /// </summary>
    public sealed class HarnessFidelityTests
    {
        // F1: on account create, record the identity the plugin sees so the test can assert it matches the
        // configured caller rather than a random GUID.
        public sealed class CaptureIdentityOnAccountCreate : EntityPluginBase<FakeAccount>
        {
            public CaptureIdentityOnAccountCreate(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name);

            protected override void Execute(ILocalPluginContext ctx)
                => ctx.SystemService.Create(new Entity("vela_capture")
                {
                    ["userid"] = ctx.UserId,
                    ["initiatinguserid"] = ctx.InitiatingUserId,
                    ["businessunitid"] = ctx.PluginExecutionContext.BusinessUnitId,
                    ["organizationid"] = ctx.PluginExecutionContext.OrganizationId,
                });
        }

        // F2: on task create (fired cascaded from the account-create plugin), record the parent context's message so
        // the test can assert ParentContext is populated on a nested execution.
        public sealed class CaptureParentOnTaskCreate : EntityPluginBase<FakeTask>
        {
            public CaptureParentOnTaskCreate(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Subject);

            protected override void Execute(ILocalPluginContext ctx)
            {
                IPluginExecutionContext? parent = ctx.PluginExecutionContext.ParentContext;
                ctx.SystemService.Create(new Entity("vela_capture")
                {
                    ["parentnull"] = parent == null,
                    ["parentmessage"] = parent?.MessageName ?? "<null>",
                    ["parententity"] = parent?.PrimaryEntityName ?? "<null>",
                });
            }
        }

        [Fact]
        public void ExecutionContext_identity_reflects_the_configured_caller_not_a_random_value()
        {
            Guid user = Guid.NewGuid();
            Guid businessUnit = Guid.NewGuid();
            Guid organization = Guid.NewGuid();

            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(user)
                .WithCurrentBusinessUnit(businessUnit)
                .WithOrganizationId(organization)
                .Register<CaptureIdentityOnAccountCreate>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity capture = Assert.Single(pipeline.Service.RetrieveMultiple(new QueryExpression("vela_capture") { ColumnSet = new ColumnSet(true) }).Entities);
            Assert.Equal(user, capture.GetAttributeValue<Guid>("userid"));
            Assert.Equal(user, capture.GetAttributeValue<Guid>("initiatinguserid"));
            Assert.Equal(businessUnit, capture.GetAttributeValue<Guid>("businessunitid"));
            Assert.Equal(organization, capture.GetAttributeValue<Guid>("organizationid"));
        }

        [Fact]
        public void ParentContext_is_populated_on_a_cascaded_execution()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<CreateTaskOnAccountCreate>()
                .Register<CaptureParentOnTaskCreate>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity capture = Assert.Single(pipeline.Service.RetrieveMultiple(new QueryExpression("vela_capture") { ColumnSet = new ColumnSet(true) }).Entities);
            Assert.False(capture.GetAttributeValue<bool>("parentnull"));
            Assert.Equal(PluginMessages.Create, capture.GetAttributeValue<string>("parentmessage"));
            Assert.Equal("account", capture.GetAttributeValue<string>("parententity"));
        }

        [Fact]
        public void Update_of_a_missing_record_faults_with_object_does_not_exist()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Update(new Entity("account", Guid.NewGuid()) { ["name"] = "x" }));
            Assert.Equal(-2147220969, fault.Detail.ErrorCode);
        }
    }
}
