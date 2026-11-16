using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Plugin;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Store behaviour checked against a live environment on 2026-11-16 (system columns, statecode on Create,
    // ExecuteTransaction faults, the SYSTEM service, organization-owned tables, reflexive relationships).
    public sealed class StoreLiveProbeFidelityTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Past = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        private static readonly Guid Caller = new Guid("7ea00000-0000-0000-0000-0000000000a1");
        private static readonly Guid Other = new Guid("7ea00000-0000-0000-0000-0000000000b2");
        private static readonly Guid CallerBu = new Guid("7ea00000-0000-0000-0000-0000000000c3");
        private static readonly Guid OtherBu = new Guid("7ea00000-0000-0000-0000-0000000000d4");

        private static InMemoryOrganizationService Store() =>
            new InMemoryOrganizationService().WithClock(() => Now).WithCurrentUser(Caller).WithCurrentBusinessUnit(CallerBu);

        private static OrganizationServiceFault Fault(Action action) =>
            Assert.Throws<FaultException<OrganizationServiceFault>>(action).Detail;

        // ---- 1. Server-managed columns supplied by the caller are ignored ----------------------------------

        public static IEnumerable<object[]> SystemColumns() => new[]
        {
            new object[] { "createdon", Past },
            new object[] { "createdby", new EntityReference("systemuser", Other) },
            new object[] { "modifiedon", Past },
            new object[] { "modifiedby", new EntityReference("systemuser", Other) },
            new object[] { "owningbusinessunit", new EntityReference("businessunit", OtherBu) },
        };

        [Theory]
        [MemberData(nameof(SystemColumns))]
        public void A_system_column_supplied_on_create_is_ignored(string column, object value)
        {
            InMemoryOrganizationService store = Store();

            Guid id = store.Create(new Entity("account") { ["name"] = "a", [column] = value });

            AssertStamped(store.Retrieve("account", id, new ColumnSet(true)));
        }

        [Theory]
        [MemberData(nameof(SystemColumns))]
        public void A_system_column_supplied_on_update_is_ignored(string column, object value)
        {
            InMemoryOrganizationService store = Store();
            Guid id = store.Create(new Entity("account") { ["name"] = "a" });

            store.Update(new Entity("account", id) { [column] = value });

            AssertStamped(store.Retrieve("account", id, new ColumnSet(true)));
        }

        private static void AssertStamped(Entity record)
        {
            Assert.Equal(Now, record.GetAttributeValue<DateTime>("createdon"));
            Assert.Equal(Now, record.GetAttributeValue<DateTime>("modifiedon"));
            Assert.Equal(Caller, record.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.Equal(Caller, record.GetAttributeValue<EntityReference>("modifiedby").Id);
            Assert.Equal(CallerBu, record.GetAttributeValue<EntityReference>("owningbusinessunit").Id);
        }

        [Fact]
        public void Overriddencreatedon_backdates_createdon_and_records_the_actual_time()
        {
            InMemoryOrganizationService store = Store();

            Guid id = store.Create(new Entity("account") { ["name"] = "a", ["overriddencreatedon"] = Past, ["createdon"] = Now.AddDays(1) });

            Entity record = store.Retrieve("account", id, new ColumnSet(true));
            Assert.Equal(Past, record.GetAttributeValue<DateTime>("createdon"));
            Assert.Equal(Now, record.GetAttributeValue<DateTime>("overriddencreatedon"));
        }

        [Fact]
        public void Seed_keeps_the_system_columns_it_is_given()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService store = Store().Seed(new Entity("account", id) { ["createdon"] = Past });

            Assert.Equal(Past, store.Retrieve("account", id, new ColumnSet("createdon")).GetAttributeValue<DateTime>("createdon"));
        }

        // ---- 2. statecode without statuscode on Create --------------------------------------------------

        [Fact]
        public void A_statecode_without_a_statuscode_on_create_is_ignored()
        {
            InMemoryOrganizationService store = Store();

            Guid id = store.Create(new Entity("account") { ["name"] = "a", ["statecode"] = new OptionSetValue(1) });

            Entity record = store.Retrieve("account", id, new ColumnSet("statecode", "statuscode"));
            Assert.Equal(0, record.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(1, record.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        [Fact]
        public void A_statecode_without_a_statuscode_on_create_is_ignored_through_the_pipeline()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            Guid id = pipeline.Service.Create(new Entity("contact") { ["lastname"] = "a", ["statecode"] = new OptionSetValue(1) });

            Entity record = Assert.Single(pipeline.GetAll("contact"), c => c.Id == id);
            Assert.Equal(0, record.GetAttributeValue<OptionSetValue>("statecode").Value);
        }

        // ---- 4. ExecuteTransaction fault shape ------------------------------------------------------------

        private static ExecuteTransactionRequest FailingBatch() => new ExecuteTransactionRequest
        {
            ReturnResponses = true,
            Requests = new OrganizationRequestCollection
            {
                new CreateRequest { Target = new Entity("account") { ["name"] = "ok" } },
                new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "missing" } },
                new CreateRequest { Target = new Entity("account") { ["name"] = "never" } },
            },
        };

        private static void AssertTransactionFault(OrganizationServiceFault detail)
        {
            ExecuteTransactionFault fault = Assert.IsType<ExecuteTransactionFault>(detail);
            Assert.Equal(1, fault.FaultedRequestIndex);
            Assert.Equal(unchecked((int)0x80040217), fault.ErrorCode);
            Assert.Contains("Does Not Exist", fault.Message);
        }

        [Fact]
        public void A_failing_execute_transaction_child_faults_with_its_code_and_index_on_the_store()
        {
            InMemoryOrganizationService store = Store();

            AssertTransactionFault(Fault(() => store.Execute(FailingBatch())));

            Assert.Empty(store.GetAll("account"));
        }

        [Fact]
        public void A_failing_execute_transaction_child_faults_with_its_code_and_index_through_the_pipeline()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            AssertTransactionFault(Fault(() => pipeline.Service.Execute(FailingBatch())));

            Assert.Empty(pipeline.GetAll("account"));
        }

        // ---- 5. The SYSTEM service ------------------------------------------------------------------------

        private static Action<SecurityConfiguration> TwoBusinessUnits => s => s
            .BusinessUnit(CallerBu)
            .BusinessUnit(OtherBu, parent: CallerBu)
            .Role("all", r => r
                .Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read, Privilege.Write)
                .Grant("contact", AccessLevel.Organization, Privilege.Create, Privilege.Read, Privilege.Write))
            .User(Other, OtherBu, "all");

        [Fact]
        public void A_record_created_through_the_system_service_is_stamped_with_system_on_behalf_of_the_caller()
        {
            SystemProbePlugin.Reset();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SystemProbePlugin>()
                .WithCurrentUser(Other).WithSecurity(TwoBusinessUnits);

            pipeline.Service.Create(new FakeAccount { Name = "trigger" });

            Guid system = SystemProbePlugin.WhoAmISystem;
            Assert.NotEqual(Guid.Empty, system);
            Assert.NotEqual(Other, system);
            Entity contact = Assert.Single(pipeline.GetAll("contact"));
            Assert.Equal(system, contact.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.Equal(system, contact.GetAttributeValue<EntityReference>("modifiedby").Id);
            Assert.Equal(system, contact.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(Other, contact.GetAttributeValue<EntityReference>("createdonbehalfby").Id);
            Assert.Equal(CallerBu, contact.GetAttributeValue<EntityReference>("owningbusinessunit").Id); // the root BU

            // The record the caller created itself keeps the caller's stamping.
            Entity account = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal(Other, account.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.False(account.Contains("createdonbehalfby"));
        }

        [Fact]
        public void WhoAmI_inside_a_plugin_returns_system_on_every_service_and_the_caller_outside()
        {
            SystemProbePlugin.Reset();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SystemProbePlugin>()
                .WithCurrentUser(Other).WithSecurity(TwoBusinessUnits);

            pipeline.Service.Create(new FakeAccount { Name = "trigger" });

            Assert.Equal(SystemProbePlugin.WhoAmISystem, SystemProbePlugin.WhoAmIUser);
            Assert.Equal(CallerBu, SystemProbePlugin.WhoAmIBusinessUnit);
            Assert.Equal(Other, SystemProbePlugin.ContextUserId);
            WhoAmIResponse outside = (WhoAmIResponse)pipeline.Service.Execute(new WhoAmIRequest());
            Assert.Equal(Other, outside.UserId);
            Assert.Equal(OtherBu, outside.BusinessUnitId);
        }

        // ---- 6. Organization-owned tables ---------------------------------------------------------------

        private static readonly EntityReference Owner = new EntityReference("systemuser", Other);

        [Fact]
        public void Ownerid_on_an_organization_owned_create_or_update_faults()
        {
            InMemoryOrganizationService store = Store().AsOrganizationOwned("subject");
            Guid id = store.Create(new Entity("subject") { ["title"] = "s" });

            OrganizationServiceFault onCreate = Fault(() => store.Create(new Entity("subject") { ["title"] = "t", ["ownerid"] = Owner }));
            OrganizationServiceFault onUpdate = Fault(() => store.Update(new Entity("subject", id) { ["ownerid"] = Owner, ["title"] = "u" }));

            foreach (OrganizationServiceFault fault in new[] { onCreate, onUpdate })
            {
                Assert.Equal(unchecked((int)0x80041103), fault.ErrorCode);
                Assert.StartsWith("'subject' entity doesn't contain attribute with Name = 'ownerid'", fault.Message);
            }

            Entity stored = Assert.Single(store.GetAll("subject"));
            Assert.Equal("s", stored.GetAttributeValue<string>("title"));
        }

        [Fact]
        public void Assign_on_an_organization_owned_table_faults_on_the_store_and_through_the_pipeline()
        {
            Guid id = Guid.NewGuid();
            AssignRequest assign = new AssignRequest { Target = new EntityReference("subject", id), Assignee = Owner };
            InMemoryOrganizationService store = Store().AsOrganizationOwned("subject").Seed(new Entity("subject", id));
            PluginPipeline pipeline = PluginPipeline.Empty().AsOrganizationOwned("subject").Seed(new Entity("subject", id));

            foreach (OrganizationServiceFault fault in new[] { Fault(() => store.Execute(assign)), Fault(() => pipeline.Service.Execute(assign)) })
            {
                Assert.Equal(unchecked((int)0x80040800), fault.ErrorCode);
                Assert.Equal("The 'Assign' method does not support entities of type 'subject'.", fault.Message);
            }
        }

        [Fact]
        public void An_organization_owned_table_known_from_its_metadata_rejects_ownerid()
        {
            InMemoryOrganizationService store = Store().WithEntityMetadata(new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = "subject",
                OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.OrganizationOwned,
            });

            Assert.Equal(unchecked((int)0x80041103), Fault(() => store.Create(new Entity("subject") { ["ownerid"] = Owner })).ErrorCode);
        }

        // ---- 7. Reflexive relationships -----------------------------------------------------------------

        [Fact]
        public void A_reflexive_many_to_many_associate_or_disassociate_without_a_role_faults()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            InMemoryOrganizationService store = Store()
                .WithManyToMany("campaigncampaign_association", "campaigncampaign", "campaign", "campaignid", "campaign", "associatedcampaignid")
                .Seed(new Entity("campaign", a), new Entity("campaign", b));
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("campaign", b) };

            foreach (OrganizationServiceFault fault in new[]
            {
                Fault(() => store.Associate("campaign", a, new Relationship("campaigncampaign_association"), related)),
                Fault(() => store.Disassociate("campaign", a, new Relationship("campaigncampaign_association"), related)),
            })
            {
                Assert.Equal(unchecked((int)0x80040203), fault.ErrorCode);
                Assert.Equal("Entity role must be specified for reflexive relationship 'campaigncampaign_association.'", fault.Message);
            }

            Assert.Empty(store.GetAll("campaigncampaign"));
        }

        [Fact]
        public void A_reflexive_one_to_many_associate_without_a_role_faults()
        {
            Guid a1 = Guid.NewGuid();
            Guid a2 = Guid.NewGuid();
            InMemoryOrganizationService store = Store()
                .WithReferentialRelationship("account", "account", "parentaccountid", schemaName: "account_parent_account")
                .Seed(new Entity("account", a1), new Entity("account", a2));

            OrganizationServiceFault fault = Fault(() => store.Associate("account", a1, new Relationship("account_parent_account"),
                new EntityReferenceCollection { new EntityReference("account", a2) }));

            Assert.Equal(unchecked((int)0x80040203), fault.ErrorCode);
            Assert.Equal("Entity role must be specified for reflexive relationship 'account_parent_account.'", fault.Message);
            Assert.All(store.GetAll("account"), a => Assert.False(a.Contains("parentaccountid")));
        }

        [Theory]
        [InlineData(EntityRole.Referenced)]
        [InlineData(EntityRole.Referencing)]
        public void A_reflexive_one_to_many_associate_with_a_role_sets_the_lookup_on_the_child_side(EntityRole role)
        {
            Guid a1 = Guid.NewGuid();
            Guid a2 = Guid.NewGuid();
            InMemoryOrganizationService store = Store()
                .WithReferentialRelationship("account", "account", "parentaccountid", schemaName: "account_parent_account")
                .Seed(new Entity("account", a1), new Entity("account", a2));
            Relationship relationship = new Relationship("account_parent_account") { PrimaryEntityRole = role };
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("account", a2) };

            store.Associate("account", a1, relationship, related);

            // Referenced (verified live): a1 is the parent, so a2's lookup points at a1. Referencing: a1 is the child.
            (Guid child, Guid parent) = role == EntityRole.Referenced ? (a2, a1) : (a1, a2);
            Assert.Equal(parent, store.Retrieve("account", child, new ColumnSet("parentaccountid")).GetAttributeValue<EntityReference>("parentaccountid").Id);
            Assert.False(store.Retrieve("account", parent, new ColumnSet("parentaccountid")).Contains("parentaccountid"));

            store.Disassociate("account", a1, relationship, related);
            Assert.Null(store.Retrieve("account", child, new ColumnSet("parentaccountid")).GetAttributeValue<EntityReference>("parentaccountid"));
        }
    }

    // Account post-create: creates a contact through the SYSTEM service and records what WhoAmI returns on both the
    // SYSTEM and the user service.
    public sealed class SystemProbePlugin : EntityPluginBase<FakeAccount>
    {
        public SystemProbePlugin(string u, string s) : base(u, s) { }

        public static Guid WhoAmISystem { get; private set; }

        public static Guid WhoAmIUser { get; private set; }

        public static Guid WhoAmIBusinessUnit { get; private set; }

        public static Guid ContextUserId { get; private set; }

        public static void Reset()
        {
            WhoAmISystem = Guid.Empty;
            WhoAmIUser = Guid.Empty;
            WhoAmIBusinessUnit = Guid.Empty;
            ContextUserId = Guid.Empty;
        }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    ctx.SystemService.Create(new Entity("contact") { ["lastname"] = "system child" });
                    WhoAmIResponse system = (WhoAmIResponse)ctx.SystemService.Execute(new WhoAmIRequest());
                    WhoAmISystem = system.UserId;
                    WhoAmIBusinessUnit = system.BusinessUnitId;
                    WhoAmIUser = ((WhoAmIResponse)ctx.Service.Execute(new WhoAmIRequest())).UserId;
                    ContextUserId = ctx.UserId;
                });
    }
}
