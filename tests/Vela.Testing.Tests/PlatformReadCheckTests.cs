using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Plugin;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Verified live (2026-11-18): SetState, Merge and Associate (1:N) check Read on the row they write before writing
    // it, and that fault, caught by a plugin inside the transaction, ends the transaction (unlike the Update message's
    // Read check). Assign does not check Read.
    public sealed class PlatformReadCheckTests
    {
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);
        private const int IsvReducedTransactionCount = -2146893812;

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000301");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000302");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000003b1");
        internal static readonly Guid Account = new Guid("acc00000-0000-0000-0000-000000000303");
        internal static readonly Guid Master = new Guid("c0000000-0000-0000-0000-000000000304");
        internal static readonly Guid Subordinate = new Guid("c0000000-0000-0000-0000-000000000305");

        private static PluginPipeline PipelineFor(bool contactRead) =>
            PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid", "contact_customer_accounts")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("tester", r =>
                    {
                        r.Grant(PlatformProbe.LogicalNameValue, AccessLevel.Organization, Privilege.Create, Privilege.Read);
                        r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write, Privilege.Append, Privilege.AppendTo);
                        r.Grant("contact", AccessLevel.Organization, Privilege.Write, Privilege.Append, Privilege.AppendTo, Privilege.Assign, Privilege.Share);
                        if (contactRead)
                        {
                            r.Grant("contact", AccessLevel.Organization, Privilege.Read);
                        }
                    })
                    .User(Alice, SalesBu, "tester")
                    .User(Bob, SalesBu, "tester"))
                .Seed(
                    new Entity("account", Account) { ["name"] = "a", ["ownerid"] = new EntityReference("systemuser", Bob), ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu) },
                    Contact(Master),
                    Contact(Subordinate))
                .Register<PlatformProbeRunsMessage>();

        private static Entity Contact(Guid id) => new Entity("contact", id)
        {
            ["statecode"] = new OptionSetValue(0),
            ["statuscode"] = new OptionSetValue(1),
            ["ownerid"] = new EntityReference("systemuser", Bob),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        [Theory]
        [InlineData("setstate")]
        [InlineData("merge")]
        [InlineData("associate")]
        public void A_caught_Read_check_fault_of_a_platform_write_ends_the_transaction(string message)
        {
            PluginPipeline pipeline = PipelineFor(contactRead: false);

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Create(new PlatformProbe { Name = message }));

            Assert.Equal(IsvReducedTransactionCount, fault.Detail.ErrorCode);
            Assert.Equal(new[] { "caught:" + PrincipalPrivilegeDenied }, PlatformProbeRunsMessage.Events);
            Assert.Empty(pipeline.GetAll(PlatformProbe.LogicalNameValue));
        }

        [Theory]
        [InlineData("setstate")]
        [InlineData("merge")]
        [InlineData("associate")]
        public void A_platform_write_without_Read_on_the_row_fails_with_the_read_privilege_fault(string message)
        {
            PluginPipeline pipeline = PipelineFor(contactRead: false);

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => PlatformProbeRunsMessage.Run(pipeline.Service, message));

            Assert.Equal(PrincipalPrivilegeDenied, fault.Detail.ErrorCode);
            Assert.Contains("prvReadcontact", fault.Detail.Message);
        }

        [Theory]
        [InlineData("setstate", 1)]
        [InlineData("merge", 1)]
        [InlineData("associate", 0)]
        public void A_platform_write_with_Read_on_the_row_succeeds(string message, int subordinateState)
        {
            PluginPipeline pipeline = PipelineFor(contactRead: true);

            Guid id = pipeline.Service.Create(new PlatformProbe { Name = message });

            Assert.NotEqual(Guid.Empty, id);
            Assert.Equal(new[] { "ok" }, PlatformProbeRunsMessage.Events);
            Entity subordinate = pipeline.GetAll("contact").Single(c => c.Id == Subordinate);
            Assert.Equal(subordinateState, subordinate.GetAttributeValue<OptionSetValue>("statecode").Value);
            if (message == "associate")
            {
                Assert.Equal(Account, subordinate.GetAttributeValue<EntityReference>("parentcustomerid").Id);
            }
        }

        [Fact]
        public void Assign_does_not_check_Read_on_the_row()
        {
            PluginPipeline pipeline = PipelineFor(contactRead: false);

            pipeline.Service.Execute(new AssignRequest { Target = new EntityReference("contact", Subordinate), Assignee = new EntityReference("systemuser", Alice) });

            Assert.Equal(Alice, pipeline.GetAll("contact").Single(c => c.Id == Subordinate).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void The_store_alone_checks_Read_on_SetState()
        {
            InMemoryOrganizationService store = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("tester", r => r.Grant("contact", AccessLevel.Organization, Privilege.Write))
                    .User(Alice, SalesBu, "tester"))
                .Seed(Contact(Subordinate));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => store.Execute(new SetStateRequest { EntityMoniker = new EntityReference("contact", Subordinate), State = new OptionSetValue(1), Status = new OptionSetValue(2) }));

            Assert.Equal(PrincipalPrivilegeDenied, fault.Detail.ErrorCode);
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class PlatformProbe : Entity
    {
        public const string LogicalNameValue = "vela_platformprobe";

        public PlatformProbe() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    // Runs SetState, Merge or Associate (1:N) on contacts as the calling user and swallows the fault.
    public sealed class PlatformProbeRunsMessage : EntityPluginBase<PlatformProbe>
    {
        public PlatformProbeRunsMessage(string u, string s) : base(u, s) { }

        public static List<string> Events { get; } = new List<string>();

        internal static void Run(IOrganizationService service, string message)
        {
            switch (message)
            {
                case "setstate":
                    service.Execute(new SetStateRequest
                    {
                        EntityMoniker = new EntityReference("contact", PlatformReadCheckTests.Subordinate),
                        State = new OptionSetValue(1),
                        Status = new OptionSetValue(2),
                    });
                    break;
                case "merge":
                    service.Execute(new MergeRequest
                    {
                        Target = new EntityReference("contact", PlatformReadCheckTests.Master),
                        SubordinateId = PlatformReadCheckTests.Subordinate,
                        UpdateContent = new Entity("contact"),
                    });
                    break;
                default:
                    service.Associate("account", PlatformReadCheckTests.Account, new Relationship("contact_customer_accounts"),
                        new EntityReferenceCollection { new EntityReference("contact", PlatformReadCheckTests.Subordinate) });
                    break;
            }
        }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                Events.Clear();
                try
                {
                    Run(ctx.Service, ctx.Request.Target.Name!);
                    Events.Add("ok");
                }
                catch (FaultException<OrganizationServiceFault> fault)
                {
                    Events.Add("caught:" + fault.Detail.ErrorCode);
                }
            });
    }
}
