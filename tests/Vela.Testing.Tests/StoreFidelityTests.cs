using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // An organization-owned early-bound table: a generated class has no OwnerId property for one.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class FakeOrgSetting : Entity
    {
        public const string EntityLogicalNameValue = "vela_setting";

        public FakeOrgSetting()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("vela_settingid")]
        public Guid? VelaSettingId
        {
            get => GetAttributeValue<Guid?>("vela_settingid");
            set => SetAttributeValue("vela_settingid", value);
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }
    }

    // In-memory store fidelity: ownership resolution, update by alternate key, atomic delete cascade, null joins and
    // N:N Associate sides.
    public sealed class StoreFidelityTests
    {
        private static readonly Guid Organization = new Guid("0e000000-0000-0000-0000-0000000000a1");

        // --- Ownership: declaration > registered metadata > early-bound type > user-owned ---

        [Fact]
        public void Pipeline_create_of_an_early_bound_organization_owned_row_stamps_no_owner()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().WithOrganizationId(Organization);

            Guid id = pipeline.Service.Create(new FakeOrgSetting { Name = "s" });

            Entity stored = pipeline.Service.Retrieve(FakeOrgSetting.EntityLogicalNameValue, id, new ColumnSet(true));
            Assert.False(stored.Contains("ownerid"));
            Assert.False(stored.Contains("owninguser"));
            Assert.Equal(Organization, stored.GetAttributeValue<EntityReference>("organizationid").Id);
        }

        [Fact]
        public void Pipeline_upsert_create_of_an_early_bound_organization_owned_row_stamps_no_owner()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            Guid id = Guid.NewGuid();
            pipeline.Service.Execute(new UpsertRequest { Target = new FakeOrgSetting { Id = id, Name = "s" } });

            Entity stored = pipeline.Service.Retrieve(FakeOrgSetting.EntityLogicalNameValue, id, new ColumnSet(true));
            Assert.False(stored.Contains("ownerid"));
        }

        [Fact]
        public void Pipeline_create_of_an_early_bound_user_owned_row_still_stamps_the_owner()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            Guid id = pipeline.Service.Create(new FakeOwnable { Name = "o" });

            Entity stored = pipeline.Service.Retrieve(FakeOwnable.EntityLogicalNameValue, id, new ColumnSet(true));
            Assert.Equal(InMemoryOrganizationService.DefaultCallingUser, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Registered_metadata_ownership_applies_to_a_late_bound_row()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("vela_config").Ownership(OwnershipTypes.OrganizationOwned).Build());

            Guid id = svc.Create(new Entity("vela_config") { ["name"] = "c" });

            Entity stored = svc.Retrieve("vela_config", id, new ColumnSet(true));
            Assert.False(stored.Contains("ownerid"));
            Assert.True(stored.Contains("organizationid"));
        }

        [Fact]
        public void Registered_metadata_ownership_wins_over_the_early_bound_type()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For(FakeOrgSetting.EntityLogicalNameValue).Ownership(OwnershipTypes.UserOwned).Build());

            Guid id = svc.Create(new FakeOrgSetting { Name = "s" });

            Assert.True(svc.Retrieve(FakeOrgSetting.EntityLogicalNameValue, id, new ColumnSet(true)).Contains("ownerid"));
        }

        [Fact]
        public void Declared_ownership_wins_over_registered_metadata()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("vela_config").Ownership(OwnershipTypes.OrganizationOwned).Build())
                .AsUserOwned("vela_config");

            Guid id = svc.Create(new Entity("vela_config"));

            Assert.True(svc.Retrieve("vela_config", id, new ColumnSet(true)).Contains("ownerid"));
        }

        [Fact]
        public void Metadata_derived_from_a_type_without_ownerid_is_organization_owned()
        {
            EntityMetadata metadata = FakeEntityMetadata.From<FakeOrgSetting>().Build();

            Assert.Equal(OwnershipTypes.OrganizationOwned, metadata.OwnershipType);
        }

        // --- Update by alternate key on the store itself ---

        private static InMemoryOrganizationService KeyedAccounts(out Guid id)
        {
            id = Guid.NewGuid();
            return new InMemoryOrganizationService()
                .WithAlternateKey("account", "accountnumber")
                .Seed(new Entity("account", id) { ["accountnumber"] = "A-1", ["name"] = "before" });
        }

        private static Entity ByKey(string number) => new Entity("account", "accountnumber", number);

        [Fact]
        public void Update_by_alternate_key_updates_the_record()
        {
            InMemoryOrganizationService svc = KeyedAccounts(out Guid id);
            Entity update = ByKey("A-1");
            update["name"] = "after";

            svc.Update(update);

            Assert.Equal("after", svc.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
            Assert.Equal(Guid.Empty, update.Id); // the caller's entity is untouched
        }

        [Fact]
        public void UpdateMultiple_by_alternate_key_updates_the_record()
        {
            InMemoryOrganizationService svc = KeyedAccounts(out Guid id);
            Entity update = ByKey("A-1");
            update["name"] = "after";

            svc.Execute(new UpdateMultipleRequest { Targets = new EntityCollection { EntityName = "account", Entities = { update } } });

            Assert.Equal("after", svc.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Assign_by_alternate_key_reassigns_the_record()
        {
            InMemoryOrganizationService svc = KeyedAccounts(out Guid id);
            Guid newOwner = Guid.NewGuid();

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", "accountnumber", "A-1"),
                Assignee = new EntityReference("systemuser", newOwner),
            });

            Assert.Equal(newOwner, svc.Retrieve("account", id, new ColumnSet("ownerid")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Update_by_an_alternate_key_that_matches_nothing_faults_with_the_key_not_found_code()
        {
            InMemoryOrganizationService svc = KeyedAccounts(out _);
            Entity update = ByKey("missing");
            update["name"] = "after";

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Update(update));

            Assert.Equal(unchecked((int)0x80060891), fault.Detail.ErrorCode);
        }

        // --- Delete cascade is atomic ---

        [Fact]
        public void A_restrict_on_a_grandchild_leaves_the_store_unchanged()
        {
            Guid account = Guid.NewGuid();
            Guid lead = Guid.NewGuid();
            Guid freeContact = Guid.NewGuid();
            Guid blockedContact = Guid.NewGuid();
            Guid task = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                // Declared first, so the link is removed before the cascade reaches the blocked grandchild.
                .WithReferentialRelationship("account", "lead", "parentaccountid")
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithReferentialRelationship("contact", "task", "regardingobjectid", restrictDelete: true)
                .Seed(
                    new Entity("account", account),
                    new Entity("lead", lead) { ["parentaccountid"] = new EntityReference("account", account) },
                    new Entity("contact", freeContact) { ["parentcustomerid"] = new EntityReference("account", account) },
                    new Entity("contact", blockedContact) { ["parentcustomerid"] = new EntityReference("account", account) },
                    new Entity("task", task) { ["regardingobjectid"] = new EntityReference("contact", blockedContact) });

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Delete("account", account));

            Assert.Equal(unchecked((int)0x80040227), fault.Detail.ErrorCode);
            Assert.Single(svc.GetAll("account"));
            Assert.Equal(2, svc.GetAll("contact").Count);
            Assert.Single(svc.GetAll("task"));
            Assert.Equal(account, Assert.Single(svc.GetAll("lead")).GetAttributeValue<EntityReference>("parentaccountid").Id);
        }

        // --- Joins and Any/All: a null never matches ---

        private static InMemoryOrganizationService RowsWithEmptyCodes() => new InMemoryOrganizationService()
            .Seed(
                new Entity("account", Guid.NewGuid()) { ["name"] = "no code" },
                new Entity("contact", Guid.NewGuid()) { ["lastname"] = "no code" });

        [Fact]
        public void An_inner_join_does_not_match_null_to_null()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.LinkEntities.Add(new LinkEntity("account", "contact", "vela_code", "vela_code", JoinOperator.Inner));

            Assert.Empty(RowsWithEmptyCodes().RetrieveMultiple(query).Entities);
        }

        [Fact]
        public void A_left_outer_join_keeps_the_row_without_joining_null_to_null()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.LinkEntities.Add(new LinkEntity("account", "contact", "vela_code", "vela_code", JoinOperator.LeftOuter)
            {
                Columns = new ColumnSet("lastname"),
                EntityAlias = "c",
            });

            Entity row = Assert.Single(RowsWithEmptyCodes().RetrieveMultiple(query).Entities);
            Assert.False(row.Contains("c.lastname"));
        }

        [Fact]
        public void An_any_filter_does_not_match_null_to_null()
        {
            InMemoryOrganizationService svc = RowsWithEmptyCodes();
            QueryExpression any = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            any.Criteria.AnyAllFilterLinkEntity = new LinkEntity("account", "contact", "vela_code", "vela_code", JoinOperator.Any);
            QueryExpression notAny = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            notAny.Criteria.AnyAllFilterLinkEntity = new LinkEntity("account", "contact", "vela_code", "vela_code", JoinOperator.NotAny);

            Assert.Empty(svc.RetrieveMultiple(any).Entities);
            Assert.Single(svc.RetrieveMultiple(notAny).Entities);
        }

        [Fact]
        public void A_join_still_matches_equal_values()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(
                new Entity("account", Guid.NewGuid()) { ["vela_code"] = "X" },
                new Entity("contact", Guid.NewGuid()) { ["vela_code"] = "X" });
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet(true) };
            query.LinkEntities.Add(new LinkEntity("account", "contact", "vela_code", "vela_code", JoinOperator.Inner));

            Assert.Single(svc.RetrieveMultiple(query).Entities);
        }

        // --- N:N Associate sides ---

        private static InMemoryOrganizationService AccountContacts(out Guid account, out Guid contact, out Guid lead)
        {
            account = Guid.NewGuid();
            contact = Guid.NewGuid();
            lead = Guid.NewGuid();
            return new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account", account), new Entity("contact", contact), new Entity("lead", lead));
        }

        [Fact]
        public void N_to_n_associate_from_a_table_the_relationship_does_not_join_faults()
        {
            InMemoryOrganizationService svc = AccountContacts(out _, out Guid contact, out Guid lead);

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
                svc.Associate("lead", lead, new Relationship("account_contacts"), new EntityReferenceCollection { new EntityReference("contact", contact) }));

            Assert.Equal(unchecked((int)0x80040203), fault.Detail.ErrorCode);
            Assert.Contains("does not play a role in many-to-many relationship", fault.Detail.Message);
            Assert.Empty(svc.GetAll("account_contact"));
        }

        [Fact]
        public void N_to_n_associate_to_a_record_of_the_wrong_table_faults_with_the_sql_constraint_code()
        {
            InMemoryOrganizationService svc = AccountContacts(out Guid account, out _, out Guid lead);

            // Verified live: the related side is not validated, the intersect row's foreign key fails in SQL.
            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
                svc.Associate("account", account, new Relationship("account_contacts"), new EntityReferenceCollection { new EntityReference("lead", lead) }));

            Assert.Equal(unchecked((int)0x80048D21), fault.Detail.ErrorCode);
            Assert.Empty(svc.GetAll("account_contact"));
        }

        [Fact]
        public void N_to_n_associate_from_either_side_still_writes_the_intersect_row()
        {
            InMemoryOrganizationService svc = AccountContacts(out Guid account, out Guid contact, out _);

            svc.Associate("contact", contact, new Relationship("account_contacts"), new EntityReferenceCollection { new EntityReference("account", account) });

            Entity row = Assert.Single(svc.GetAll("account_contact"));
            Assert.Equal(account, row.GetAttributeValue<EntityReference>("accountid").Id);
            Assert.Equal(contact, row.GetAttributeValue<EntityReference>("contactid").Id);
        }

        // --- MessageNotModelledException lists what the store handles ---

        [Fact]
        public void The_not_modelled_message_lists_every_message_the_store_handles()
        {
            MessageNotModelledException ex = Assert.Throws<MessageNotModelledException>(
                () => new InMemoryOrganizationService().Execute(new OrganizationRequest("vela_Unknown")));

            foreach (string message in new[] { "Upsert", "Merge", "WhoAmI", "GrantAccess", "ModifyAccess", "RevokeAccess", "RetrievePrincipalAccess", "RetrieveSharedPrincipalsAndAccess", "AddUserToRecordTeam", "RemoveUserFromRecordTeam" })
            {
                Assert.Contains(message, ex.Message);
            }
        }
    }
}
