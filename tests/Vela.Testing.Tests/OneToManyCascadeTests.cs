using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // One-to-many relationship cascade behaviour on Delete: Parental cascades to children, Referential clears the
    // child link (or Restrict blocks the delete). Cascaded child deletes are system operations and do not re-check
    // the caller's Delete privilege — only the parent's is validated.
    public sealed class OneToManyCascadeTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000301");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000302");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000003b1");

        private static Entity Account(Guid id) => new Entity("account", id);
        private static Entity ContactOf(Guid id, Guid parent) =>
            new Entity("contact", id) { ["parentcustomerid"] = new EntityReference("account", parent) };

        [Fact]
        public void Parental_delete_cascades_to_children()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(Account(account), ContactOf(Guid.NewGuid(), account), ContactOf(Guid.NewGuid(), account));

            svc.Delete("account", account);

            Assert.Empty(svc.GetAll("account"));
            Assert.Empty(svc.GetAll("contact")); // both children cascaded away
        }

        [Fact]
        public void Referential_delete_clears_the_child_link_but_keeps_the_child()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid")
                .Seed(Account(account), ContactOf(contact, account));

            svc.Delete("account", account);

            Assert.Empty(svc.GetAll("account"));
            Entity survivor = Assert.Single(svc.GetAll("contact"));
            Assert.Null(survivor.GetAttributeValue<EntityReference>("parentcustomerid")); // link removed
        }

        [Fact]
        public void Restrict_blocks_deleting_a_parent_that_has_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid", restrictDelete: true)
                .Seed(Account(account), ContactOf(contact, account));

            Assert.Throws<InvalidOperationException>(() => svc.Delete("account", account));
            Assert.Single(svc.GetAll("account"));  // parent untouched
            Assert.Single(svc.GetAll("contact"));  // child untouched
        }

        [Fact]
        public void Restrict_allows_deleting_a_parent_with_no_children()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid", restrictDelete: true)
                .Seed(Account(account));

            svc.Delete("account", account);

            Assert.Empty(svc.GetAll("account"));
        }

        [Fact]
        public void Cascade_delete_is_recursive_down_the_hierarchy()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            Guid task = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithParentalRelationship("contact", "task", "regardingobjectid")
                .Seed(
                    Account(account),
                    ContactOf(contact, account),
                    new Entity("task", task) { ["regardingobjectid"] = new EntityReference("contact", contact) });

            svc.Delete("account", account);

            Assert.Empty(svc.GetAll("account"));
            Assert.Empty(svc.GetAll("contact"));
            Assert.Empty(svc.GetAll("task")); // grandchild cascaded too
        }

        [Fact]
        public void Cascade_delete_does_not_check_the_childs_delete_privilege()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    // Alice can delete accounts, but holds NO privilege at all on contact.
                    .Role("acctadmin", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Delete))
                    .User(Alice, SalesBu, "acctadmin"))
                .Seed(
                    new Entity("account", account) { ["ownerid"] = new EntityReference("systemuser", Bob), ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu) },
                    ContactOf(contact, account));

            svc.Delete("account", account); // parent delete allowed; children cascade as a system operation

            Assert.Empty(svc.GetAll("account"));
            Assert.Empty(svc.GetAll("contact"));
        }

        [Fact]
        public void Without_a_relationship_delete_removes_only_the_target()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(Account(account), ContactOf(contact, account)); // no relationship registered

            svc.Delete("account", account);

            Assert.Empty(svc.GetAll("account"));
            Assert.Single(svc.GetAll("contact")); // untouched — no cascade
        }
    }
}
