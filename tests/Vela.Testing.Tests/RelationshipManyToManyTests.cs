using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class RelationshipManyToManyTests
    {
        private static InMemoryOrganizationService NewService() =>
            new InMemoryOrganizationService()
                .WithManyToMany("account_contacts_nn", "accountcontact", "account", "accountid", "contact", "contactid");

        [Fact]
        public void AssociateRelated_associates_existing_records_and_Include_reads_them_back()
        {
            InMemoryOrganizationService service = NewService();
            Guid accountId = service.Create(new Entity("account") { ["name"] = "Contoso" });
            Guid ada = service.Create(new Entity("contact") { ["firstname"] = "Ada" });
            Guid grace = service.Create(new Entity("contact") { ["firstname"] = "Grace" });

            DeepAccount account = new DeepAccount { Id = accountId };
            account.RelatedContacts.Add(new DeepContact { Id = ada });
            account.RelatedContacts.Add(new DeepContact { Id = grace });
            service.AssociateRelated(account);

            DeepAccount got = service.RetrieveWithRelated<DeepAccount>(
                new EntityReference("account", accountId),
                q => q.Include(a => a.RelatedContacts));

            Assert.Equal(2, got.RelatedContacts.Count);
            Assert.Contains(got.RelatedContacts, c => c.Id == ada);
            Assert.Contains(got.RelatedContacts, c => c.Id == grace);
        }

        [Fact]
        public void AssociateRelated_requires_an_id_on_the_entity()
        {
            InMemoryOrganizationService service = NewService();
            DeepAccount account = new DeepAccount();
            account.RelatedContacts.Add(new DeepContact { Id = Guid.NewGuid() });

            Assert.Throws<ArgumentException>(() => service.AssociateRelated(account));
        }

        [Fact]
        public void CreateWithRelated_does_not_create_NN_members_as_children()
        {
            InMemoryOrganizationService service = NewService();
            Guid ada = service.Create(new Entity("contact") { ["firstname"] = "Ada" });

            DeepAccount account = new DeepAccount { Name = "Contoso" };
            account.RelatedContacts.Add(new DeepContact { Id = ada });   // existing N:N member, must not be created
            service.CreateWithRelated(account);

            // Only the original contact exists - the N:N nav was not deep-inserted as a child.
            int contactCount = service.RetrieveMultiple(new QueryExpression("contact") { ColumnSet = new ColumnSet(false) }).Entities.Count;
            Assert.Equal(1, contactCount);
        }
    }
}
