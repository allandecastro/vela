using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class ExecuteAndContextTests
    {
        [Fact]
        public void CreateMultiple_ThenUpdateMultiple_ViaExecute()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new Entity("account") { ["name"] = "A" });
            targets.Entities.Add(new Entity("account") { ["name"] = "B" });

            CreateMultipleResponse created = (CreateMultipleResponse)crm.Execute(new CreateMultipleRequest { Targets = targets });
            Assert.Equal(2, created.Ids.Length);

            Guid first = created.Ids[0];
            EntityCollection updates = new EntityCollection { EntityName = "account" };
            updates.Entities.Add(new Entity("account") { Id = first, ["name"] = "A-renamed" });
            crm.Execute(new UpdateMultipleRequest { Targets = updates });

            Entity stored = crm.Retrieve("account", first, new ColumnSet(true));
            Assert.Equal("A-renamed", stored.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Execute_UnsupportedMessage_ThrowsHelpfulMessage()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => crm.Execute(new OrganizationRequest("vela_SomethingElse")));
            Assert.Contains("CreateMultiple", ex.Message);
        }

        [Fact]
        public void FakeContext_ImplementsFullInterfaceChain_2Through7()
        {
            FakePluginExecutionContext context = new FakePluginExecutionContext
            {
                IsApplicationUser = true,
                EnvironmentId = "env-123",
                TenantId = Guid.NewGuid(),
                AuthenticatedUserId = Guid.NewGuid(),
                InitiatingUserAgent = "agent",
                IsPortalsClientCall = true
            };

            IPluginExecutionContext7 c = context; // chains 2..7 + base

            Assert.True(c.IsApplicationUser);
            Assert.Equal("env-123", c.EnvironmentId);
            Assert.NotEqual(Guid.Empty, c.TenantId);
            Assert.NotEqual(Guid.Empty, c.AuthenticatedUserId);
            Assert.Equal("agent", c.InitiatingUserAgent);
            Assert.True(c.IsPortalsClientCall);
            Assert.NotNull(c.PreEntityImagesCollection);
        }
    }
}
