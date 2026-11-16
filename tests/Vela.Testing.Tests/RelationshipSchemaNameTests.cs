using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // A 1:N relationship declared under its real schema name: Associate/Disassociate, a deep insert and an Include
    // find it by that name, and two relationships cannot share one.
    public sealed class RelationshipSchemaNameTests
    {
        private static InMemoryOrganizationService TwoAccountContactRelationships(out Guid account, out Guid contact)
        {
            account = Guid.NewGuid();
            contact = Guid.NewGuid();
            return new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid", schemaName: "contact_customer_accounts")
                .WithReferentialRelationship("account", "contact", "vela_billingaccountid", schemaName: "vela_account_contact_billing")
                .Seed(new Entity("account", account), new Entity("contact", contact));
        }

        [Fact]
        public void Associate_by_the_real_schema_name_sets_that_relationships_lookup()
        {
            InMemoryOrganizationService svc = TwoAccountContactRelationships(out Guid account, out Guid contact);

            svc.Associate("account", account, new Relationship("vela_account_contact_billing"), new EntityReferenceCollection { new EntityReference("contact", contact) });

            Entity stored = svc.Retrieve("contact", contact, new ColumnSet(true));
            Assert.Equal(account, stored.GetAttributeValue<EntityReference>("vela_billingaccountid").Id);
            Assert.False(stored.Contains("parentcustomerid"));
        }

        [Fact]
        public void Disassociate_by_the_real_schema_name_clears_that_relationships_lookup()
        {
            InMemoryOrganizationService svc = TwoAccountContactRelationships(out Guid account, out Guid contact);
            svc.Associate("account", account, new Relationship("contact_customer_accounts"), new EntityReferenceCollection { new EntityReference("contact", contact) });
            svc.Associate("account", account, new Relationship("vela_account_contact_billing"), new EntityReferenceCollection { new EntityReference("contact", contact) });

            svc.Disassociate("account", account, new Relationship("contact_customer_accounts"), new EntityReferenceCollection { new EntityReference("contact", contact) });

            Entity stored = svc.Retrieve("contact", contact, new ColumnSet(true));
            Assert.Null(stored.GetAttributeValue<EntityReference>("parentcustomerid"));
            Assert.Equal(account, stored.GetAttributeValue<EntityReference>("vela_billingaccountid").Id);
        }

        [Fact]
        public void A_deep_insert_resolves_the_relationship_by_its_real_schema_name()
        {
            InMemoryOrganizationService svc = TwoAccountContactRelationships(out _, out _);
            Entity parent = new Entity("account");
            parent.RelatedEntities[new Relationship("vela_account_contact_billing")] = new EntityCollection { Entities = { new Entity("contact") { ["lastname"] = "child" } } };

            Guid id = svc.Create(parent);

            Entity child = Assert.Single(svc.GetAll("contact"), c => c.GetAttributeValue<string>("lastname") == "child");
            Assert.Equal(id, child.GetAttributeValue<EntityReference>("vela_billingaccountid").Id);
            Assert.False(child.Contains("parentcustomerid"));
        }

        [Fact]
        public void An_ambiguous_deep_insert_names_the_schema_name_parameter()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid")
                .WithReferentialRelationship("account", "contact", "vela_billingaccountid");
            Entity parent = new Entity("account");
            parent.RelatedEntities[new Relationship("account_contacts_unknown")] = new EntityCollection { Entities = { new Entity("contact") } };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Create(parent));

            Assert.Contains("schemaName: \"account_contacts_unknown\"", ex.Message);
        }

        [Fact]
        public void An_include_resolves_the_relationship_by_its_real_schema_name()
        {
            InMemoryOrganizationService svc = TwoAccountContactRelationships(out Guid account, out Guid contact);
            svc.Associate("account", account, new Relationship("vela_account_contact_billing"), new EntityReferenceCollection { new EntityReference("contact", contact) });
            RetrieveRequest request = new RetrieveRequest
            {
                Target = new EntityReference("account", account),
                ColumnSet = new ColumnSet(true),
                RelatedEntitiesQuery = new RelationshipQueryCollection
                {
                    [new Relationship("vela_account_contact_billing")] = new QueryExpression("contact") { ColumnSet = new ColumnSet(true) },
                },
            };

            Entity retrieved = ((RetrieveResponse)svc.Execute(request)).Entity;

            Assert.Single(retrieved.RelatedEntities[new Relationship("vela_account_contact_billing")].Entities);
        }

        [Fact]
        public void Two_relationships_cannot_share_a_schema_name()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid", schemaName: "contact_customer_accounts");

            Assert.Throws<InvalidOperationException>(() =>
                svc.WithReferentialRelationship("account", "lead", "parentaccountid", schemaName: "Contact_Customer_Accounts"));
        }

        [Fact]
        public void A_schema_name_cannot_be_both_a_1_to_n_and_an_n_to_n_relationship()
        {
            InMemoryOrganizationService oneToManyFirst = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid", schemaName: "account_contacts");
            InMemoryOrganizationService manyToManyFirst = new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

            Assert.Throws<InvalidOperationException>(() =>
                oneToManyFirst.WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid"));
            Assert.Throws<InvalidOperationException>(() =>
                manyToManyFirst.WithReferentialRelationship("account", "contact", "parentcustomerid", schemaName: "account_contacts"));
        }

        [Fact]
        public void A_relationship_declared_without_a_schema_name_keeps_the_synthesized_one()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithRelationship("account", "contact", "parentcustomerid", new CascadeConfiguration { Delete = CascadeType.RemoveLink })
                // Two unnamed relationships between one pair may still share the synthesized name.
                .WithRelationship("account", "contact", "vela_billingaccountid", new CascadeConfiguration { Delete = CascadeType.RemoveLink })
                .Seed(new Entity("account", account), new Entity("contact", contact));

            svc.Associate("account", account, new Relationship("account_contact"), new EntityReferenceCollection { new EntityReference("contact", contact) });

            Assert.Equal(account, svc.Retrieve("contact", contact, new ColumnSet("parentcustomerid")).GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        [Fact]
        public void The_pipeline_forwards_the_schema_name()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithParentalRelationship("account", "contact", "parentcustomerid", schemaName: "contact_customer_accounts")
                .Seed(new Entity("account", account), new Entity("contact", contact));

            pipeline.Service.Associate("account", account, new Relationship("contact_customer_accounts"), new EntityReferenceCollection { new EntityReference("contact", contact) });

            Assert.Equal(account, pipeline.Service.Retrieve("contact", contact, new ColumnSet("parentcustomerid")).GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }
    }
}
