using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Field-level security: a field-secured column is hidden on read and rejected on write unless a field permission
    // (to the user or a team) grants it — independent of record access.
    public sealed class FieldSecurityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000a01");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000a02");
        private static readonly Guid SalesTeam = new Guid("7ea11000-0000-0000-0000-000000000a03");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-000000000ab1");

        private static Entity Account(Guid id) => new Entity("account", id)
        {
            ["name"] = "Zava",
            ["creditlimit"] = new Money(50000m),
            ["ownerid"] = new EntityReference("systemuser", Bob),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        // Everyone can read/write account records at Organization level; creditlimit is the secured column.
        private static SecurityConfiguration Base(SecurityConfiguration s) => s
            .BusinessUnit(SalesBu)
            .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write))
            .User(Alice, SalesBu, "editor")
            .User(Bob, SalesBu, "editor")
            .SecureColumn("account", "creditlimit");

        [Fact]
        public void A_secured_column_is_hidden_without_field_read_permission()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => Base(s)) // Alice has record access but no field permission on creditlimit
                .Seed(Account(id));

            Entity read = svc.Retrieve("account", id, new ColumnSet(true));
            Assert.Equal("Zava", read.GetAttributeValue<string>("name")); // other columns still there
            Assert.False(read.Contains("creditlimit"));                    // secured column masked out
        }

        [Fact]
        public void A_secured_column_is_visible_with_field_read_permission()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Bob)
                .WithSecurity(s => Base(s).FieldPermission(Bob, "account", "creditlimit", read: true))
                .Seed(Account(id));

            Entity read = svc.Retrieve("account", id, new ColumnSet(true));
            Assert.Equal(50000m, read.GetAttributeValue<Money>("creditlimit").Value);
        }

        [Fact]
        public void Masking_does_not_change_the_stored_value()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => Base(s).FieldPermission(Bob, "account", "creditlimit", read: true))
                .Seed(Account(id));

            Assert.False(svc.Retrieve("account", id, new ColumnSet(true)).Contains("creditlimit")); // Alice: hidden

            svc.WithCurrentUser(Bob);
            Assert.Equal(50000m, svc.Retrieve("account", id, new ColumnSet(true)).GetAttributeValue<Money>("creditlimit").Value); // Bob: intact
        }

        [Fact]
        public void Writing_a_secured_column_is_rejected_without_field_update_permission()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => Base(s)) // Alice can write the record, but not the secured column
                .Seed(Account(id));

            svc.Update(new Entity("account", id) { ["name"] = "Renamed" }); // non-secured column: fine

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Update(new Entity("account", id) { ["creditlimit"] = new Money(1m) }));
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Create(new Entity("account") { ["creditlimit"] = new Money(1m) }));
        }

        [Fact]
        public void Writing_a_secured_column_succeeds_with_field_update_permission()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Bob)
                .WithSecurity(s => Base(s).FieldPermission(Bob, "account", "creditlimit", read: true, update: true))
                .Seed(Account(id));

            svc.Update(new Entity("account", id) { ["creditlimit"] = new Money(75000m) });

            Assert.Equal(75000m, svc.Retrieve("account", id, new ColumnSet(true)).GetAttributeValue<Money>("creditlimit").Value);
        }

        [Fact]
        public void Field_permission_granted_to_a_team_reaches_its_members()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => Base(s)
                    .Team(SalesTeam, SalesBu)
                    .Member(Alice, SalesTeam)
                    .FieldPermission(SalesTeam, "account", "creditlimit", read: true))
                .Seed(Account(id));

            Assert.Equal(50000m, svc.Retrieve("account", id, new ColumnSet(true)).GetAttributeValue<Money>("creditlimit").Value);
        }

        [Fact]
        public void Retrieve_multiple_masks_secured_columns()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => Base(s))
                .Seed(Account(id));

            Entity row = Assert.Single(svc.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) }).Entities);
            Assert.True(row.Contains("name"));
            Assert.False(row.Contains("creditlimit"));
        }
    }
}
