using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
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

        // --- Cascade Assign -----------------------------------------------------------------------------

        private static Entity AccountOwned(Guid id, Guid owner) =>
            new Entity("account", id) { ["ownerid"] = new EntityReference("systemuser", owner) };

        private static Entity ContactOwned(Guid id, Guid parent, Guid owner) => new Entity("contact", id)
        {
            ["parentcustomerid"] = new EntityReference("account", parent),
            ["ownerid"] = new EntityReference("systemuser", owner),
        };

        private static Guid OwnerOf(InMemoryOrganizationService svc, Guid contactId) =>
            svc.Retrieve("contact", contactId, new ColumnSet(true)).GetAttributeValue<EntityReference>("ownerid").Id;

        [Fact]
        public void Parental_assign_cascades_the_new_owner_to_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(AccountOwned(account, Bob), ContactOwned(contact, account, Bob));

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Assert.Equal(Alice, OwnerOf(svc, contact)); // child reassigned with the parent
        }

        [Fact]
        public void Referential_assign_does_not_cascade_to_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid") // Assign = NoCascade
                .Seed(AccountOwned(account, Bob), ContactOwned(contact, account, Bob));

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Assert.Equal(Bob, OwnerOf(svc, contact)); // child keeps its owner
        }

        [Fact]
        public void Assigning_via_a_plain_ownerid_update_also_cascades()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(AccountOwned(account, Bob), ContactOwned(contact, account, Bob));

            svc.Update(new Entity("account", account) { ["ownerid"] = new EntityReference("systemuser", Alice) });

            Assert.Equal(Alice, OwnerOf(svc, contact));
        }

        [Fact]
        public void User_owned_assign_cascades_only_children_owned_by_the_previous_parent_owner()
        {
            Guid account = Guid.NewGuid();
            Guid bobContact = Guid.NewGuid();
            Guid carolContact = Guid.NewGuid();
            Guid carol = new Guid("ca401000-0000-0000-0000-000000000303");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithRelationship("account", "contact", "parentcustomerid", new CascadeConfiguration { Assign = CascadeType.UserOwned })
                .Seed(
                    AccountOwned(account, Bob),                 // parent owned by Bob
                    ContactOwned(bobContact, account, Bob),     // child owned by the same user as the parent
                    ContactOwned(carolContact, account, carol)); // child owned by someone else

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Assert.Equal(Alice, OwnerOf(svc, bobContact));  // cascaded (was owned by the parent's previous owner)
            Assert.Equal(carol, OwnerOf(svc, carolContact)); // left alone
        }

        [Fact]
        public void RetrievePrincipalAccess_reflects_access_inherited_from_the_parent()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("acctreader", r => r.Grant("account", Privilege.Read, AccessLevel.Organization)) // nothing on contact
                    .User(Alice, SalesBu, "acctreader"))
                .Seed(AccountOwned(account, Bob), ContactOf(contact, account));

            RetrievePrincipalAccessResponse response = (RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest
                {
                    Target = new EntityReference("contact", contact),
                    Principal = new EntityReference("systemuser", Alice),
                });

            // Alice holds no role on contact but inherits Read from the parent account she can read.
            Assert.True(response.AccessRights.HasFlag(AccessRights.ReadAccess));
        }

        [Fact]
        public void Cascade_assign_moves_the_childs_business_unit_to_the_new_owner()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("mover", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Assign))
                    .User(Alice, SalesBu, "mover")
                    .User(Bob, new Guid("b0000000-0000-0000-0000-0000000003b9"))) // Bob in another BU
                .Seed(
                    new Entity("account", account) { ["ownerid"] = new EntityReference("systemuser", Bob), ["owningbusinessunit"] = new EntityReference("businessunit", new Guid("b0000000-0000-0000-0000-0000000003b9")) },
                    ContactOwned(contact, account, Bob));

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Entity child = Assert.Single(svc.GetAll("contact")); // GetAll bypasses security (assertion helper)
            Assert.Equal(Alice, child.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(SalesBu, child.GetAttributeValue<EntityReference>("owningbusinessunit").Id); // followed the new owner
        }

        [Fact]
        public void Cascaded_assign_to_a_team_stamps_owning_team_columns_on_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            Guid team = new Guid("7ea11000-0000-0000-0000-000000000305");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(AccountOwned(account, Bob), ContactOwned(contact, account, Bob)); // child starts user-owned

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("team", team),
            });

            Entity child = Assert.Single(svc.GetAll("contact"));
            EntityReference owningTeam = child.GetAttributeValue<EntityReference>("owningteam");
            Assert.Equal(team, owningTeam.Id);
            Assert.Equal("team", owningTeam.LogicalName); // team branch of the owner stamping, not the systemuser branch
            Assert.False(child.Contains("owninguser"));   // the user column is cleared on a team reassignment
        }

        [Fact]
        public void Cascaded_assign_to_a_user_stamps_owning_user_columns_on_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            Guid team = new Guid("7ea11000-0000-0000-0000-000000000306");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(
                    AccountOwned(account, Bob),
                    new Entity("contact", contact)
                    {
                        ["parentcustomerid"] = new EntityReference("account", account),
                        ["ownerid"] = new EntityReference("team", team), // child starts team-owned
                    });

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Entity child = Assert.Single(svc.GetAll("contact"));
            EntityReference owningUser = child.GetAttributeValue<EntityReference>("owninguser");
            Assert.Equal(Alice, owningUser.Id);
            Assert.Equal("systemuser", owningUser.LogicalName); // user branch of the owner stamping
            Assert.False(child.Contains("owningteam"));         // the team column is cleared on a user reassignment
        }

        [Fact]
        public void Active_scope_assign_cascades_only_active_children()
        {
            Guid account = Guid.NewGuid();
            Guid activeChild = Guid.NewGuid();
            Guid inactiveChild = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithRelationship("account", "contact", "parentcustomerid", new CascadeConfiguration { Assign = CascadeType.Active })
                .Seed(
                    AccountOwned(account, Bob),
                    ContactOwned(activeChild, account, Bob), // no statecode -> active
                    new Entity("contact", inactiveChild)
                    {
                        ["parentcustomerid"] = new EntityReference("account", account),
                        ["ownerid"] = new EntityReference("systemuser", Bob),
                        ["statecode"] = new OptionSetValue(1), // inactive
                    });

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            Assert.Equal(Alice, OwnerOf(svc, activeChild));  // active child cascaded
            Assert.Equal(Bob, OwnerOf(svc, inactiveChild));  // inactive child left alone (Active scope)
        }

        [Fact]
        public void Parental_assign_cascades_through_multiple_levels()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            Guid task = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithParentalRelationship("contact", "task", "regardingcontactid")
                .Seed(
                    AccountOwned(account, Bob),
                    ContactOwned(contact, account, Bob),
                    new Entity("task", task)
                    {
                        ["regardingcontactid"] = new EntityReference("contact", contact),
                        ["ownerid"] = new EntityReference("systemuser", Bob),
                    });

            svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", account),
                Assignee = new EntityReference("systemuser", Alice),
            });

            // The grandchild (task under contact under account) is reassigned too — the cascade recurses down levels.
            Assert.Equal(Alice, svc.Retrieve("task", task, new ColumnSet(true)).GetAttributeValue<EntityReference>("ownerid").Id);
        }
    }
}
