using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: alternate-key behaviours the existing AlternateKeyTests do not already cover — the duplicate-key
    // fault (with the platform ErrorCode), and update/delete/upsert addressed by alternate key through Execute.
    public sealed class AlternateKeyFidelityTests
    {
        // 0x80060892 DuplicateRecordKeyValueExists — the platform's alternate-key duplicate fault.
        private const int DuplicateRecordKeyValueExists = unchecked((int)0x80060892);

        private static InMemoryOrganizationService AccountNumberService() =>
            new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");

        // --- Duplicate key on Create --------------------------------------------------------------------

        [Fact]
        public void Creating_a_record_with_a_duplicate_key_throws_the_platform_fault()
        {
            InMemoryOrganizationService svc = AccountNumberService();
            svc.Create(new Entity("account") { ["accountnumber"] = "A-001" });

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Create(new Entity("account") { ["accountnumber"] = "A-001" }));

            Assert.Equal(DuplicateRecordKeyValueExists, ex.Detail.ErrorCode);
        }

        // --- Duplicate key on Update --------------------------------------------------------------------

        [Fact]
        public void Updating_a_record_into_a_duplicate_key_throws_and_leaves_the_store_intact()
        {
            InMemoryOrganizationService svc = AccountNumberService();
            svc.Create(new Entity("account") { ["accountnumber"] = "A-001" });
            Guid second = svc.Create(new Entity("account") { ["accountnumber"] = "A-002", ["name"] = "Second" });

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Update(new Entity("account", second) { ["accountnumber"] = "A-001" }));
            Assert.Equal(DuplicateRecordKeyValueExists, ex.Detail.ErrorCode);

            // The rejected update did not partially apply: the second record keeps its own key.
            Assert.Equal("A-002", svc.Retrieve("account", second, new ColumnSet(true)).GetAttributeValue<string>("accountnumber"));
        }

        // --- Update by alternate key --------------------------------------------------------------------

        [Fact]
        public void Update_addressed_by_alternate_key_resolves_and_updates_the_record()
        {
            InMemoryOrganizationService svc = AccountNumberService();
            Guid id = svc.Create(new Entity("account") { ["accountnumber"] = "A-001", ["name"] = "Old" });

            Entity target = new Entity("account") { ["name"] = "New" };
            target.KeyAttributes.Add("accountnumber", "A-001");
            svc.Execute(new UpdateRequest { Target = target });

            Assert.Equal("New", svc.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        // --- Delete by alternate key --------------------------------------------------------------------

        [Fact]
        public void Delete_addressed_by_alternate_key_resolves_and_deletes_the_record()
        {
            InMemoryOrganizationService svc = AccountNumberService();
            svc.Create(new Entity("account") { ["accountnumber"] = "A-001" });

            EntityReference key = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-001" } });
            svc.Execute(new DeleteRequest { Target = key });

            Assert.Empty(svc.RetrieveMultiple(new QueryExpression("account")).Entities);
        }

        [Fact]
        public void Deleting_by_a_key_that_matches_nothing_throws_not_found()
        {
            InMemoryOrganizationService svc = AccountNumberService();

            EntityReference key = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "missing" } });
            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Execute(new DeleteRequest { Target = key }));
            Assert.Contains("alternate key", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // --- Upsert by composite key via Execute: create then update ------------------------------------

        [Fact]
        public void Upsert_by_composite_key_via_execute_creates_then_updates()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().WithAlternateKey("account", "code", "region");

            Entity first = new Entity("account") { ["name"] = "First" };
            first.KeyAttributes.Add("code", "C1");
            first.KeyAttributes.Add("region", "EU");
            UpsertResponse created = (UpsertResponse)svc.Execute(new UpsertRequest { Target = first });

            Assert.True(created.RecordCreated); // first upsert inserts
            Guid id = created.Target.Id;
            Entity stored = svc.Retrieve("account", id, new ColumnSet(true));
            Assert.Equal("First", stored.GetAttributeValue<string>("name"));
            Assert.Equal("C1", stored.GetAttributeValue<string>("code"));   // key columns are seeded onto the new row
            Assert.Equal("EU", stored.GetAttributeValue<string>("region"));

            Entity second = new Entity("account") { ["name"] = "Second" };
            second.KeyAttributes.Add("code", "C1");
            second.KeyAttributes.Add("region", "EU");
            UpsertResponse updated = (UpsertResponse)svc.Execute(new UpsertRequest { Target = second });

            Assert.False(updated.RecordCreated);        // same key -> updates the existing row
            Assert.Equal(id, updated.Target.Id);
            Assert.Equal("Second", svc.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }
    }
}
