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
    // Cascade Share/Unshare across a 1:N relationship, and the Reparent behaviour (changing a child's parent
    // re-derives its inherited access — which is automatic here because inherited access is computed live).
    public sealed class CascadeSharingReparentTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000501");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000502");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000005b1");

        private static Entity AccountOwnedBy(Guid id, Guid owner) => new Entity("account", id)
        {
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static Entity ContactUnder(Guid id, Guid account, Guid owner) => new Entity("contact", id)
        {
            ["parentcustomerid"] = new EntityReference("account", account),
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static AccessRights SharedRightsOf(InMemoryOrganizationService svc, Guid contactId, Guid principal)
        {
            var response = (RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                new RetrieveSharedPrincipalsAndAccessRequest { Target = new EntityReference("contact", contactId) });
            PrincipalAccess share = response.PrincipalAccesses.FirstOrDefault(p => p.Principal.Id == principal);
            return share?.AccessMask ?? AccessRights.None;
        }

        // Alice can read + share accounts; Bob is the sharee (no roles).
        private static InMemoryOrganizationService Sharing(Guid account, Guid contact, bool parental)
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().WithCurrentUser(Alice);
            svc = parental
                ? svc.WithParentalRelationship("account", "contact", "parentcustomerid")
                : svc.WithReferentialRelationship("account", "contact", "parentcustomerid");
            return svc
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("sharer", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Share))
                    .User(Alice, SalesBu, "sharer")
                    .User(Bob, SalesBu))
                .Seed(AccountOwnedBy(account, Alice), ContactUnder(contact, account, Alice));
        }

        private static GrantAccessRequest GrantRead(Guid account, Guid principal) => new GrantAccessRequest
        {
            Target = new EntityReference("account", account),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", principal), AccessMask = AccessRights.ReadAccess },
        };

        [Fact]
        public void Sharing_a_parent_cascades_the_share_to_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = Sharing(account, contact, parental: true);

            svc.Execute(GrantRead(account, Bob));

            Assert.True(SharedRightsOf(svc, contact, Bob).HasFlag(AccessRights.ReadAccess)); // child shared by cascade
        }

        [Fact]
        public void Referential_share_does_not_cascade_to_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = Sharing(account, contact, parental: false);

            svc.Execute(GrantRead(account, Bob));

            Assert.Equal(AccessRights.None, SharedRightsOf(svc, contact, Bob)); // no cascade
        }

        [Fact]
        public void Revoking_a_parent_cascades_the_unshare_to_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = Sharing(account, contact, parental: true);
            svc.Execute(GrantRead(account, Bob));
            Assert.True(SharedRightsOf(svc, contact, Bob).HasFlag(AccessRights.ReadAccess)); // cascaded in

            svc.Execute(new RevokeAccessRequest
            {
                Target = new EntityReference("account", account),
                Revokee = new EntityReference("systemuser", Bob),
            });

            Assert.Equal(AccessRights.None, SharedRightsOf(svc, contact, Bob)); // unshare cascaded out
        }

        [Fact]
        public void Reparenting_a_child_re_derives_its_inherited_access()
        {
            Guid accountA = Guid.NewGuid();
            Guid accountB = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    // Alice owns account A (User-read reaches it), can move the contact's lookup, but has no contact Read.
                    .Role("reparenter", r => r
                        .Grant("account", Privilege.Read, AccessLevel.User)
                        .Grant("account", Privilege.AppendTo, AccessLevel.Organization)
                        .Grant("contact", AccessLevel.Organization, Privilege.Write, Privilege.Append))
                    .User(Alice, SalesBu, "reparenter"))
                .Seed(
                    AccountOwnedBy(accountA, Alice),  // Alice owns A
                    AccountOwnedBy(accountB, Bob),    // Bob owns B
                    ContactUnder(contact, accountA, Bob));

            // Under account A (owned by Alice), the contact is readable by inheritance.
            Assert.NotNull(svc.Retrieve("contact", contact, new ColumnSet(true)));

            // Reparent the contact to account B (owned by Bob), which Alice cannot access.
            svc.Update(new Entity("contact", contact) { ["parentcustomerid"] = new EntityReference("account", accountB) });

            // Inherited read is re-derived from the new parent, so the contact is no longer readable.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("contact", contact, new ColumnSet(true)));
        }
    }
}
