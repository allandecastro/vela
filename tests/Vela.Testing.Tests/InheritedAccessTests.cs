using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Inherited access (the 3rd access source, besides roles and sharing): in a parental relationship a child
    // inherits the access the user has on its parent, for every privilege except Create.
    public sealed class InheritedAccessTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000401");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000402");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000004b1");

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

        // Alice can act on accounts (via role) but has NO privilege at all on contact — so any contact access is
        // inherited from the parent account.
        private static InMemoryOrganizationService WithAccountRole(Guid account, Guid contact, params Privilege[] accountPrivileges) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("acct", r => r.Grant("account", AccessLevel.Organization, accountPrivileges))
                    .User(Alice, SalesBu, "acct"))
                .Seed(AccountOwnedBy(account, Bob), ContactUnder(contact, account, Bob));

        [Fact]
        public void A_child_inherits_read_access_from_its_parent()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = WithAccountRole(account, contact, Privilege.Read);

            Assert.NotNull(svc.Retrieve("contact", contact, new ColumnSet(true))); // readable via the parent
        }

        [Fact]
        public void A_referential_child_does_not_inherit_access()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithReferentialRelationship("account", "contact", "parentcustomerid") // Reparent = NoCascade
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("acct", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "acct"))
                .Seed(AccountOwnedBy(account, Bob), ContactUnder(contact, account, Bob));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("contact", contact, new ColumnSet(true)));
        }

        [Fact]
        public void A_child_inherits_write_access_from_its_parent()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = WithAccountRole(account, contact, Privilege.Read, Privilege.Write);

            svc.Update(new Entity("contact", contact) { ["jobtitle"] = "edited" }); // inherited write

            Assert.Equal("edited", svc.Retrieve("contact", contact, new ColumnSet(true)).GetAttributeValue<string>("jobtitle"));
        }

        [Fact]
        public void Inherited_access_walks_up_the_hierarchy()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            Guid task = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithParentalRelationship("contact", "task", "regardingobjectid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("acct", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                    .User(Alice, SalesBu, "acct"))
                .Seed(
                    AccountOwnedBy(account, Bob),
                    ContactUnder(contact, account, Bob),
                    new Entity("task", task) { ["regardingobjectid"] = new EntityReference("contact", contact) });

            Assert.NotNull(svc.Retrieve("task", task, new ColumnSet(true))); // account -> contact -> task
        }

        [Fact]
        public void Read_filtering_includes_records_readable_by_inheritance()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = WithAccountRole(account, contact, Privilege.Read);

            EntityCollection visible = svc.RetrieveMultiple(
                new QueryExpression("contact") { ColumnSet = new ColumnSet(true) });

            Assert.Equal(contact, Assert.Single(visible.Entities).Id); // surfaced via inherited read
        }
    }
}
