using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Mutation-survivor kills for the in-memory store (InMemoryOrganizationService, #448): table declarations, the
    // default-status table, the Execute dispatch of the CRUD messages, reference resolution, Upsert, Include child
    // queries, deep insert relationship resolution, metadata reads and ExecuteMultiple. Each test asserts what a
    // caller of the store observes (returned values, fault types and codes, stored rows).
    public sealed class InMemoryStoreMutationTests
    {
        private const int ObjectDoesNotExist = unchecked((int)0x80040217);
        private const int DuplicateRecord = unchecked((int)0x80040237);

        // ---- Table declarations ----------------------------------------------------------------------------

        // A later AsUserOwned overrides an earlier AsOrganizationOwned, so Create stamps the owner columns.
        [Fact]
        public void AsUserOwned_overrides_an_earlier_organization_owned_declaration()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .AsOrganizationOwned("new_widget")
                .AsUserOwned("new_widget");

            Guid id = svc.Create(new Entity("new_widget") { ["new_name"] = "w" });

            Entity stored = svc.Retrieve("new_widget", id, new ColumnSet(true));
            Assert.Equal(InMemoryOrganizationService.DefaultCallingUser, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        // Elastic tables are ownable: AsElastic replaces an earlier organization-owned declaration.
        [Fact]
        public void AsElastic_overrides_an_earlier_organization_owned_declaration()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .AsOrganizationOwned("new_event")
                .AsElastic("new_event");

            Guid id = svc.Create(new Entity("new_event") { ["new_name"] = "e" });

            Entity stored = svc.Retrieve("new_event", id, new ColumnSet(true));
            Assert.Equal(InMemoryOrganizationService.DefaultCallingUser, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Theory]
        [InlineData(" ", "contact", "parentcustomerid")]
        [InlineData("account", " ", "parentcustomerid")]
        [InlineData("account", "contact", " ")]
        public void WithRelationship_rejects_any_blank_part(string referenced, string referencing, string attribute)
        {
            Assert.Throws<ArgumentException>(() => new InMemoryOrganizationService()
                .WithRelationship(referenced, referencing, attribute, new CascadeConfiguration()));
        }

        [Fact]
        public void WithDefaultStatus_requires_a_table_name()
        {
            Assert.Throws<ArgumentException>(() => new InMemoryOrganizationService().WithDefaultStatus("", 1, 2));
        }

        [Fact]
        public void Read_only_virtual_table_rejects_update_and_delete_of_an_existing_row()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .AsVirtual("new_external", readOnly: true)
                .Seed(new Entity("new_external", id) { ["new_name"] = "x" });

            Assert.Throws<InvalidOperationException>(() => svc.Update(new Entity("new_external", id) { ["new_name"] = "y" }));
            Assert.Throws<InvalidOperationException>(() => svc.Delete("new_external", id));

            Entity row = Assert.Single(svc.GetAll("new_external"));
            Assert.Equal("x", row.GetAttributeValue<string>("new_name"));
        }

        // ---- Default status / SetState ---------------------------------------------------------------------

        private static Entity SetStateAndRead(InMemoryOrganizationService svc, string table, Guid id, int state, int status)
        {
            svc.Execute(new SetStateRequest
            {
                EntityMoniker = new EntityReference(table, id),
                State = new OptionSetValue(state),
                Status = new OptionSetValue(status),
            });
            return svc.Retrieve(table, id, new ColumnSet(true));
        }

        [Fact]
        public void SetState_to_active_with_status_minus_one_lands_on_status_1()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", id));

            Entity read = SetStateAndRead(svc, "account", id, 0, -1);

            Assert.Equal(0, read.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(1, read.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        [Fact]
        public void SetState_with_an_explicit_status_keeps_that_status()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", id));

            Entity read = SetStateAndRead(svc, "account", id, 1, 5);

            Assert.Equal(1, read.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(5, read.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        // The declared default is keyed by (table, state): table "new_t" state 11 must not leak into "new_t1" state 1.
        [Fact]
        public void Declared_default_status_is_keyed_by_table_and_state_separately()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithDefaultStatus("new_t", 11, 7)
                .Seed(new Entity("new_t1", id));

            Entity read = SetStateAndRead(svc, "new_t1", id, 1, -1);

            Assert.Equal(2, read.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        // ---- Execute dispatch --------------------------------------------------------------------------------

        [Fact]
        public void CreateRequest_returns_the_new_id_and_RetrieveMultipleRequest_returns_the_rows()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            Guid id = ((CreateResponse)svc.Execute(new CreateRequest { Target = new Entity("account") { ["name"] = "Zava" } })).id;

            Assert.NotEqual(Guid.Empty, id);
            EntityCollection rows = ((RetrieveMultipleResponse)svc.Execute(new RetrieveMultipleRequest
            {
                Query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") },
            })).EntityCollection;
            Assert.Equal(id, Assert.Single(rows.Entities).Id);
        }

        [Fact]
        public void UpdateRequest_by_id_updates_the_row()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", id) { ["name"] = "old" });

            svc.Execute(new UpdateRequest { Target = new Entity("account", id) { ["name"] = "new" } });

            Assert.Equal("new", svc.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Creating_a_duplicate_id_faults_with_DuplicateRecord()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", id));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Create(new Entity("account", id) { ["name"] = "again" }));

            Assert.Equal(DuplicateRecord, fault.Detail.ErrorCode);
        }

        // A reference with no id and a null key collection is just a (missing) empty id: the not-found fault, with
        // the platform's "With Id = ... Does Not Exist" message, never a NullReferenceException.
        [Fact]
        public void Retrieve_and_delete_with_no_id_and_null_keys_fault_as_not_found()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", Guid.NewGuid()));

            FaultException<OrganizationServiceFault> retrieve = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Execute(new RetrieveRequest
                {
                    Target = new EntityReference("account", Guid.Empty) { KeyAttributes = null },
                    ColumnSet = new ColumnSet(true),
                }));
            Assert.Equal(ObjectDoesNotExist, retrieve.Detail.ErrorCode);
            Assert.Equal("account With Id = 00000000-0000-0000-0000-000000000000 Does Not Exist", retrieve.Detail.Message);

            FaultException<OrganizationServiceFault> delete = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Execute(new DeleteRequest { Target = new EntityReference("account", Guid.Empty) { KeyAttributes = null } }));
            Assert.Equal(ObjectDoesNotExist, delete.Detail.ErrorCode);
        }

        [Fact]
        public void UpdateRequest_with_no_id_and_null_keys_faults_as_not_found()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", Guid.NewGuid()));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Execute(new UpdateRequest { Target = new Entity("account") { KeyAttributes = null, ["name"] = "x" } }));

            Assert.Equal(ObjectDoesNotExist, fault.Detail.ErrorCode);
        }

        [Fact]
        public void Retrieve_by_an_unknown_alternate_key_faults_with_ObjectDoesNotExist()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", Guid.NewGuid()) { ["accountnumber"] = "A-1" });

            EntityReference byKey = new EntityReference("account", "accountnumber", "A-404");
            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Execute(new RetrieveRequest { Target = byKey, ColumnSet = new ColumnSet(true) }));

            Assert.Equal(ObjectDoesNotExist, fault.Detail.ErrorCode);
        }

        // ---- Upsert ----------------------------------------------------------------------------------------

        [Fact]
        public void Upsert_by_the_id_of_an_existing_row_updates_it()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", id) { ["name"] = "old" });

            UpsertResponse response = (UpsertResponse)svc.Execute(new UpsertRequest { Target = new Entity("account", id) { ["name"] = "new" } });

            Assert.Equal(false, response.Results["RecordCreated"]); // the response carries the flag explicitly
            Assert.Equal(id, response.Target.Id);
            Entity only = Assert.Single(svc.GetAll("account"));
            Assert.Equal("new", only.GetAttributeValue<string>("name"));
        }

        // Every key attribute must match: a row matching only part of a composite key is not the upsert target.
        [Fact]
        public void Upsert_by_a_composite_key_ignores_a_partial_match()
        {
            Guid existing = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", existing) { ["name"] = "Zava", ["accountnumber"] = "1" });

            Entity target = new Entity("account");
            target.KeyAttributes["name"] = "Zava";
            target.KeyAttributes["accountnumber"] = "2";
            UpsertResponse response = (UpsertResponse)svc.Execute(new UpsertRequest { Target = target });

            Assert.True(response.RecordCreated);
            Assert.NotEqual(existing, response.Target.Id);
            Assert.Equal(2, svc.GetAll("account").Count);
            Assert.Equal("1", svc.Retrieve("account", existing, new ColumnSet("accountnumber")).GetAttributeValue<string>("accountnumber"));
        }

        // With neither an id nor a key nothing can match, so the upsert creates (it never picks an arbitrary row). This
        // pins the store's behaviour; the platform's answer to such an Upsert is not verified live.
        [Fact]
        public void Upsert_with_no_id_and_no_key_creates_a_new_row()
        {
            Guid existing = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", existing) { ["name"] = "Zava" });

            UpsertResponse response = (UpsertResponse)svc.Execute(new UpsertRequest { Target = new Entity("account") { ["name"] = "Acme" } });

            Assert.True(response.RecordCreated);
            Assert.NotEqual(existing, response.Target.Id);
            Assert.Equal("Zava", svc.Retrieve("account", existing, new ColumnSet("name")).GetAttributeValue<string>("name"));
            Assert.Equal(2, svc.GetAll("account").Count);
        }

        // ---- Include (RetrieveRequest.RelatedEntitiesQuery) ------------------------------------------------

        private static (InMemoryOrganizationService Svc, Guid AccountId) AccountWithTwoContacts()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid");
            Guid accountId = svc.Create(new Entity("account") { ["name"] = "Contoso" });
            EntityReference parent = new EntityReference("account", accountId);
            svc.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = parent });
            svc.Create(new Entity("contact") { ["firstname"] = "Bob", ["parentcustomerid"] = parent });
            return (svc, accountId);
        }

        private static EntityCollection IncludeContacts(InMemoryOrganizationService svc, Guid accountId, QueryExpression childQuery)
        {
            Entity account = ((RetrieveResponse)svc.Execute(new RetrieveRequest
            {
                Target = new EntityReference("account", accountId),
                ColumnSet = new ColumnSet("name"),
                RelatedEntitiesQuery = new RelationshipQueryCollection { { new Relationship("account_contact"), childQuery } },
            })).Entity;
            return account.RelatedEntities[new Relationship("account_contact")];
        }

        [Fact]
        public void Include_applies_the_child_query_criteria()
        {
            (InMemoryOrganizationService svc, Guid accountId) = AccountWithTwoContacts();
            QueryExpression childQuery = new QueryExpression("contact") { ColumnSet = new ColumnSet("firstname") };
            childQuery.Criteria.AddCondition("firstname", ConditionOperator.Equal, "Bob");

            Entity only = Assert.Single(IncludeContacts(svc, accountId, childQuery).Entities);

            Assert.Equal("Bob", only.GetAttributeValue<string>("firstname"));
        }

        [Fact]
        public void Include_with_a_null_child_criteria_returns_every_child()
        {
            (InMemoryOrganizationService svc, Guid accountId) = AccountWithTwoContacts();
            QueryExpression childQuery = new QueryExpression("contact") { ColumnSet = new ColumnSet("firstname"), Criteria = null };

            Assert.Equal(2, IncludeContacts(svc, accountId, childQuery).Entities.Count);
        }

        // ---- Deep insert -----------------------------------------------------------------------------------

        [Fact]
        public void Deep_insert_with_an_empty_related_collection_creates_just_the_parent()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();
            Entity account = new Entity("account") { ["name"] = "Contoso" };
            account.RelatedEntities[new Relationship("account_contact")] = new EntityCollection();

            Guid id = svc.Create(account);

            Assert.Equal(id, Assert.Single(svc.GetAll("account")).Id);
            Assert.Empty(svc.GetAll("contact"));
        }

        private static Entity AccountWithChild(string schemaName)
        {
            Entity account = new Entity("account") { ["name"] = "Contoso" };
            account.RelatedEntities[new Relationship(schemaName)] =
                new EntityCollection { Entities = { new Entity("contact") { ["firstname"] = "Ada" } } };
            return account;
        }

        // Two relationships link account to contact; the one whose schema name matches is used.
        [Fact]
        public void Deep_insert_uses_the_schema_name_match_when_several_relationships_link_the_tables()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithReferentialRelationship("account", "contact", "new_secondaryaccountid");

            Guid accountId = svc.Create(AccountWithChild("account_contact"));

            Entity contact = Assert.Single(svc.GetAll("contact"));
            Assert.Equal(accountId, contact.GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        [Fact]
        public void Deep_insert_with_several_relationships_and_no_schema_match_is_rejected()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithReferentialRelationship("account", "contact", "new_secondaryaccountid");

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Create(AccountWithChild("contact_customer_accounts")));

            Assert.Contains("several 1:N relationships", ex.Message);
            Assert.Empty(svc.GetAll("account")); // the deep insert rolled back
        }

        [Fact]
        public void Deep_insert_without_a_declared_relationship_names_the_missing_declaration()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Create(AccountWithChild("contact_customer_accounts")));

            Assert.Contains("no 1:N relationship from 'account' to 'contact'", ex.Message);
        }

        // ---- Metadata reads --------------------------------------------------------------------------------

        [Fact]
        public void RetrieveAttribute_for_an_unregistered_attribute_explains_how_to_register_it()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account").Attribute("name", AttributeTypeCode.String).Build());

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Execute(
                new RetrieveAttributeRequest { EntityLogicalName = "account", LogicalName = "nope" }));

            Assert.Contains("Attribute metadata 'account.nope' is not registered", ex.Message);
            Assert.Contains("FakeEntityMetadata.For(...).Attribute(...)", ex.Message);
        }

        [Fact]
        public void RetrieveEntity_by_an_unknown_metadata_id_explains_how_to_register_it()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account").Build());

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Execute(
                new RetrieveEntityRequest { LogicalName = "nope", MetadataId = Guid.NewGuid() }));

            Assert.Contains("Entity metadata 'nope' is not registered", ex.Message);
        }

        // ---- ExecuteMultiple -------------------------------------------------------------------------------

        // Without Settings the batch stops at the first fault (ContinueOnError defaults to false). This pins the store's
        // default; Dataverse may instead reject an ExecuteMultipleRequest without Settings (not verified live).
        [Fact]
        public void ExecuteMultiple_without_settings_stops_at_the_first_fault()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)svc.Execute(new ExecuteMultipleRequest
            {
                Requests = new OrganizationRequestCollection
                {
                    new DeleteRequest { Target = new EntityReference("account", Guid.NewGuid()) },
                    new DeleteRequest { Target = new EntityReference("account", Guid.NewGuid()) },
                },
                Settings = null,
            });

            Assert.True(response.IsFaulted);
            Assert.Equal(0, Assert.Single(response.Responses).RequestIndex);
        }

        // A faulted item that is not a platform fault still carries the exception's message.
        [Fact]
        public void ExecuteMultiple_item_fault_carries_the_message_of_a_non_platform_exception()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)svc.Execute(new ExecuteMultipleRequest
            {
                Requests = new OrganizationRequestCollection { new OrganizationRequest("vela_NotModelled") },
                Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
            });

            ExecuteMultipleResponseItem item = Assert.Single(response.Responses);
            Assert.Contains("vela_NotModelled", item.Fault.Message);
        }
    }
}
