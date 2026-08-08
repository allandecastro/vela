using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Record-level sharing (PoA): GrantAccess/ModifyAccess/RevokeAccess, RetrievePrincipalAccess and
    // RetrieveSharedPrincipalsAndAccess, unioned with role access. Shared access only ever adds access.
    public sealed class SharingTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000201");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000202");
        private static readonly Guid Sharer = new Guid("54a4e000-0000-0000-0000-000000000203");
        private static readonly Guid SalesTeam = new Guid("7ea11000-0000-0000-0000-000000000204");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000002b1");

        private static EntityReference Account(Guid id) => new EntityReference("account", id);
        private static EntityReference User(Guid id) => new EntityReference("systemuser", id);

        private static Entity AccountOwnedBy(Guid id, Guid owner) =>
            new Entity("account", id)
            {
                ["ownerid"] = new EntityReference("systemuser", owner),
                ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
            };

        // Alice holds no role on account, so any access she has comes purely from sharing.
        private static InMemoryOrganizationService NoRoleForAlice(Guid record, Guid owner) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu))
                .Seed(AccountOwnedBy(record, owner));

        [Fact]
        public void A_shared_record_is_readable_by_a_user_without_the_role()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(record, Bob)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess);

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Sharing_is_per_record_not_global()
        {
            Guid shared = Guid.NewGuid();
            Guid other = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(shared, Bob)
                .Seed(AccountOwnedBy(other, Bob))
                .WithSharedRecord(Account(shared), User(Alice), AccessRights.ReadAccess);

            Assert.NotNull(svc.Retrieve("account", shared, new ColumnSet(true)));
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", other, new ColumnSet(true)));
        }

        [Fact]
        public void A_shared_record_appears_in_retrieve_multiple()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(record, Bob)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess);

            EntityCollection visible = svc.RetrieveMultiple(
                new QueryExpression("account") { ColumnSet = new ColumnSet(true) });

            Assert.Equal(record, Assert.Single(visible.Entities).Id);
        }

        [Fact]
        public void Shared_write_allows_update_without_the_write_role()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(record, Bob)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess | AccessRights.WriteAccess);

            svc.Update(new Entity("account", record) { ["name"] = "edited" });

            Assert.Equal("edited", svc.Retrieve("account", record, new ColumnSet(true)).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Sharing_to_a_team_reaches_its_members()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .User(Alice, SalesBu)
                    .Team(SalesTeam, SalesBu)
                    .Member(Alice, SalesTeam))
                .Seed(AccountOwnedBy(record, Bob))
                .WithSharedRecord(Account(record), new EntityReference("team", SalesTeam), AccessRights.ReadAccess);

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        // A current user that may share the record (holds the Share privilege), so it can drive
        // GrantAccess/ModifyAccess/RevokeAccess. Alice is the sharee whose resulting access we assert.
        private static InMemoryOrganizationService SharerCanShare(Guid record) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Sharer)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("sharer", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Share))
                    .User(Sharer, SalesBu, "sharer")
                    .User(Alice, SalesBu))
                .Seed(AccountOwnedBy(record, Sharer));

        private static AccessRights AliceAccess(InMemoryOrganizationService svc, Guid record) =>
            ((RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest { Target = Account(record), Principal = User(Alice) })).AccessRights;

        [Fact]
        public void RevokeAccess_removes_a_grant()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = SharerCanShare(record)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess);
            Assert.True(AliceAccess(svc, record).HasFlag(AccessRights.ReadAccess));

            svc.Execute(new RevokeAccessRequest { Target = Account(record), Revokee = User(Alice) });

            Assert.Equal(AccessRights.None, AliceAccess(svc, record)); // Alice has no role either, so nothing remains
        }

        [Fact]
        public void ModifyAccess_replaces_the_rights()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = SharerCanShare(record)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess | AccessRights.WriteAccess);

            // Narrow Alice's share down to read-only.
            svc.Execute(new ModifyAccessRequest
            {
                Target = Account(record),
                PrincipalAccess = new PrincipalAccess { Principal = User(Alice), AccessMask = AccessRights.ReadAccess },
            });

            AccessRights rights = AliceAccess(svc, record);
            Assert.True(rights.HasFlag(AccessRights.ReadAccess));    // still readable
            Assert.False(rights.HasFlag(AccessRights.WriteAccess));  // no longer writable
        }

        [Fact]
        public void GrantAccess_requires_the_share_privilege_and_then_shares()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Sharer)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization))
                    .User(Sharer, SalesBu, "reader")   // can read, cannot share
                    .User(Alice, SalesBu))
                .Seed(AccountOwnedBy(record, Sharer));

            GrantAccessRequest grant = new GrantAccessRequest
            {
                Target = Account(record),
                PrincipalAccess = new PrincipalAccess { Principal = User(Alice), AccessMask = AccessRights.ReadAccess },
            };

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(grant)); // lacks Share

            // Grant the Sharer the Share privilege and retry: the grant now goes through and Alice can read.
            svc.WithSecurity(s => s
                .BusinessUnit(SalesBu)
                .Role("sharer", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Share))
                .User(Sharer, SalesBu, "sharer")
                .User(Alice, SalesBu));
            svc.Execute(grant);

            svc.WithCurrentUser(Alice);
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void RetrievePrincipalAccess_unions_role_and_shared_rights()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization))
                    .User(Alice, SalesBu, "reader"))
                .Seed(AccountOwnedBy(record, Bob))
                .WithSharedRecord(Account(record), User(Alice), AccessRights.WriteAccess); // shared write on top of role read

            RetrievePrincipalAccessResponse response = (RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest { Target = Account(record), Principal = User(Alice) });

            Assert.True(response.AccessRights.HasFlag(AccessRights.ReadAccess));   // from the role
            Assert.True(response.AccessRights.HasFlag(AccessRights.WriteAccess));  // from the share
            Assert.False(response.AccessRights.HasFlag(AccessRights.DeleteAccess));
        }

        [Fact]
        public void RetrieveSharedPrincipalsAndAccess_lists_every_share()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(record, Sharer)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess)
                .WithSharedRecord(Account(record), User(Bob), AccessRights.ReadAccess | AccessRights.WriteAccess);

            RetrieveSharedPrincipalsAndAccessResponse response = (RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                new RetrieveSharedPrincipalsAndAccessRequest { Target = Account(record) });

            Assert.Equal(2, response.PrincipalAccesses.Length);
            Assert.Contains(response.PrincipalAccesses, p => p.Principal.Id == Alice && p.AccessMask == AccessRights.ReadAccess);
            Assert.Contains(response.PrincipalAccesses, p => p.Principal.Id == Bob && p.AccessMask == (AccessRights.ReadAccess | AccessRights.WriteAccess));
        }

        [Fact]
        public void GrantAccess_accumulates_rights_across_grants()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(record, Bob)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.WriteAccess); // second grant adds, not replaces

            AccessRights rights = AliceAccess(svc, record);
            Assert.True(rights.HasFlag(AccessRights.ReadAccess));
            Assert.True(rights.HasFlag(AccessRights.WriteAccess));
        }

        [Fact]
        public void ModifyAccess_to_none_revokes_the_share()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = SharerCanShare(record)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess);

            svc.Execute(new ModifyAccessRequest
            {
                Target = Account(record),
                PrincipalAccess = new PrincipalAccess { Principal = User(Alice), AccessMask = AccessRights.None },
            });

            Assert.Equal(AccessRights.None, AliceAccess(svc, record));
        }

        [Theory]
        [InlineData(AccessRights.ReadAccess)]
        [InlineData(AccessRights.WriteAccess)]
        [InlineData(AccessRights.DeleteAccess)]
        [InlineData(AccessRights.AppendAccess)]
        [InlineData(AccessRights.AppendToAccess)]
        [InlineData(AccessRights.AssignAccess)]
        [InlineData(AccessRights.ShareAccess)]
        public void Each_shared_right_maps_exactly_through_retrieve_principal_access(AccessRights right)
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = NoRoleForAlice(record, Bob)
                .WithSharedRecord(Account(record), User(Alice), right);

            // Alice holds no role, so her effective mask must equal exactly the single shared right — this pins the
            // Privilege <-> AccessRights mapping and the per-principal rights union.
            Assert.Equal(right, AliceAccess(svc, record));
        }

        [Fact]
        public void RetrievePrincipalAccess_on_a_missing_record_reports_no_access()
        {
            Guid missing = Guid.NewGuid();
            InMemoryOrganizationService withSecurity = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu));
            InMemoryOrganizationService withoutSecurity = new InMemoryOrganizationService();

            // A record that does not exist confers no access — the same answer whether or not security is on.
            foreach (InMemoryOrganizationService svc in new[] { withSecurity, withoutSecurity })
            {
                RetrievePrincipalAccessResponse response = (RetrievePrincipalAccessResponse)svc.Execute(
                    new RetrievePrincipalAccessRequest { Target = Account(missing), Principal = User(Alice) });
                Assert.Equal(AccessRights.None, response.AccessRights);
            }
        }

        [Fact]
        public void RetrievePrincipalAccess_without_security_reports_full_access()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(AccountOwnedBy(record, Bob));

            RetrievePrincipalAccessResponse response = (RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest { Target = Account(record), Principal = User(Alice) });

            Assert.True(response.AccessRights.HasFlag(AccessRights.ReadAccess));
            Assert.True(response.AccessRights.HasFlag(AccessRights.WriteAccess));
            Assert.True(response.AccessRights.HasFlag(AccessRights.ShareAccess));
        }
    }
}
