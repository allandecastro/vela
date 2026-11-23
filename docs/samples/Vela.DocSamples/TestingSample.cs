// The samples of the testing guides (docs/guides/testing.md, security-model.md, telemetry.md,
// environment-variables.md). Edit the regions, not the pages.
namespace Vela.DocSamples
{
    using System;
    using System.ServiceModel;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.PluginTelemetry;
    using Vela.Context;
    using Vela.DocSamples.Models;
    using Vela.Plugin;
    using Vela.Querying;
    using Vela.Testing;
    using Vela.Testing.Security;
    using Xunit;

    #region pipeline-plugin
    public sealed partial class AccountWelcomeTask(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnCreate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx => ctx.Service.Create(new Entity("task")
                {
                    ["subject"] = "Welcome " + ctx.Request.Target.Name,
                    ["regardingobjectid"] = new EntityReference(Account.EntityLogicalNameValue, ctx.PrimaryEntityId),
                }));
    }
    #endregion

    public sealed class TestingSampleTests
    {
        [Fact]
        public void Pipeline_Creates_The_Welcome_Task()
        {
            #region pipeline-test
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountWelcomeTask>();

            Guid accountId = pipeline.Service.Create(new Account { Name = "Zava" });

            // Assert on the resulting data...
            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("Welcome Zava", task.GetAttributeValue<string>("subject"));
            Assert.Equal(accountId, task.GetAttributeValue<EntityReference>("regardingobjectid").Id);

            // ...and on what ran.
            PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog);
            Assert.Equal(typeof(AccountWelcomeTask).FullName, entry.PluginTypeName);
            Assert.True(entry.HandlerInvoked);
            #endregion
        }

        [Fact]
        public void Security_Faults_A_Record_Out_Of_Reach()
        {
            Guid aliceId = Guid.NewGuid();
            Guid bobId = Guid.NewGuid();
            Guid salesBu = Guid.NewGuid();
            Guid bobsAccountId = Guid.NewGuid();

            #region security
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(aliceId)
                .WithSecurity(s => s
                    .BusinessUnit(salesBu)
                    .Role("salesperson", r => r
                        .Grant("account", AccessLevel.User, Privilege.Create, Privilege.Read, Privilege.Write))
                    .User(aliceId, salesBu, "salesperson")
                    .User(bobId, salesBu, "salesperson"))
                .Seed(new Account
                {
                    Id = bobsAccountId,
                    Name = "Bob's account",
                    ["ownerid"] = new EntityReference("systemuser", bobId),
                });

            // Alice reads her own accounts only: Bob's is left out of queries...
            Assert.Empty(crm.RetrieveMultiple(Query.For<Account>().Select(a => a.Name)).Entities);

            // ...and a Retrieve by id faults with 0x80048306.
            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Retrieve<Account>(bobsAccountId, a => a.Name));
            Assert.Equal(unchecked((int)0x80048306), fault.Detail.ErrorCode);
            #endregion
        }

        [Fact]
        public void Telemetry_Is_Recorded()
        {
            #region telemetry
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrderTelemetry>();

            pipeline.Service.Create(new Account { Name = "Zava" });

            // The runtime logs the execution's start and duration too: pick the plugin's own entries.
            LogEntry entry = Assert.Single(pipeline.LogCapture.Entries, e => e.Level == LogLevel.Warning);
            Assert.Equal("careful", entry.Message);
            Assert.Equal(2, Assert.Single(pipeline.LogCapture.Metrics, m => m.Name == "Orders").Value);
            #endregion
        }

        [Fact]
        public void Environment_Variable_Is_Read()
        {
            #region environment-variables
            Guid definitionId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ApiUrlReader>();
            pipeline.Seed(
                new Entity("environmentvariabledefinition", definitionId)
                {
                    ["schemaname"] = "adc_ApiBaseUrl",
                    ["type"] = new OptionSetValue(100000000), // String
                    ["defaultvalue"] = "https://default",
                },
                new Entity("environmentvariablevalue", Guid.NewGuid())
                {
                    ["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", definitionId),
                    ["value"] = "https://override",
                    ["statecode"] = new OptionSetValue(0),
                });

            pipeline.Service.Create(new Account { Name = "Zava" });

            Assert.Contains("api: https://override", pipeline.Trace.Traces);
            #endregion
        }
    }

    #region telemetry-plugin
    public sealed partial class OrderTelemetry(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnCreate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    ctx.LogWarning("careful");
                    ctx.LogMetric("Orders", 2);
                });
    }
    #endregion

    #region environment-variables-plugin
    public sealed partial class ApiUrlReader(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnCreate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx => ctx.Trace("api: {0}", ctx.GetEnvironmentVariable("adc_ApiBaseUrl")));
    }
    #endregion
}
