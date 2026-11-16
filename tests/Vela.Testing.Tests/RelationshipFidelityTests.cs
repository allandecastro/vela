using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: relationship behaviours the existing Relationship*/Include/ManyToMany tests do not already cover —
    // a multi-level deep insert (grandchildren), Include honouring the child query's filter/order/top, and a
    // self-referential N:N association.
    public sealed class RelationshipFidelityTests
    {
        private static EntityCollection Collection(string entityName, params Entity[] entities)
        {
            EntityCollection collection = new EntityCollection { EntityName = entityName };
            collection.Entities.AddRange(entities);
            return collection;
        }

        // --- Multi-level deep insert: parent -> child -> grandchild --------------------------------------

        [Fact]
        public void Deep_insert_creates_grandchildren_linked_up_the_hierarchy()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")   // schema: account_contact
                .WithParentalRelationship("contact", "task", "regardingobjectid");     // schema: contact_task

            Entity grandchild = new Entity("task") { ["subject"] = "Follow up" };
            Entity child = new Entity("contact") { ["firstname"] = "Ada" };
            child.RelatedEntities[new Relationship("contact_task")] = Collection("task", grandchild);
            Entity parent = new Entity("account") { ["name"] = "Contoso" };
            parent.RelatedEntities[new Relationship("account_contact")] = Collection("contact", child);

            Guid accountId = svc.Create(parent);

            Entity createdContact = Assert.Single(svc.RetrieveMultiple(new QueryExpression("contact") { ColumnSet = new ColumnSet(true) }).Entities);
            Assert.Equal(accountId, createdContact.GetAttributeValue<EntityReference>("parentcustomerid").Id);

            Entity createdTask = Assert.Single(svc.RetrieveMultiple(new QueryExpression("task") { ColumnSet = new ColumnSet(true) }).Entities);
            // The grandchild is linked to the child that was itself created by the deep insert.
            Assert.Equal(createdContact.Id, createdTask.GetAttributeValue<EntityReference>("regardingobjectid").Id);
            Assert.Equal("Follow up", createdTask.GetAttributeValue<string>("subject"));
        }

        // --- Include honours the child query's filter, order and top ------------------------------------

        [Fact]
        public void Include_honours_the_child_querys_filter_order_and_top()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid");

            Guid accountId = svc.Create(new Entity("account") { ["name"] = "Contoso" });
            EntityReference parentRef = new EntityReference("account", accountId);
            svc.Create(new Entity("contact") { ["firstname"] = "Cara", ["parentcustomerid"] = parentRef });
            svc.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = parentRef });
            svc.Create(new Entity("contact") { ["firstname"] = "Bob", ["parentcustomerid"] = parentRef });

            QueryExpression childQuery = new QueryExpression("contact") { ColumnSet = new ColumnSet(true), TopCount = 1 };
            childQuery.Criteria.AddCondition("firstname", ConditionOperator.NotEqual, "Bob"); // filter out Bob
            childQuery.Orders.Add(new OrderExpression("firstname", OrderType.Ascending));     // order the survivors

            RetrieveRequest request = new RetrieveRequest
            {
                Target = parentRef,
                ColumnSet = new ColumnSet("name"),
                RelatedEntitiesQuery = new RelationshipQueryCollection
                {
                    { new Relationship("account_contact"), childQuery },
                },
            };

            Entity account = ((RetrieveResponse)svc.Execute(request)).Entity;
            EntityCollection children = account.RelatedEntities[new Relationship("account_contact")];

            // Bob filtered out; Ada/Cara ordered ascending; top 1 -> only Ada survives.
            Entity only = Assert.Single(children.Entities);
            Assert.Equal("Ada", only.GetAttributeValue<string>("firstname"));
        }

        // --- Self-referential N:N -----------------------------------------------------------------------

        [Fact]
        public void Self_referential_many_to_many_associates_and_disassociates()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithManyToMany("contact_referrals", "contactreferral", "contact", "contactidone", "contact", "contactidtwo");

            Guid a = svc.Create(new Entity("contact") { ["firstname"] = "Ada" });
            Guid b = svc.Create(new Entity("contact") { ["firstname"] = "Bob" });

            svc.Associate("contact", a, new Relationship("contact_referrals") { PrimaryEntityRole = EntityRole.Referencing },
                new EntityReferenceCollection { new EntityReference("contact", b) });

            // A Referencing caller goes in the first column of a self-referential relationship.
            Entity intersect = Assert.Single(svc.GetAll("contactreferral"));
            Assert.Equal(a, intersect.GetAttributeValue<EntityReference>("contactidone").Id);
            Assert.Equal(b, intersect.GetAttributeValue<EntityReference>("contactidtwo").Id);

            svc.Disassociate("contact", a, new Relationship("contact_referrals") { PrimaryEntityRole = EntityRole.Referencing },
                new EntityReferenceCollection { new EntityReference("contact", b) });
            Assert.Empty(svc.GetAll("contactreferral"));
        }
    }
}
