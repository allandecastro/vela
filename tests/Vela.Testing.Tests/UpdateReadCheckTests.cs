using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Plugin;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Verified live (2026-11-18): an Update checks Read on the row before Write. A caller that cannot read the row gets
    // the read fault, and a plugin that catches it carries on as after a failed read; a caught Write denial still ends
    // the transaction.
    public sealed class UpdateReadCheckTests
    {
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);
        private const int RecordAccessDenied = unchecked((int)0x80048306);
        private const int IsvReducedTransactionCount = -2146893812;

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000201");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000202");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000002b1");
        private static readonly Guid BobsAccount = new Guid("acc00000-0000-0000-0000-000000000203");

        private static PluginPipeline PipelineFor(Action<RoleBuilder> accountGrants) =>
            PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("tester", r =>
                    {
                        r.Grant(UpdateProbe.LogicalNameValue, AccessLevel.Organization, Privilege.Create, Privilege.Read);
                        accountGrants(r);
                    })
                    .User(Alice, SalesBu, "tester"))
                .Seed(new Entity("account", BobsAccount)
                {
                    ["name"] = "bob's",
                    ["ownerid"] = new EntityReference("systemuser", Bob),
                    ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
                })
                .Register<UpdateProbeUpdatesAccount>();

        [Fact]
        public void An_Update_of_a_row_out_of_the_callers_read_depth_fails_with_the_record_read_fault()
        {
            PluginPipeline pipeline = PipelineFor(r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Update(new Entity("account", BobsAccount) { ["name"] = "renamed" }));

            Assert.Equal(RecordAccessDenied, fault.Detail.ErrorCode);
            Assert.Contains("ReadAccess", fault.Detail.Message);
        }

        [Fact]
        public void An_Update_without_the_Read_privilege_fails_with_the_read_privilege_fault()
        {
            PluginPipeline pipeline = PipelineFor(r => r.Grant("account", AccessLevel.Organization, Privilege.Write));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Update(new Entity("account", BobsAccount) { ["name"] = "renamed" }));

            Assert.Equal(PrincipalPrivilegeDenied, fault.Detail.ErrorCode);
            Assert.Contains("prvReadaccount", fault.Detail.Message);
        }

        [Fact]
        public void A_caught_Update_denied_by_its_read_check_does_not_end_the_transaction()
        {
            PluginPipeline pipeline = PipelineFor(r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write));

            Guid id = pipeline.Service.Create(new UpdateProbe { Name = "read check" });

            Assert.NotEqual(Guid.Empty, id);
            Assert.Equal(new[] { "caught:" + RecordAccessDenied, "logged" }, UpdateProbeUpdatesAccount.Events);
            Assert.Single(pipeline.GetAll("vela_txlog"));
            Assert.Equal("bob's", pipeline.GetAll("account")[0].GetAttributeValue<string>("name"));
        }

        [Fact]
        public void A_caught_Update_denied_on_Write_still_ends_the_transaction()
        {
            PluginPipeline pipeline = PipelineFor(r => r.Grant("account", AccessLevel.Organization, Privilege.Read));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Create(new UpdateProbe { Name = "write denied" }));

            Assert.Equal(IsvReducedTransactionCount, fault.Detail.ErrorCode);
            Assert.Equal(new[] { "caught:" + PrincipalPrivilegeDenied }, UpdateProbeUpdatesAccount.Events);
            Assert.Empty(pipeline.GetAll(UpdateProbe.LogicalNameValue));
            Assert.Empty(pipeline.GetAll("vela_txlog"));
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class UpdateProbe : Entity
    {
        public const string LogicalNameValue = "vela_updateprobe";

        public UpdateProbe() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    // Updates Bob's account as the calling user and swallows the fault; on "read check", then writes a log row as SYSTEM.
    public sealed class UpdateProbeUpdatesAccount : EntityPluginBase<UpdateProbe>
    {
        public UpdateProbeUpdatesAccount(string u, string s) : base(u, s) { }

        public static System.Collections.Generic.List<string> Events { get; } = new System.Collections.Generic.List<string>();

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                Events.Clear();
                try
                {
                    ctx.Service.Update(new Entity("account", new Guid("acc00000-0000-0000-0000-000000000203")) { ["name"] = "renamed" });
                }
                catch (FaultException<OrganizationServiceFault> fault)
                {
                    Events.Add("caught:" + fault.Detail.ErrorCode);
                }

                if (ctx.Request.Target.Name == "read check")
                {
                    ctx.SystemService.Create(new Entity("vela_txlog") { ["vela_name"] = "after" });
                    Events.Add("logged");
                }
            });
    }
}
