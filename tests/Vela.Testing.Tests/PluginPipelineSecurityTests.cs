using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // The security model wired onto PluginPipeline: writes the pipeline drives and reads plugins issue through
    // Service are enforced against the current user, and a denied write rolls the operation back.
    public sealed class PluginPipelineSecurityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000101");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000102");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000001b1");

        private static Entity AccountOwnedBy(Guid id, Guid owner) =>
            new Entity("account", id)
            {
                ["ownerid"] = new EntityReference("systemuser", owner),
                ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
            };

        [Fact]
        public void Without_security_the_pipeline_allows_writes()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().WithCurrentUser(Alice);

            Guid id = pipeline.Service.Create(new Entity("account"));

            Assert.NotEqual(Guid.Empty, id);
            Assert.Single(pipeline.GetAll("account"));
        }

        [Fact]
        public void A_denied_write_throws_and_rolls_the_operation_back()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "reader")); // no Create privilege

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Create(new Entity("account")));
            Assert.Empty(pipeline.GetAll("account")); // rolled back — nothing persisted
        }

        [Fact]
        public void A_granted_write_succeeds_through_the_pipeline()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "author"));

            Guid id = pipeline.Service.Create(new Entity("account"));

            Assert.NotEqual(Guid.Empty, id);
            Assert.Single(pipeline.GetAll("account"));
        }

        [Fact]
        public void Reads_through_the_pipeline_service_are_filtered_by_read_access()
        {
            Guid bobRecord = Guid.NewGuid();
            Guid aliceRecord = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "author"))
                .Seed(AccountOwnedBy(bobRecord, Bob), AccountOwnedBy(aliceRecord, Alice));

            EntityCollection visible = pipeline.Service.RetrieveMultiple(
                new QueryExpression("account") { ColumnSet = new ColumnSet(true) });

            Assert.Equal(aliceRecord, Assert.Single(visible.Entities).Id);
        }
    }
}
