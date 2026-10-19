using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Inherited access over a parental relationship, as verified live (2026-10-19): role access to a parent confers
    // nothing; the Reparent cascade gives a child, when it is parented, grants for the parent's owner and for the
    // principals the parent is already shared with; the Share cascade carries later shares down as inherited (not
    // explicit) grants; and an inherited grant only counts with the privilege on the child table.
    public sealed class InheritedAccessTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000401");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000402");
        private static readonly Guid Carol = new Guid("ca401000-0000-0000-0000-000000000403");
        private static readonly Guid Manager = new Guid("3a7a6e00-0000-0000-0000-000000000404");
        private static readonly Guid SalesTeam = new Guid("7ea11000-0000-0000-0000-000000000405");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000004b1");

        private static readonly Privilege[] All =
        {
            Privilege.Create, Privilege.Read, Privilege.Write, Privilege.Delete,
            Privilege.Append, Privilege.AppendTo, Privilege.Assign, Privilege.Share,
        };

        // Reparent cascades, Share/Unshare do not: only what the parent holds when the child is parented flows down.
        private static readonly CascadeConfiguration ReparentOnly = new CascadeConfiguration
        {
            Assign = CascadeType.NoCascade, Delete = CascadeType.RemoveLink, Merge = CascadeType.Cascade,
            Reparent = CascadeType.Cascade, Share = CascadeType.NoCascade, Unshare = CascadeType.NoCascade,
        };

        // Share/Unshare cascade, Reparent does not: parenting a child gives it nothing.
        private static readonly CascadeConfiguration ShareOnly = new CascadeConfiguration
        {
            Assign = CascadeType.NoCascade, Delete = CascadeType.RemoveLink, Merge = CascadeType.Cascade,
            Reparent = CascadeType.NoCascade, Share = CascadeType.Cascade, Unshare = CascadeType.Cascade,
        };

        // Carol (the acting user) and Bob administer everything; Alice holds <paramref name="alice"/> and is the
        // principal whose access each test asserts.
        private static InMemoryOrganizationService Store(CascadeConfiguration contactRelationship, Action<RoleBuilder> alice) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Carol)
                .WithRelationship("account", "contact", "parentcustomerid", contactRelationship)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("admin", r => r
                        .Grant("account", AccessLevel.Organization, All)
                        .Grant("contact", AccessLevel.Organization, All)
                        .Grant("task", AccessLevel.Organization, All)
                        .Grant("new_project", AccessLevel.Organization, All))
                    .Role("alice", alice)
                    .User(Carol, SalesBu, "admin")
                    .User(Bob, SalesBu, "admin")
                    .User(Alice, SalesBu, "alice"));

        private static CascadeConfiguration Parental => new CascadeConfiguration
        {
            Assign = CascadeType.Cascade, Delete = CascadeType.Cascade, Merge = CascadeType.Cascade,
            Reparent = CascadeType.Cascade, Share = CascadeType.Cascade, Unshare = CascadeType.Cascade,
        };

        // Alice: Basic Read+Write on account (reaches only accounts she owns) and Basic Read+Write+Delete on contact
        // (reaches no contact she doesn't own) — so any contact access she has is inherited.
        private static void BasicAccountAndContact(RoleBuilder r) => r
            .Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write)
            .Grant("contact", AccessLevel.User, Privilege.Read, Privilege.Write, Privilege.Delete);

        private static Entity AccountOwnedBy(Guid id, Guid owner) => new Entity("account", id)
        {
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static Entity ContactUnder(Guid account) => new Entity("contact")
        {
            ["parentcustomerid"] = new EntityReference("account", account),
        };

        private static AccessRights AccessOf(InMemoryOrganizationService svc, Guid principal, string entity, Guid id) =>
            ((RetrievePrincipalAccessResponse)svc.Execute(new RetrievePrincipalAccessRequest
            {
                Target = new EntityReference(entity, id),
                Principal = new EntityReference("systemuser", principal),
            })).AccessRights;

        private static PrincipalAccess[] ExplicitSharesOf(InMemoryOrganizationService svc, string entity, Guid id) =>
            ((RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                new RetrieveSharedPrincipalsAndAccessRequest { Target = new EntityReference(entity, id) })).PrincipalAccesses;

        private static void Share(InMemoryOrganizationService svc, Guid account, Guid principal, AccessRights rights) =>
            svc.Execute(new GrantAccessRequest
            {
                Target = new EntityReference("account", account),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", principal), AccessMask = rights },
            });

        private static void Unshare(InMemoryOrganizationService svc, Guid account, Guid principal) =>
            svc.Execute(new RevokeAccessRequest
            {
                Target = new EntityReference("account", account),
                Revokee = new EntityReference("systemuser", principal),
            });

        private static void Reparent(InMemoryOrganizationService svc, Guid contact, EntityReference? parent) =>
            svc.Update(new Entity("contact", contact) { ["parentcustomerid"] = parent });

        // --- Roles alone ----------------------------------------------------------------------------------

        [Fact]
        public void Role_access_to_the_parent_alone_confers_nothing_on_the_child()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(Parental, r => r
                .Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write)
                .Grant("contact", AccessLevel.User, Privilege.Read))
                .Seed(AccountOwnedBy(account, Bob));
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "account", account));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
            svc.WithCurrentUser(Alice);
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Retrieve("contact", contact, new ColumnSet(true)));
            Assert.Empty(svc.RetrieveMultiple(new QueryExpression("contact")).Entities);
        }

        // --- Reparent cascade: the parent's owner -----------------------------------------------------------

        [Fact]
        public void The_parents_owner_inherits_its_rights_on_a_child_created_under_it()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact).Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account));

            // Read+Write from the parent; not Delete, which Alice holds on contact but not on the account.
            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));
            Assert.Empty(ExplicitSharesOf(svc, "contact", contact)); // inherited, not an explicit share

            svc.WithCurrentUser(Alice);
            svc.Update(new Entity("contact", contact) { ["jobtitle"] = "edited" });
            Assert.Equal("edited", svc.Retrieve("contact", contact, new ColumnSet(true)).GetAttributeValue<string>("jobtitle"));
            Assert.Equal(contact, Assert.Single(svc.RetrieveMultiple(new QueryExpression("contact")).Entities).Id);
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Delete("contact", contact));
        }

        [Fact]
        public void The_inherited_rights_are_bounded_by_the_owners_rights_on_the_parent()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, r => r
                .Grant("account", AccessLevel.User, Privilege.Read)
                .Grant("contact", AccessLevel.User, All))
                .Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void An_inherited_grant_needs_the_privilege_on_the_child_table()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, r => r
                .Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write)
                .Grant("contact", AccessLevel.User, Privilege.Write)) // no Read on contact
                .Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));
            svc.WithCurrentUser(Alice);
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Retrieve("contact", contact, new ColumnSet(true)));
        }

        [Fact]
        public void Without_any_privilege_on_the_child_table_the_inherited_grant_confers_nothing()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, r => r.Grant("account", AccessLevel.User, Privilege.Read))
                .Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void A_team_owned_parent_gives_the_inherited_rights_to_the_teams_members()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Carol)
                .WithRelationship("account", "contact", "parentcustomerid", ReparentOnly)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization, All).Grant("contact", AccessLevel.Organization, All))
                    .Role("teamrole", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .Role("contactreader", r => r.Grant("contact", AccessLevel.User, Privilege.Read, Privilege.Write))
                    .User(Carol, SalesBu, "admin")
                    .User(Alice, SalesBu, "contactreader")
                    .User(Bob, SalesBu, "contactreader")
                    .Team(SalesTeam, SalesBu, "teamrole")
                    .Member(Alice, SalesTeam))
                .Seed(new Entity("account", account)
                {
                    ["ownerid"] = new EntityReference("team", SalesTeam),
                    ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
                });
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact)); // the team's Read on its account
            Assert.Equal(AccessRights.None, AccessOf(svc, Bob, "contact", contact));          // not a member
        }

        [Fact]
        public void A_share_only_relationship_gives_the_parents_owner_nothing()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ShareOnly, BasicAccountAndContact).Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        // --- Reparent cascade: the parent's existing shares --------------------------------------------------

        [Fact]
        public void A_share_made_before_the_child_is_parented_is_inherited_through_reparent()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact).Seed(AccountOwnedBy(account, Bob));
            Share(svc, account, Alice, AccessRights.ReadAccess);
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact));
            Assert.Empty(ExplicitSharesOf(svc, "contact", contact));
        }

        [Fact]
        public void A_parent_with_no_owner_passes_on_only_its_shares()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact)
                .Seed(new Entity("account", account))
                .WithSharedRecord(new EntityReference("account", account), new EntityReference("systemuser", Alice), AccessRights.WriteAccess);
            Guid contact = svc.Create(ContactUnder(account));

            Assert.Equal(AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void A_share_made_after_the_child_is_parented_does_not_flow_through_reparent()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact).Seed(AccountOwnedBy(account, Bob));
            Guid contact = svc.Create(ContactUnder(account));
            Share(svc, account, Alice, AccessRights.ReadAccess);

            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "account", account));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void An_inherited_grant_on_the_parent_is_passed_on_to_a_grandchild()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, r => r
                .Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write)
                .Grant("contact", AccessLevel.User, Privilege.Read, Privilege.Write)
                .Grant("task", AccessLevel.User, Privilege.Read))
                .WithRelationship("contact", "task", "regardingobjectid", ReparentOnly)
                .Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account));
            Guid task = svc.Create(new Entity("task") { ["regardingobjectid"] = new EntityReference("contact", contact) });

            // account (owned by Alice) -> contact (inherited Read+Write) -> task (passed on; Read is all she holds on task).
            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "task", task));
        }

        // --- Reparenting ----------------------------------------------------------------------------------

        [Fact]
        public void Moving_the_child_to_another_parent_replaces_its_inherited_grants()
        {
            Guid aliceAccount = Guid.NewGuid();
            Guid bobAccount = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact)
                .Seed(AccountOwnedBy(aliceAccount, Alice), AccountOwnedBy(bobAccount, Bob));
            Guid contact = svc.Create(ContactUnder(aliceAccount));
            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));

            Reparent(svc, contact, new EntityReference("account", bobAccount));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact)); // the old parent's grant is gone

            Reparent(svc, contact, new EntityReference("account", aliceAccount));
            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));

            Reparent(svc, contact, null);
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact)); // no parent, nothing inherited
        }

        [Fact]
        public void Only_a_change_of_parent_re_derives_the_inherited_grants()
        {
            Guid account = Guid.NewGuid();
            Guid other = Guid.NewGuid();
            Guid project = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact)
                .WithReferentialRelationship("new_project", "contact", "new_projectid")
                .Seed(AccountOwnedBy(account, Alice), AccountOwnedBy(other, Bob), new Entity("new_project", project));
            Guid contact = svc.Create(ContactUnder(account));

            // Assign does not cascade over this relationship: Alice's grant, taken when the contact was parented, stays
            // through other field and referential-lookup changes.
            svc.Execute(new AssignRequest { Target = new EntityReference("account", account), Assignee = new EntityReference("systemuser", Bob) });
            svc.Update(new Entity("contact", contact) { ["jobtitle"] = "edited" });
            svc.Update(new Entity("contact", contact) { ["new_projectid"] = new EntityReference("new_project", project) });
            Reparent(svc, contact, new EntityReference("account", account)); // same parent: not a reparent
            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));

            // Moving away and back re-derives from the account's CURRENT owner (Bob), so Alice's grant is gone.
            Reparent(svc, contact, new EntityReference("account", other));
            Reparent(svc, contact, new EntityReference("account", account));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void Reparenting_a_record_does_not_re_derive_its_existing_descendants()
        {
            Guid aliceAccount = Guid.NewGuid();
            Guid bobAccount = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, r => r
                .Grant("account", AccessLevel.User, Privilege.Read)
                .Grant("contact", AccessLevel.User, Privilege.Read)
                .Grant("task", AccessLevel.User, Privilege.Read))
                .WithRelationship("contact", "task", "regardingobjectid", ReparentOnly)
                .Seed(AccountOwnedBy(aliceAccount, Alice), AccountOwnedBy(bobAccount, Bob));
            Guid contact = svc.Create(ContactUnder(aliceAccount));
            Guid task = svc.Create(new Entity("task") { ["regardingobjectid"] = new EntityReference("contact", contact) });

            Reparent(svc, contact, new EntityReference("account", bobAccount));

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "task", task)); // multi-level re-derivation is not modelled
        }

        [Fact]
        public void Merge_re_derives_the_inherited_grants_of_the_children_it_moves()
        {
            Guid master = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact)
                .Seed(AccountOwnedBy(master, Alice), AccountOwnedBy(subordinate, Bob));
            Guid contact = svc.Create(ContactUnder(subordinate));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));

            svc.Execute(new MergeRequest
            {
                Target = new EntityReference("account", master),
                SubordinateId = subordinate,
                UpdateContent = new Entity("account"),
            });

            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, false)]
        public void An_active_reparent_cascade_skips_an_inactive_child(int state, bool inherited)
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(new CascadeConfiguration { Reparent = CascadeType.Active }, BasicAccountAndContact)
                .Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(new Entity("contact")
            {
                ["parentcustomerid"] = new EntityReference("account", account),
                ["statecode"] = new OptionSetValue(state),
            });

            Assert.Equal(inherited ? AccessRights.ReadAccess | AccessRights.WriteAccess : AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void A_user_owned_reparent_cascade_covers_only_children_owned_by_the_parents_owner()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(new CascadeConfiguration { Reparent = CascadeType.UserOwned }, r => r
                .Grant("account", AccessLevel.User, Privilege.Read)
                .Grant("contact", AccessLevel.User, Privilege.Read))
                .Seed(AccountOwnedBy(account, Bob));
            Share(svc, account, Alice, AccessRights.ReadAccess);
            Guid carols = svc.Create(ContactUnder(account)); // owned by Carol, the caller
            Guid bobs = svc.Create(new Entity("contact")
            {
                ["parentcustomerid"] = new EntityReference("account", account),
                ["ownerid"] = new EntityReference("systemuser", Bob),
            });

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", carols));
            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", bobs));
        }

        [Fact]
        public void A_seeded_child_inherits_like_a_created_one_whatever_its_position_in_the_seed()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact)
                .Seed(new Entity("contact", contact) { ["parentcustomerid"] = new EntityReference("account", account) }, AccountOwnedBy(account, Alice));

            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void Without_a_security_model_no_inherited_grant_is_recorded()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithRelationship("account", "contact", "parentcustomerid", ReparentOnly)
                .Seed(AccountOwnedBy(account, Alice), new Entity("contact", contact) { ["parentcustomerid"] = new EntityReference("account", account) })
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("alice", BasicAccountAndContact)
                    .User(Alice, SalesBu, "alice"));

            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "account", account));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void A_referential_relationship_gives_the_child_nothing()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Carol)
                .WithReferentialRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization, All).Grant("contact", AccessLevel.Organization, All))
                    .Role("alice", BasicAccountAndContact)
                    .User(Carol, SalesBu, "admin")
                    .User(Alice, SalesBu, "alice"))
                .Seed(AccountOwnedBy(account, Alice));
            Share(svc, account, Alice, AccessRights.ReadAccess);
            Guid contact = svc.Create(ContactUnder(account));
            Share(svc, account, Alice, AccessRights.WriteAccess);

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        // --- Share cascade --------------------------------------------------------------------------------

        [Fact]
        public void A_share_cascades_to_existing_children_as_an_inherited_grant()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ShareOnly, BasicAccountAndContact).Seed(AccountOwnedBy(account, Bob));
            Guid contact = svc.Create(ContactUnder(account));

            Share(svc, account, Alice, AccessRights.ReadAccess);

            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact));
            Assert.Empty(ExplicitSharesOf(svc, "contact", contact)); // not listed as an explicit share
            svc.WithCurrentUser(Alice);
            Assert.NotNull(svc.Retrieve("contact", contact, new ColumnSet(true)));
        }

        [Fact]
        public void Unsharing_the_parent_removes_the_inherited_grant_but_keeps_the_childs_own_share()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(Parental, BasicAccountAndContact).Seed(AccountOwnedBy(account, Bob));
            Guid shared = svc.Create(ContactUnder(account));
            Guid alsoOwnShare = svc.Create(ContactUnder(account));
            Share(svc, account, Alice, AccessRights.ReadAccess | AccessRights.WriteAccess);
            svc.Execute(new GrantAccessRequest
            {
                Target = new EntityReference("contact", alsoOwnShare),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", Alice), AccessMask = AccessRights.ReadAccess },
            });

            Unshare(svc, account, Alice);

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", shared));
            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", alsoOwnShare)); // its explicit share remains
        }

        [Fact]
        public void Unsharing_the_parent_also_removes_a_share_inherited_through_reparent()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(Parental, BasicAccountAndContact).Seed(AccountOwnedBy(account, Bob));
            Share(svc, account, Alice, AccessRights.ReadAccess);
            Guid contact = svc.Create(ContactUnder(account));
            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact));

            Unshare(svc, account, Alice);

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void Without_an_unshare_cascade_the_inherited_grant_survives_the_parents_unshare()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(new CascadeConfiguration { Share = CascadeType.Cascade, Unshare = CascadeType.NoCascade }, BasicAccountAndContact)
                .Seed(AccountOwnedBy(account, Bob));
            Guid contact = svc.Create(ContactUnder(account));
            Share(svc, account, Alice, AccessRights.ReadAccess);

            Unshare(svc, account, Alice);

            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "account", account));
            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact));
        }

        [Fact]
        public void A_cascaded_share_rolls_back_with_a_failed_transaction()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ShareOnly, BasicAccountAndContact).Seed(AccountOwnedBy(account, Bob));
            Guid contact = svc.Create(ContactUnder(account));
            Share(svc, account, Alice, AccessRights.ReadAccess);

            ExecuteTransactionRequest tx = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            tx.Requests.Add(new GrantAccessRequest
            {
                Target = new EntityReference("account", account),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", Alice), AccessMask = AccessRights.WriteAccess },
            });
            tx.Requests.Add(new DeleteRequest { Target = new EntityReference("account", Guid.NewGuid()) }); // does not exist -> throws
            Assert.ThrowsAny<Exception>(() => svc.Execute(tx));

            Assert.Equal(AccessRights.ReadAccess, AccessOf(svc, Alice, "contact", contact)); // restored, not dropped or widened
        }

        // --- Hierarchy ------------------------------------------------------------------------------------

        [Fact]
        public void A_manager_reaches_a_child_through_the_inherited_grant_of_a_report()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Carol)
                .WithRelationship("account", "contact", "parentcustomerid", ReparentOnly)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization, All).Grant("contact", AccessLevel.Organization, All))
                    .Role("basic", BasicAccountAndContact)
                    .User(Carol, SalesBu, "admin")
                    .User(Alice, SalesBu, "basic")
                    .User(Manager, SalesBu, "basic")
                    .Manager(Alice, Manager)
                    .ManagerHierarchy(1))
                .Seed(AccountOwnedBy(account, Alice));
            Guid contact = svc.Create(ContactUnder(account)); // owned by Carol, inherited by Alice

            Assert.True(AccessOf(svc, Manager, "contact", contact).HasFlag(AccessRights.ReadAccess));
        }

        // --- Polymorphic lookups and the one-parent rule ---------------------------------------------------

        [Fact]
        public void A_polymorphic_lookup_inherits_only_over_the_relationship_to_the_table_it_points_at()
        {
            Guid account = Guid.NewGuid();
            Guid parentContact = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(ReparentOnly, BasicAccountAndContact)
                // Same lookup column, second target table: allowed. This one only cascades sharing.
                .WithRelationship("contact", "contact", "parentcustomerid", ShareOnly)
                .Seed(AccountOwnedBy(account, Alice), new Entity("contact", parentContact) { ["ownerid"] = new EntityReference("systemuser", Alice) });
            Guid underAccount = svc.Create(ContactUnder(account));
            Guid underContact = svc.Create(new Entity("contact") { ["parentcustomerid"] = new EntityReference("contact", parentContact) });

            Assert.Equal(AccessRights.ReadAccess | AccessRights.WriteAccess, AccessOf(svc, Alice, "contact", underAccount));
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", underContact)); // contact -> contact has no Reparent cascade

            Reparent(svc, underAccount, new EntityReference("contact", parentContact)); // switches table: re-derived
            Assert.Equal(AccessRights.None, AccessOf(svc, Alice, "contact", underAccount));
        }

        public static TheoryData<CascadeConfiguration> ParentalConfigurations()
        {
            TheoryData<CascadeConfiguration> data = new TheoryData<CascadeConfiguration>();
            foreach (CascadeType type in new[] { CascadeType.Cascade, CascadeType.Active, CascadeType.UserOwned })
            {
                data.Add(new CascadeConfiguration { Assign = type });
                data.Add(new CascadeConfiguration { Reparent = type });
                data.Add(new CascadeConfiguration { Share = type });
                data.Add(new CascadeConfiguration { Unshare = type });
            }

            data.Add(new CascadeConfiguration { Delete = CascadeType.Cascade });
            return data;
        }

        [Theory]
        [MemberData(nameof(ParentalConfigurations))]
        public void A_second_parental_relationship_on_the_same_table_is_rejected(CascadeConfiguration second)
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid");

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => svc.WithRelationship("new_project", "contact", "new_projectid", second));

            Assert.Equal(
                "Entity: contact is parented to Entity: account (through 'parentcustomerid'). Cannot create another parental " +
                "relation with Entity: new_project (through 'new_projectid'). Dataverse allows one parental relationship per " +
                "table (0x80047007); declare this one as referential, or with no Assign/Reparent/Share/Unshare cascade and a " +
                "Delete behaviour other than Cascade.",
                ex.Message);
        }

        [Theory]
        [MemberData(nameof(ParentalConfigurations))]
        public void An_existing_parental_relationship_of_any_kind_blocks_another(CascadeConfiguration first)
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithRelationship("new_project", "contact", "new_projectid", first);

            Assert.Throws<InvalidOperationException>(() => svc.WithParentalRelationship("account", "contact", "parentcustomerid"));
        }

        public static TheoryData<CascadeConfiguration> NonParentalConfigurations() => new TheoryData<CascadeConfiguration>
        {
            new CascadeConfiguration(),
            new CascadeConfiguration { Delete = CascadeType.RemoveLink, Merge = CascadeType.Cascade },
            new CascadeConfiguration { Delete = CascadeType.Restrict },
            new CascadeConfiguration
            {
                Assign = CascadeType.NoCascade, Reparent = CascadeType.NoCascade, Share = CascadeType.NoCascade,
                Unshare = CascadeType.NoCascade, Delete = CascadeType.RemoveLink,
            },
        };

        [Theory]
        [MemberData(nameof(NonParentalConfigurations))]
        public void A_non_parental_relationship_can_sit_beside_the_parental_one(CascadeConfiguration other)
        {
            Guid account = Guid.NewGuid();
            Guid project = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithRelationship("new_project", "contact", "new_projectid", other)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithRelationship("new_project", "contact", "new_secondaryprojectid", other)
                .Seed(new Entity("account", account), new Entity("new_project", project));
            svc.Create(new Entity("contact")
            {
                ["parentcustomerid"] = new EntityReference("account", account),
                ["new_projectid"] = new EntityReference("new_project", project),
            });

            svc.Delete("account", account); // the parental relationship still drives the cascade

            Assert.Empty(svc.GetAll("contact"));
        }

        [Fact]
        public void Each_table_may_have_its_own_parental_relationship_and_a_polymorphic_lookup_two()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithParentalRelationship("contact", "contact", "parentcustomerid")
                .WithParentalRelationship("contact", "task", "regardingobjectid")
                .WithParentalRelationship("account", "task", "regardingobjectid");

            Assert.NotNull(svc);
        }
    }
}
