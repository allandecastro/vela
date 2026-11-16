using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: role/privilege depth, sharing (GrantAccess/ModifyAccess/RevokeAccess <-> RetrievePrincipalAccess),
    // the "you can't share rights you don't have" clamp, and depth-checked assignment. These add the parity/edge
    // cases the existing SecurityModelTests / AccessTeamTests do not already cover.
    public sealed class SecurityFidelityTests
    {
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-0000000009a1");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-0000000009b2");
        private static readonly Guid Carol = new Guid("ca401000-0000-0000-0000-0000000009c3");

        private static readonly Guid RootBu = new Guid("b0000000-0000-0000-0000-0000000009d0");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000009d1");
        private static readonly Guid SubSalesBu = new Guid("b0000000-0000-0000-0000-0000000009d2");
        private static readonly Guid OtherBu = new Guid("b0000000-0000-0000-0000-0000000009d9");

        private static Entity AccountOwnedBy(Guid recordId, Guid ownerUser, Guid businessUnit) =>
            new Entity("account", recordId)
            {
                ["ownerid"] = new EntityReference("systemuser", ownerUser),
                ["owningbusinessunit"] = new EntityReference("businessunit", businessUnit),
            };

        private static AccessRights PrincipalAccessFor(InMemoryOrganizationService svc, Guid record, Guid principal) =>
            ((RetrievePrincipalAccessResponse)svc.Execute(new RetrievePrincipalAccessRequest
            {
                Target = new EntityReference("account", record),
                Principal = new EntityReference("systemuser", principal),
            })).AccessRights;

        // --- Depth governs write: BusinessUnit level covers the caller's BU only -------------------------

        [Fact]
        public void Business_unit_level_write_covers_same_bu_but_not_another_bu()
        {
            Guid sameBu = Guid.NewGuid();
            Guid otherBu = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("bu-editor", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "bu-editor"))
                .Seed(AccountOwnedBy(sameBu, Bob, SalesBu), AccountOwnedBy(otherBu, Carol, OtherBu));

            svc.Update(new Entity("account", sameBu) { ["name"] = "ok" }); // same BU: allowed
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Update(new Entity("account", otherBu) { ["name"] = "no" })); // other BU: denied
        }

        // --- Depth governs write: ParentChild (deep) covers a descendant BU -----------------------------

        [Fact]
        public void Parent_child_level_write_covers_a_descendant_business_unit()
        {
            Guid descendant = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(RootBu)
                    .BusinessUnit(SalesBu, parent: RootBu)
                    .BusinessUnit(SubSalesBu, parent: SalesBu)
                    .Role("deep-editor", r => r.Grant("account", AccessLevel.ParentChild, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "deep-editor"))
                .Seed(AccountOwnedBy(descendant, Bob, SubSalesBu));

            svc.Update(new Entity("account", descendant) { ["name"] = "deep" });
            Assert.Equal("deep", svc.Retrieve("account", descendant, new Microsoft.Xrm.Sdk.Query.ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        // --- Sharing round trip: GrantAccess / ModifyAccess / RevokeAccess <-> RetrievePrincipalAccess ---

        [Fact]
        public void GrantAccess_is_reflected_by_RetrievePrincipalAccess_then_Modify_and_Revoke_update_it()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("owner", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write, Privilege.Delete, Privilege.Share))
                    .User(Alice, SalesBu, "owner")
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write, Privilege.Delete))
                    .User(Bob, SalesBu, "basic")) // Basic only: reaches none of these records without a share
                .Seed(AccountOwnedBy(record, Alice, SalesBu));

            // Before any grant, Bob has no access.
            Assert.Equal(AccessRights.None, PrincipalAccessFor(svc, record, Bob));

            svc.Execute(new GrantAccessRequest
            {
                Target = new EntityReference("account", record),
                PrincipalAccess = new PrincipalAccess
                {
                    Principal = new EntityReference("systemuser", Bob),
                    AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess,
                },
            });
            AccessRights afterGrant = PrincipalAccessFor(svc, record, Bob);
            Assert.True(afterGrant.HasFlag(AccessRights.ReadAccess));
            Assert.True(afterGrant.HasFlag(AccessRights.WriteAccess));

            // Modify narrows Bob's mask to Read only.
            svc.Execute(new ModifyAccessRequest
            {
                Target = new EntityReference("account", record),
                PrincipalAccess = new PrincipalAccess
                {
                    Principal = new EntityReference("systemuser", Bob),
                    AccessMask = AccessRights.ReadAccess,
                },
            });
            AccessRights afterModify = PrincipalAccessFor(svc, record, Bob);
            Assert.True(afterModify.HasFlag(AccessRights.ReadAccess));
            Assert.False(afterModify.HasFlag(AccessRights.WriteAccess));

            // Revoke removes Bob's shared access entirely.
            svc.Execute(new RevokeAccessRequest
            {
                Target = new EntityReference("account", record),
                Revokee = new EntityReference("systemuser", Bob),
            });
            Assert.Equal(AccessRights.None, PrincipalAccessFor(svc, record, Bob));
        }

        // --- A user can only share rights they themselves hold (the clamp) ------------------------------

        [Fact]
        public void GrantAccess_is_clamped_to_the_rights_the_sharer_holds()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    // Alice can Read and Share, but has no Write on the table.
                    .Role("reader-sharer", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Share))
                    .User(Alice, SalesBu, "reader-sharer")
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write))
                    .User(Bob, SalesBu, "basic")) // Bob could hold a shared Write
                .Seed(AccountOwnedBy(record, Alice, SalesBu));

            svc.Execute(new GrantAccessRequest
            {
                Target = new EntityReference("account", record),
                PrincipalAccess = new PrincipalAccess
                {
                    Principal = new EntityReference("systemuser", Bob),
                    AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess, // asks for more than Alice has
                },
            });

            AccessRights granted = PrincipalAccessFor(svc, record, Bob);
            Assert.True(granted.HasFlag(AccessRights.ReadAccess));
            Assert.False(granted.HasFlag(AccessRights.WriteAccess)); // clamped away — Alice can't grant Write
        }

        // --- Sharing itself requires the Share privilege ------------------------------------------------

        [Fact]
        public void GrantAccess_is_denied_without_the_share_privilege()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write)) // no Share
                    .User(Alice, SalesBu, "editor")
                    .User(Bob, SalesBu))
                .Seed(AccountOwnedBy(record, Alice, SalesBu));

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Execute(new GrantAccessRequest
                {
                    Target = new EntityReference("account", record),
                    PrincipalAccess = new PrincipalAccess
                    {
                        Principal = new EntityReference("systemuser", Bob),
                        AccessMask = AccessRights.ReadAccess,
                    },
                }));
            Assert.Equal(PrincipalPrivilegeDenied, ex.Detail.ErrorCode);
        }

        // --- A shared record becomes readable by the grantee --------------------------------------------

        [Fact]
        public void A_record_shared_to_a_user_becomes_readable_by_that_user()
        {
            Guid record = Guid.NewGuid();
            // Bob is the current user and holds only Basic Read and owns nothing, so the only way he can read is via the share.
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Bob)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Bob, SalesBu, "basic"))
                .Seed(AccountOwnedBy(record, Alice, SalesBu))
                .WithSharedRecord(new EntityReference("account", record), new EntityReference("systemuser", Bob), AccessRights.ReadAccess);

            Assert.NotNull(svc.Retrieve("account", record, new Microsoft.Xrm.Sdk.Query.ColumnSet(true)));
        }

        // --- Create-on-behalf: a User-level Assign must not reach a principal in another BU --------------

        [Fact]
        public void User_level_assign_cannot_create_on_behalf_of_a_principal_in_another_bu()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    // Org-level Create satisfies the create depth for any owner; Assign is only User-level, so it
                    // covers only records Alice will own — never an assignment to another principal.
                    .Role("author", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Create)
                        .Grant("account", AccessLevel.User, Privilege.Assign))
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read)) // the owner must hold Read
                    .User(Alice, SalesBu, "author")
                    .User(Bob, OtherBu, "reader"));

            svc.Create(new Entity("account")); // owned by Alice: no assignment implied, allowed

            // Creating on behalf of Bob (another BU) is an assignment a User-level Assign cannot cover.
            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", Bob) }));
            Assert.Equal(unchecked((int)0x80040220), ex.Detail.ErrorCode); // PrincipalPrivilegeDenied: the Assign, not the owner's Read
        }

        // --- Assign across BUs needs a broad enough Assign depth ----------------------------------------

        [Fact]
        public void Organization_level_assign_can_reassign_a_record_owned_in_another_bu()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(OtherBu)
                    .Role("assigner", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Assign))
                    .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read)) // the owner must hold Read
                    .User(Alice, SalesBu, "assigner")
                    .User(Carol, SalesBu, "reader"))
                .Seed(AccountOwnedBy(record, Bob, OtherBu));

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = new EntityReference("systemuser", Carol),
            });

            Entity stored = Assert.Single(svc.GetAll("account"));
            Assert.Equal(Carol, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            // The owning business unit follows the new owner into Alice/Carol's BU.
            Assert.Equal(SalesBu, stored.GetAttributeValue<EntityReference>("owningbusinessunit").Id);
        }
    }
}
