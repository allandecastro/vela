using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class RelationshipIncludeTests
    {
        private static InMemoryOrganizationService NewService() =>
            new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid");

        [Fact]
        public void RetrieveWithRelated_hydrates_a_1N_child_collection()
        {
            InMemoryOrganizationService service = NewService();
            Entity account = new Entity("account");
            account["name"] = "Contoso";
            Guid accountId = service.Create(account);
            service.Create(new Entity("contact") { ["firstname"] = "Ada", ["parentcustomerid"] = new EntityReference("account", accountId) });
            service.Create(new Entity("contact") { ["firstname"] = "Grace", ["parentcustomerid"] = new EntityReference("account", accountId) });

            DeepAccount got = service.RetrieveWithRelated<DeepAccount>(
                new EntityReference("account", accountId),
                q => q.Include(a => a.Contacts),
                a => a.Name);

            Assert.Equal("Contoso", got.Name);
            Assert.Equal(2, got.Contacts.Count);
            Assert.Contains(got.Contacts, c => c.FirstName == "Ada");
            Assert.Contains(got.Contacts, c => c.FirstName == "Grace");
        }

        [Fact]
        public void RetrieveWithRelated_hydrates_an_N1_parent()
        {
            InMemoryOrganizationService service = NewService();
            Guid accountId = service.Create(new Entity("account") { ["name"] = "Contoso" });
            Guid contactId = service.Create(new Entity("contact")
            {
                ["firstname"] = "Ada",
                ["parentcustomerid"] = new EntityReference("account", accountId),
            });

            DeepContact got = service.RetrieveWithRelated<DeepContact>(
                new EntityReference("contact", contactId),
                q => q.Include(c => c.ParentCustomer));

            Assert.NotNull(got.ParentCustomer);
            Assert.Equal(accountId, got.ParentCustomer!.Id);
            Assert.Equal("Contoso", got.ParentCustomer.Name);
        }

        [Fact]
        public void RetrieveWithRelated_leaves_an_empty_collection_when_there_are_no_children()
        {
            InMemoryOrganizationService service = NewService();
            Guid accountId = service.Create(new Entity("account") { ["name"] = "Lonely" });

            DeepAccount got = service.RetrieveWithRelated<DeepAccount>(
                new EntityReference("account", accountId),
                q => q.Include(a => a.Contacts));

            Assert.Empty(got.Contacts);
        }

        [Fact]
        public void Include_rejects_a_non_relationship_property()
        {
            InMemoryOrganizationService service = NewService();
            Guid accountId = service.Create(new Entity("account") { ["name"] = "Contoso" });

            // Name is a column, not a relationship nav.
            Assert.Throws<ArgumentException>(() => service.RetrieveWithRelated<DeepAccount>(
                new EntityReference("account", accountId),
                q => q.Include(a => a.Name)));
        }
    }
}
