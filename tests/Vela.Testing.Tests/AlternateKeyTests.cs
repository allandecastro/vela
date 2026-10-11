using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class AlternateKeyTests
    {
        private static InMemoryOrganizationService KeyedService() => new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");

        private static EntityReference AccountNumberKey(string accountNumber) =>
            new EntityReference("account", new KeyAttributeCollection { { "accountnumber", accountNumber } });

        [Fact]
        public void RetrieveByKey_resolves_a_record_by_its_alternate_key()
        {
            InMemoryOrganizationService service = KeyedService();
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
            InMemoryOrganizationService service = KeyedService();

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => service.RetrieveByKey<DeepAccount>(AccountNumberKey("missing"), a => a.Name));
            Assert.Equal(unchecked((int)0x80060891), ex.Detail.ErrorCode);
        }

        // #451 (verified live): only a declared key addresses a record. Columns that are not a key, or a declared key
        // plus another column, fault with 0x80060889 instead of matching whatever row carries those values.
        [Fact]
        public void A_key_that_is_not_declared_is_rejected_even_when_a_row_matches()
        {
            InMemoryOrganizationService service = KeyedService();
            service.Create(new Entity("account") { ["accountnumber"] = "A-001", ["name"] = "Contoso" });

            FaultException<OrganizationServiceFault> byName = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => service.RetrieveByKey<DeepAccount>(new EntityReference("account", "name", "Contoso"), a => a.Name));
            Assert.Equal(unchecked((int)0x80060889), byName.Detail.ErrorCode);
            Assert.Contains("accountnumber", byName.Detail.Message, StringComparison.Ordinal);

            EntityReference extra = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-001" }, { "name", "Contoso" } });
            Assert.Equal(unchecked((int)0x80060889), Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => service.Execute(new DeleteRequest { Target = extra })).Detail.ErrorCode);

            // A table with no key at all rejects any key, and a lookup set by key is checked the same way.
            Assert.Equal(unchecked((int)0x80060889), Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => service.Create(new Entity("contact") { ["parentcustomerid"] = new EntityReference("account", "name", "Contoso") })).Detail.ErrorCode);
            Assert.Equal(unchecked((int)0x80060889), Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => service.Execute(new RetrieveRequest { Target = new EntityReference("contact", "lastname", "x"), ColumnSet = new ColumnSet(true) })).Detail.ErrorCode);
        }

        [Fact]
        public void RetrieveByKey_rejects_a_relationship_nav_in_the_column_list()
        {
            InMemoryOrganizationService service = KeyedService();
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
            InMemoryOrganizationService service = KeyedService();

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
            InMemoryOrganizationService service = KeyedService();
            Entity existing = new Entity("account");
            existing["accountnumber"] = "A-001";
            existing["name"] = "Old name";
            Guid id = service.Create(existing);

            (bool created, EntityReference reference) = service.UpsertByKey(new DeepAccount { Name = "New name" }, AccountNumberKey("A-001"));

            Assert.False(created);
            Assert.Equal(id, reference.Id);
            Assert.Equal("New name", service.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void UpsertByKey_is_keyed_by_the_given_key_only_and_leaves_the_entity_untouched()
        {
            InMemoryOrganizationService service = KeyedService();
            Guid existing = service.Create(new Entity("account") { ["accountnumber"] = "A-001", ["name"] = "Old" });

            // A stale key already on the entity must not be merged into the upsert key.
            DeepAccount entity = new DeepAccount { Name = "New" };
            entity.KeyAttributes["name"] = "Stale";

            (bool created, EntityReference reference) = service.UpsertByKey(entity, AccountNumberKey("A-001"));

            Assert.False(created);
            Assert.Equal(existing, reference.Id);
            Assert.Single(entity.KeyAttributes);
            Assert.Equal("Stale", entity.KeyAttributes["name"]);
        }

        [Fact]
        public void UpsertByKey_rejects_an_entity_with_an_id_and_a_key_without_attributes()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService();

            Assert.Throws<ArgumentException>(() => service.UpsertByKey(new DeepAccount { Id = Guid.NewGuid() }, AccountNumberKey("A-001")));
            Assert.Throws<ArgumentException>(() => service.UpsertByKey(new DeepAccount(), new EntityReference("account", Guid.NewGuid())));
        }
    }
}
