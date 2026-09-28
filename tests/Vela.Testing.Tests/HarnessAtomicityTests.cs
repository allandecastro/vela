using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // #303 Tier-1: bulk and deep-insert writes through the store are one transaction - a mid-way fault rolls the whole
    // batch back, so no partial rows survive (matching Dataverse's all-or-nothing semantics).
    public sealed class HarnessAtomicityTests
    {
        [Fact]
        public void CreateMultiple_rolls_back_the_whole_batch_on_a_mid_batch_fault()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid existing = crm.Create(new Entity("account") { ["name"] = "pre" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new Entity("account") { ["name"] = "first" });
            targets.Entities.Add(new Entity("account", existing) { ["name"] = "dup" }); // id already exists -> faults

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Execute(new CreateMultipleRequest { Targets = targets }));

            // The whole batch rolled back: only the pre-existing record remains ("first" was not persisted).
            Assert.Single(crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet("name") }).Entities);
        }

        [Fact]
        public void UpdateMultiple_rolls_back_the_whole_batch_on_a_mid_batch_fault()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = crm.Create(new Entity("account") { ["name"] = "original" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new Entity("account", id) { ["name"] = "changed" });
            targets.Entities.Add(new Entity("account", Guid.NewGuid()) { ["name"] = "missing" }); // no such record -> faults

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Execute(new UpdateMultipleRequest { Targets = targets }));

            // The first update rolled back with the batch: the record keeps its original name.
            Assert.Equal("original", crm.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void A_deep_insert_rolls_back_the_parent_when_a_child_fails()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            // No 1:N relationship is declared, so creating the child fails while resolving the relationship.
            Entity parent = new Entity("account") { ["name"] = "parent" };
            EntityCollection children = new EntityCollection();
            children.Entities.Add(new Entity("contact") { ["fullname"] = "child" });
            parent.RelatedEntities[new Relationship("account_contacts")] = children;

            Assert.Throws<InvalidOperationException>(() => crm.Create(parent));

            // The parent rolled back with the failed deep insert: nothing persisted.
            Assert.Empty(crm.RetrieveMultiple(new QueryExpression("account")).Entities);
            Assert.Empty(crm.RetrieveMultiple(new QueryExpression("contact")).Entities);
        }
    }
}
