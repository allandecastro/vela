using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class AlternateKeyTests
    {
        private static EntityReference AccountNumberKey(string accountNumber) =>
            new EntityReference("account", new KeyAttributeCollection { { "accountnumber", accountNumber } });

        [Fact]
        public void RetrieveByKey_resolves_a_record_by_its_alternate_key()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService();
            Entity account = new Entity("account");
            account["accountnumber"] = "A-001";
            account["name"] = "Contoso";
            Guid id = service.Create(account);

            DeepAccount got = service.RetrieveByKey<DeepAccount>(AccountNumberKey("A-001"), a => a.Name);

            Assert.Equal(id, got.Id);
            Assert.Equal("Contoso", got.Name);
        }

        [Fact]
        public void RetrieveByKey_throws_not_found_when_no_record_matches_the_key()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService();

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => service.RetrieveByKey<DeepAccount>(AccountNumberKey("missing"), a => a.Name));
            Assert.Contains("alternate key", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RetrieveByKey_rejects_a_relationship_nav_in_the_column_list()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService();
            Entity account = new Entity("account");
            account["accountnumber"] = "A-001";
            service.Create(account);

            // Contacts is a relationship, not a column - the resolver guardrail must reject it.
            Assert.Throws<ArgumentException>(
                () => service.RetrieveByKey<DeepAccount>(AccountNumberKey("A-001"), a => a.Contacts));
        }

        [Fact]
        public void UpsertByKey_creates_a_new_record_when_the_key_does_not_match()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService();

            (bool created, EntityReference reference) = service.UpsertByKey(new DeepAccount { Name = "Contoso" }, AccountNumberKey("A-001"));

            Assert.True(created);
            Entity stored = service.Retrieve("account", reference.Id, new ColumnSet(true));
            Assert.Equal("Contoso", stored.GetAttributeValue<string>("name"));
            // The alternate-key column is seeded onto the new record.
            Assert.Equal("A-001", stored.GetAttributeValue<string>("accountnumber"));
        }

        [Fact]
        public void RetrieveByKey_and_UpsertByKey_work_with_a_composite_key()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService().WithAlternateKey("account", "code", "region");
            Guid id = service.Create(new Entity("account") { ["code"] = "C1", ["region"] = "EU", ["name"] = "Old" });
            EntityReference key = new EntityReference("account", new KeyAttributeCollection { { "code", "C1" }, { "region", "EU" } });

            DeepAccount got = service.RetrieveByKey<DeepAccount>(key, a => a.Name);
            Assert.Equal(id, got.Id);
            Assert.Equal("Old", got.Name);

            (bool created, EntityReference reference) = service.UpsertByKey(new DeepAccount { Name = "New" }, key);
            Assert.False(created);
            Assert.Equal(id, reference.Id);
            Assert.Equal("New", service.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void UpsertByKey_updates_the_existing_record_when_the_key_matches()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService();
            Entity existing = new Entity("account");
            existing["accountnumber"] = "A-001";
            existing["name"] = "Old name";
            Guid id = service.Create(existing);

            (bool created, EntityReference reference) = service.UpsertByKey(new DeepAccount { Name = "New name" }, AccountNumberKey("A-001"));

            Assert.False(created);
            Assert.Equal(id, reference.Id);
            Assert.Equal("New name", service.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }
    }
}
