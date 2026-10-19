using System;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Targeted coverage for sharing behaviours mutation testing found under-asserted: the union (not XOR)
    // of overlapping grants when computing a principal's rights, and the two ModifyAccess edges (removing a
    // grant when narrowed to None, and creating a grant on a record that was never shared before).
    public sealed class SharingMutationTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000301");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000302");
        private static readonly Guid Sharer = new Guid("54a4e000-0000-0000-0000-000000000303");
        private static readonly Guid SalesTeam = new Guid("7ea11000-0000-0000-0000-000000000304");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000003b1");

        private static EntityReference Account(Guid id) => new EntityReference("account", id);
        private static EntityReference User(Guid id) => new EntityReference("systemuser", id);

        private static Entity AccountOwnedBy(Guid id, Guid owner) =>
            new Entity("account", id)
            {
                ["ownerid"] = new EntityReference("systemuser", owner),
                ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
            };

        private static AccessRights AliceAccess(InMemoryOrganizationService svc, Guid record) =>
            ((RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest { Target = Account(record), Principal = User(Alice) })).AccessRights;

        // A current user holding the Share privilege, so it can drive ModifyAccess. Alice is the sharee.
        private static InMemoryOrganizationService SharerCanShare(Guid record) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Sharer)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("sharer", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Share))
                    .User(Sharer, SalesBu, "sharer")
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "basic"))
                .Seed(AccountOwnedBy(record, Sharer));

        [Fact]
        public void Rights_are_the_union_of_overlapping_user_and_team_shares()
        {
            // Alice gets Read directly and Read+Write via her team on the SAME record. Her effective rights
            // are the union (Read+Write). An XOR of the two overlapping grants would cancel the shared Read bit.
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "basic")
                    .Team(SalesTeam, SalesBu)
                    .Member(Alice, SalesTeam))
                .Seed(AccountOwnedBy(record, Bob))
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess)
                .WithSharedRecord(Account(record), new EntityReference("team", SalesTeam), AccessRights.ReadAccess | AccessRights.WriteAccess);

            AccessRights rights = AliceAccess(svc, record);

            Assert.True(rights.HasFlag(AccessRights.ReadAccess));   // survives the overlap only under a union
            Assert.True(rights.HasFlag(AccessRights.WriteAccess));
        }

        [Fact]
        public void ModifyAccess_to_none_removes_the_share_entirely()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = SharerCanShare(record)
                .WithSharedRecord(Account(record), User(Alice), AccessRights.ReadAccess);

            svc.Execute(new ModifyAccessRequest
            {
                Target = Account(record),
                PrincipalAccess = new PrincipalAccess { Principal = User(Alice), AccessMask = AccessRights.None },
            });

            RetrieveSharedPrincipalsAndAccessResponse shares = (RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                new RetrieveSharedPrincipalsAndAccessRequest { Target = Account(record) });

            // Narrowing to None must revoke the grant outright, not leave a lingering empty share behind.
            Assert.DoesNotContain(shares.PrincipalAccesses, p => p.Principal.Id == Alice);
            Assert.Equal(AccessRights.None, AliceAccess(svc, record));
        }

        [Fact]
        public void ModifyAccess_creates_the_grant_on_a_record_that_was_never_shared()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = SharerCanShare(record); // no prior share for Alice

            svc.Execute(new ModifyAccessRequest
            {
                Target = Account(record),
                PrincipalAccess = new PrincipalAccess { Principal = User(Alice), AccessMask = AccessRights.ReadAccess },
            });

            Assert.True(AliceAccess(svc, record).HasFlag(AccessRights.ReadAccess));
        }
    }
}
