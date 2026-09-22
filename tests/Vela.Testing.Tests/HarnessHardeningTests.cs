using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity hardening: the fake now raises the platform faults for cases it previously accepted silently, and
    // resolves alternate-key targets on write like a real org (maturity findings HARN-001/002/008).
    public sealed class HarnessHardeningTests
    {
        [Fact]
        public void Delete_of_a_missing_record_throws_not_found()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Delete("account", Guid.NewGuid()));
            Assert.Contains("Does Not Exist", ex.Message);
        }

        [Fact]
        public void Create_with_a_colliding_supplied_id_throws_duplicate()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = Guid.NewGuid();
            crm.Create(new Entity("account") { Id = id, ["name"] = "First" });

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Create(new Entity("account") { Id = id, ["name"] = "Second" }));
        }

        [Fact]
        public void Update_by_alternate_key_resolves_the_target()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");
            Guid id = crm.Create(new Entity("account") { ["accountnumber"] = "ACC-1", ["name"] = "Old" });

            Entity target = new Entity("account") { KeyAttributes = { { "accountnumber", "ACC-1" } }, ["name"] = "New" };
            crm.Execute(new UpdateRequest { Target = target });

            Assert.Equal("New", crm.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Delete_by_alternate_key_resolves_the_target()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");
            Guid id = crm.Create(new Entity("account") { ["accountnumber"] = "ACC-1" });

            crm.Execute(new DeleteRequest { Target = new EntityReference("account", "accountnumber", "ACC-1") });

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => crm.Retrieve("account", id, new ColumnSet(true)));
        }
    }
}
