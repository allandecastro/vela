using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Mutation-survivor kills for the in-memory store (InMemoryOrganizationService, #448): Associate/Disassociate
    // over N:N and 1:N (from either side), metadata-declared N:N, Merge, ModifyAccess cascade and the security and
    // strict-security demands of the store's message paths.
    public sealed class InMemoryStoreRelationshipMutationTests
    {
        private const int ObjectDoesNotExist = unchecked((int)0x80040217);
        private const int DuplicateRecord = unchecked((int)0x80040237);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000c01");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000c02");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-000000000cb1");

        private static readonly Relationship AccountContacts = new Relationship("account_contacts");
        private static readonly Relationship AccountContact1N = new Relationship("account_contact");

        private static EntityReferenceCollection Refs(params EntityReference[] refs) => new EntityReferenceCollection(refs);

        private static InMemoryOrganizationService ManyToMany() => new InMemoryOrganizationService()
            .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

        // ---- N:N -------------------------------------------------------------------------------------------

        [Fact]
        public void Associate_and_Disassociate_requests_add_and_remove_the_intersect_row()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = ManyToMany().Seed(new Entity("account", account), new Entity("contact", contact));

            svc.Execute(new AssociateRequest
            {
                Target = new EntityReference("account", account),
                Relationship = AccountContacts,
                RelatedEntities = Refs(new EntityReference("contact", contact)),
            });
            Assert.Single(svc.GetAll("account_contact"));

            svc.Execute(new DisassociateRequest
            {
                Target = new EntityReference("account", account),
                Relationship = AccountContacts,
                RelatedEntities = Refs(new EntityReference("contact", contact)),
            });
            Assert.Empty(svc.GetAll("account_contact"));
        }

        [Fact]
        public void Reassociating_an_existing_pair_faults_with_DuplicateRecord()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = ManyToMany().Seed(new Entity("account", account), new Entity("contact", contact));
            svc.Associate("account", account, AccountContacts, Refs(new EntityReference("contact", contact)));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Associate("account", account, AccountContacts, Refs(new EntityReference("contact", contact))));

            Assert.Equal(DuplicateRecord, fault.Detail.ErrorCode);
            Assert.Single(svc.GetAll("account_contact"));
        }

        [Fact]
        public void Associate_with_a_missing_primary_record_faults_and_writes_nothing()
        {
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = ManyToMany().Seed(new Entity("contact", contact));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Associate("account", Guid.NewGuid(), AccountContacts, Refs(new EntityReference("contact", contact))));

            Assert.Equal(ObjectDoesNotExist, fault.Detail.ErrorCode);
            Assert.Empty(svc.GetAll("account_contact"));
        }

        [Fact]
        public void Associate_and_Disassociate_with_a_missing_related_record_fault()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = ManyToMany().Seed(new Entity("account", account));
            EntityReference missing = new EntityReference("contact", Guid.NewGuid());

            FaultException<OrganizationServiceFault> associate = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Associate("account", account, AccountContacts, Refs(missing)));
            Assert.Equal(ObjectDoesNotExist, associate.Detail.ErrorCode);
            Assert.Empty(svc.GetAll("account_contact"));

            FaultException<OrganizationServiceFault> disassociate = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Disassociate("account", account, AccountContacts, Refs(missing)));
            Assert.Equal(ObjectDoesNotExist, disassociate.Detail.ErrorCode);
        }

        // Disassociating account A from contact C leaves account B's link to C alone.
        [Fact]
        public void Disassociate_removes_only_the_callers_pairs()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            Guid c = Guid.NewGuid();
            InMemoryOrganizationService svc = ManyToMany().Seed(new Entity("account", a), new Entity("account", b), new Entity("contact", c));
            svc.Associate("account", a, AccountContacts, Refs(new EntityReference("contact", c)));
            svc.Associate("account", b, AccountContacts, Refs(new EntityReference("contact", c)));

            svc.Disassociate("account", a, AccountContacts, Refs(new EntityReference("contact", c)));

            Entity remaining = Assert.Single(svc.GetAll("account_contact"));
            Assert.Equal(b, remaining.GetAttributeValue<EntityReference>("accountid").Id);
        }

        [Fact]
        public void Disassociate_over_an_undeclared_relationship_explains_how_to_declare_it()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", account), new Entity("contact", contact));

            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Disassociate("account", account, new Relationship("nope"), Refs(new EntityReference("contact", contact))));

            Assert.Equal(unchecked((int)0x80040216), ex.Detail.ErrorCode);
            Assert.StartsWith("The Entity Relationship with SchemaName 'nope' was not found in the MetadataCache.", ex.Detail.Message);
            Assert.Contains("WithManyToMany(\"nope\", ...)", ex.Detail.Message);
        }

        // ---- N:N declared through registered metadata ------------------------------------------------------

        [Fact]
        public void Deleting_a_record_removes_its_metadata_declared_intersect_rows()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account")
                    .ManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid").Build())
                .Seed(new Entity("account", account), new Entity("contact", contact));
            svc.Associate("account", account, AccountContacts, Refs(new EntityReference("contact", contact)));
            Assert.Single(svc.GetAll("account_contact"));

            svc.Delete("contact", contact);

            Assert.Empty(svc.GetAll("account_contact"));
        }

        [Fact]
        public void Delete_works_when_registered_metadata_declares_no_many_to_many()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account").Build())
                .Seed(new Entity("account", account));

            svc.Delete("account", account);

            Assert.Empty(svc.GetAll("account"));
        }

        // Registered metadata carrying an unrelated N:N must not get in the way of resolving a 1:N relationship.
        [Fact]
        public void A_one_to_many_associate_resolves_past_unrelated_metadata_many_to_many()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account")
                    .ManyToMany("account_leads", "account_lead", "account", "accountid", "lead", "leadid").Build())
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .Seed(new Entity("account", account), new Entity("contact", contact));

            svc.Associate("account", account, AccountContact1N, Refs(new EntityReference("contact", contact)));

            Assert.Equal(account, svc.Retrieve("contact", contact, new ColumnSet("parentcustomerid"))
                .GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        // ---- 1:N associate/disassociate --------------------------------------------------------------------

        private static InMemoryOrganizationService OneToMany(params Entity[] rows) => new InMemoryOrganizationService()
            .WithParentalRelationship("account", "contact", "parentcustomerid")
            .Seed(rows);

        [Fact]
        public void One_to_many_associate_and_disassociate_from_the_child_side_set_and_clear_the_childs_lookup()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = OneToMany(new Entity("account", account), new Entity("contact", contact));

            svc.Associate("contact", contact, AccountContact1N, Refs(new EntityReference("account", account)));

            Assert.Equal(account, svc.Retrieve("contact", contact, new ColumnSet("parentcustomerid"))
                .GetAttributeValue<EntityReference>("parentcustomerid").Id);
            Assert.False(svc.Retrieve("account", account, new ColumnSet(true)).Contains("parentcustomerid"));

            svc.Disassociate("contact", contact, AccountContact1N, Refs(new EntityReference("account", account)));

            Assert.Null(svc.Retrieve("contact", contact, new ColumnSet("parentcustomerid"))
                .GetAttributeValue<EntityReference>("parentcustomerid"));
        }

        [Fact]
        public void One_to_many_associate_from_a_missing_parent_faults_and_leaves_the_child_unlinked()
        {
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = OneToMany(new Entity("contact", contact));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Associate("account", Guid.NewGuid(), AccountContact1N, Refs(new EntityReference("contact", contact))));

            Assert.Equal(ObjectDoesNotExist, fault.Detail.ErrorCode);
            Assert.False(svc.Retrieve("contact", contact, new ColumnSet(true)).Contains("parentcustomerid"));
        }

        [Fact]
        public void One_to_many_disassociate_from_a_missing_parent_faults_and_keeps_the_childs_lookup()
        {
            Guid ghost = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = OneToMany(
                new Entity("contact", contact) { ["parentcustomerid"] = new EntityReference("account", ghost) });

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Disassociate("account", ghost, AccountContact1N, Refs(new EntityReference("contact", contact))));

            Assert.Equal(ObjectDoesNotExist, fault.Detail.ErrorCode);
            Assert.Equal(ghost, svc.GetAll("contact")[0].GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        // ---- Merge -----------------------------------------------------------------------------------------

        private static void Merge(InMemoryOrganizationService svc, string entity, Guid target, Guid subordinate) =>
            svc.Execute(new MergeRequest { Target = new EntityReference(entity, target), SubordinateId = subordinate });

        [Theory]
        [InlineData("account")]
        [InlineData("contact")]
        [InlineData("lead")]
        [InlineData("incident")]
        public void Merge_is_supported_on_each_mergeable_table(string entity)
        {
            Guid target = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity(entity, target), new Entity(entity, subordinate));

            Merge(svc, entity, target, subordinate);

            Entity merged = svc.Retrieve(entity, subordinate, new ColumnSet(true));
            Assert.Equal(1, merged.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(2, merged.GetAttributeValue<OptionSetValue>("statuscode").Value);
            Assert.DoesNotContain(string.Empty, merged.Attributes.Keys);
        }

        [Fact]
        public void Merge_with_a_missing_target_or_subordinate_faults_as_not_found()
        {
            Guid existing = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(new Entity("account", existing));

            FaultException<OrganizationServiceFault> missingTarget = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => Merge(svc, "account", Guid.NewGuid(), existing));
            Assert.Equal(ObjectDoesNotExist, missingTarget.Detail.ErrorCode);

            FaultException<OrganizationServiceFault> missingSubordinate = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => Merge(svc, "account", existing, Guid.NewGuid()));
            Assert.Equal(ObjectDoesNotExist, missingSubordinate.Detail.ErrorCode);
        }

        // The subordinate's primary key and state columns are never copied onto the target.
        [Fact]
        public void Merge_does_not_copy_any_subordinate_column_to_the_master()
        {
            Guid target = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(
                new Entity("account", target) { ["name"] = "Target" },
                new Entity("account", subordinate)
                {
                    ["accountid"] = subordinate,
                    ["statecode"] = new OptionSetValue(0),
                    ["statuscode"] = new OptionSetValue(7),
                    ["telephone1"] = "555",
                });

            Merge(svc, "account", target, subordinate);

            Entity merged = svc.Retrieve("account", target, new ColumnSet(true));
            Assert.False(merged.Contains("telephone1")); // verified live: the subordinate's data is not copied
            Assert.Equal(target, merged.GetAttributeValue<Guid>("accountid"));
            Assert.False(merged.Contains("statecode"));
            Assert.False(merged.Contains("statuscode"));
        }

        // ---- Sharing ---------------------------------------------------------------------------------------

        private static PrincipalAccess ShareOf(InMemoryOrganizationService svc, EntityReference target, Guid principal)
        {
            PrincipalAccess[] shares = ((RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                new RetrieveSharedPrincipalsAndAccessRequest { Target = target })).PrincipalAccesses;
            return Array.Find(shares, s => s.Principal.Id == principal);
        }

        [Fact]
        public void ModifyAccess_replaces_the_parents_rights_and_cascades_to_children()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            EntityReference accountRef = new EntityReference("account", account);
            EntityReference alice = new EntityReference("systemuser", Alice);
            EntityReference contactRef = new EntityReference("contact", contact);
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Bob)
                .WithParentalRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("sharer", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write, Privilege.Share))
                    .Role("basic", r => r
                        .Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write)
                        .Grant("contact", AccessLevel.User, Privilege.Read, Privilege.Write))
                    .User(Bob, SalesBu, "sharer")
                    .User(Alice, SalesBu, "basic"))
                .Seed(
                    new Entity("account", account) { ["ownerid"] = new EntityReference("systemuser", Bob) },
                    new Entity("contact", contact) { ["parentcustomerid"] = accountRef, ["ownerid"] = new EntityReference("systemuser", Bob) });

            svc.Execute(new GrantAccessRequest
            {
                Target = accountRef,
                PrincipalAccess = new PrincipalAccess { Principal = alice, AccessMask = AccessRights.ReadAccess },
            });
            svc.Execute(new ModifyAccessRequest
            {
                Target = accountRef,
                PrincipalAccess = new PrincipalAccess { Principal = alice, AccessMask = AccessRights.WriteAccess },
            });

            Assert.Equal(AccessRights.WriteAccess, ShareOf(svc, accountRef, Alice).AccessMask);
            // The child's inherited Read is REPLACED by Write (not unioned), and it is not an explicit share.
            Assert.Equal(AccessRights.WriteAccess, ((RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest { Target = contactRef, Principal = alice })).AccessRights);
            Assert.Null(ShareOf(svc, contactRef, Alice));
        }

        [Fact]
        public void AddUserToRecordTeam_returns_an_access_team_id_and_requires_a_registered_template()
        {
            Guid account = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess)
                .Seed(new Entity("account", account));

            AddUserToRecordTeamResponse response = (AddUserToRecordTeamResponse)svc.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", account), SystemUserId = Alice, TeamTemplateId = template,
            });
            Assert.NotEqual(Guid.Empty, response.AccessTeamId);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => svc.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", account), SystemUserId = Alice, TeamTemplateId = Guid.NewGuid(),
            }));
            Assert.Contains("WithAccessTeamTemplate", ex.Message);
        }

        // ---- Security demands ------------------------------------------------------------------------------

        private static Entity OwnedBy(string entity, Guid id, Guid owner) => new Entity(entity, id)
        {
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static InMemoryOrganizationService Secured(Action<RoleBuilder> role, Func<SecurityConfiguration, SecurityConfiguration>? extra = null) =>
            new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s =>
                {
                    SecurityConfiguration config = s.BusinessUnit(SalesBu)
                        .Role("role", role)
                        .User(Alice, SalesBu, "role")
                        .User(Bob, SalesBu, "role");
                    extra?.Invoke(config);
                });

        [Fact]
        public void Create_with_a_lookup_requires_append_and_appendto()
        {
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = Secured(r => r
                    .Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read)
                    .Grant("contact", AccessLevel.Organization, Privilege.Read))
                .Seed(OwnedBy("contact", contact, Alice));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Create(
                new Entity("account") { ["primarycontactid"] = new EntityReference("contact", contact) }));
            Assert.Empty(svc.GetAll("account"));
        }

        [Fact]
        public void Create_with_a_secured_column_requires_field_update_permission()
        {
            InMemoryOrganizationService svc = Secured(
                r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read),
                s => s.SecureColumn("account", "creditlimit"));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Create(
                new Entity("account") { ["creditlimit"] = new Money(1m) }));
            Assert.Empty(svc.GetAll("account"));
        }

        // Changing the owner AND another column needs Write as well as Assign.
        [Fact]
        public void Update_of_owner_plus_another_column_requires_write()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = Secured(r => r
                    .Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Assign))
                .Seed(OwnedBy("account", account, Alice));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Update(new Entity("account", account)
            {
                ["ownerid"] = new EntityReference("systemuser", Bob),
                ["name"] = "renamed",
            }));
            Assert.Equal(Alice, svc.GetAll("account")[0].GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void RevokeAccess_requires_the_share_privilege()
        {
            Guid account = Guid.NewGuid();
            EntityReference accountRef = new EntityReference("account", account);
            InMemoryOrganizationService svc = Secured(r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .Seed(OwnedBy("account", account, Alice))
                .WithSharedRecord(accountRef, new EntityReference("systemuser", Bob), AccessRights.ReadAccess);

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(
                new RevokeAccessRequest { Target = accountRef, Revokee = new EntityReference("systemuser", Bob) }));
            Assert.NotNull(ShareOf(svc, accountRef, Bob));
        }

        [Fact]
        public void ModifyAccess_requires_the_share_privilege()
        {
            Guid account = Guid.NewGuid();
            EntityReference accountRef = new EntityReference("account", account);
            EntityReference bob = new EntityReference("systemuser", Bob);
            InMemoryOrganizationService svc = Secured(r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write))
                .Seed(OwnedBy("account", account, Alice))
                .WithSharedRecord(accountRef, bob, AccessRights.ReadAccess);

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(new ModifyAccessRequest
            {
                Target = accountRef,
                PrincipalAccess = new PrincipalAccess { Principal = bob, AccessMask = AccessRights.WriteAccess },
            }));
            Assert.Equal(AccessRights.ReadAccess, ShareOf(svc, accountRef, Bob).AccessMask);
        }

        [Fact]
        public void RemoveUserFromRecordTeam_requires_appendto_on_the_record()
        {
            Guid account = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = Secured(r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess)
                .Seed(OwnedBy("account", account, Alice));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(new RemoveUserFromRecordTeamRequest
            {
                Record = new EntityReference("account", account), SystemUserId = Bob, TeamTemplateId = template,
            }));
        }

        [Fact]
        public void Strict_security_without_a_model_fails_many_to_many_associate_and_disassociate()
        {
            Guid account = Guid.NewGuid();
            Guid contact = Guid.NewGuid();
            InMemoryOrganizationService svc = ManyToMany()
                .Seed(new Entity("account", account), new Entity("contact", contact))
                .WithStrictSecurity();

            Assert.Throws<InvalidOperationException>(
                () => svc.Associate("account", account, AccountContacts, Refs(new EntityReference("contact", contact))));
            Assert.Empty(svc.GetAll("account_contact"));
            Assert.Throws<InvalidOperationException>(
                () => svc.Disassociate("account", account, AccountContacts, Refs(new EntityReference("contact", contact))));
        }
    }
}
