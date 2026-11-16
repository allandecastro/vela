using System;
using System.ServiceModel;
using System.Threading.Tasks;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Pins the edges of the security harness the behavioural suites leave open: the configuration error messages,
    // hierarchy access through a team share and the nearer of two hierarchy paths, a cyclic business-unit tree, and
    // the rollback of shares/access-team grants that existed before a failed transaction.
    public sealed class SecurityModelMutationTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000b01"); // manager
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000b02");   // report
        private static readonly Guid Middle = new Guid("3dd1e000-0000-0000-0000-000000000b03"); // between Alice and Bob
        private static readonly Guid Stranger = new Guid("57a11000-0000-0000-0000-000000000b04");
        private static readonly Guid Crew = new Guid("7ea11000-0000-0000-0000-000000000b05");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-000000000bb1");
        private static readonly Guid SubBu = new Guid("b0000000-0000-0000-0000-000000000bb2");
        private static readonly Guid OtherBu = new Guid("b0000000-0000-0000-0000-000000000bb3");
        private static readonly Guid CeoPos = new Guid("9051d000-0000-0000-0000-000000000bc1");
        private static readonly Guid SalesPos = new Guid("9051d000-0000-0000-0000-000000000bc2");
        private static readonly Guid Template = new Guid("7e770000-0000-0000-0000-000000000b99");

        private static Entity AccountOwnedBy(Guid id, Guid owner, Guid bu) => new Entity("account", id)
        {
            ["name"] = "seed",
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", bu),
        };

        private static AccessRights PrincipalAccess(IOrganizationService svc, Guid record, Guid principal) =>
            ((RetrievePrincipalAccessResponse)svc.Execute(new RetrievePrincipalAccessRequest
            {
                Target = new EntityReference("account", record),
                Principal = new EntityReference("systemuser", principal),
            })).AccessRights;

        // ---- Configuration errors name what is wrong -------------------------------------------------------

        [Fact]
        public void A_blank_entity_in_a_role_grant_is_rejected_with_its_reason()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s
                    .Role("r", r => r.Grant(" ", AccessLevel.User, Privilege.Read))));

            Assert.Equal("entity", exception.ParamName);
            Assert.StartsWith("Entity logical name is required.", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_business_unit_id_is_rejected_with_its_reason()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.BusinessUnit(Guid.Empty)));

            Assert.Equal("id", exception.ParamName);
            Assert.StartsWith("Business unit id is required.", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_blank_role_name_is_rejected_with_its_reason_by_both_role_overloads()
        {
            ArgumentException handWritten = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.Role(" ", r => { })));
            ArgumentException generated = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.Role(" ", Guid.NewGuid(), ("account", "User", "Read"))));

            Assert.Equal("name", handWritten.ParamName);
            Assert.StartsWith("Role name is required.", handWritten.Message, StringComparison.Ordinal);
            Assert.Equal("name", generated.ParamName);
            Assert.StartsWith("Role name is required.", generated.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unknown_generated_access_level_names_the_level_and_the_role()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.Role("gen", Guid.NewGuid(), ("account", "Everything", "Read"))));

            Assert.Equal("grants", exception.ParamName);
            Assert.StartsWith("Unknown access level 'Everything' in role 'gen'.", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unknown_generated_privilege_names_the_privilege_and_the_role()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.Role("gen", Guid.NewGuid(), ("account", "User", "Fly"))));

            Assert.Equal("grants", exception.ParamName);
            Assert.StartsWith("Unknown privilege 'Fly' in role 'gen'.", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Configuring_both_hierarchy_models_explains_why()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.ManagerHierarchy(1).PositionHierarchy(1)));

            Assert.Equal(
                "Configure either ManagerHierarchy or PositionHierarchy, not both — Dataverse activates only one hierarchy model at a time.",
                exception.Message);
        }

        [Fact]
        public void An_undefined_role_on_a_user_names_the_user()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu, "ghost")));

            Assert.Equal(
                $"User {Alice} references role 'ghost', which is not defined. Declare it with Role(\"ghost\", ...).",
                exception.Message);
        }

        [Fact]
        public void An_undefined_role_on_a_team_names_the_team()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                new InMemoryOrganizationService().WithSecurity(s => s.BusinessUnit(SalesBu).Team(Crew, SalesBu, "ghost")));

            Assert.Equal(
                $"Team {Crew} references role 'ghost', which is not defined. Declare it with Role(\"ghost\", ...).",
                exception.Message);
        }

        // ---- Configuration validation ----------------------------------------------------------------------

        private static InvalidOperationException ConfigurationError(Action<SecurityConfiguration> configure) =>
            Assert.Throws<InvalidOperationException>(() => new InMemoryOrganizationService().WithSecurity(configure));

        [Fact]
        public void A_user_in_an_undeclared_business_unit_fails_once_business_units_are_declared()
        {
            InvalidOperationException exception = ConfigurationError(s => s.BusinessUnit(SalesBu).User(Alice, SubBu));

            Assert.Equal(
                $"User {Alice} is in business unit {SubBu}, which is not declared. Declare it with BusinessUnit({SubBu}, ...).",
                exception.Message);
        }

        [Fact]
        public void A_team_in_an_undeclared_business_unit_fails_once_business_units_are_declared()
        {
            InvalidOperationException exception = ConfigurationError(s => s.BusinessUnit(SalesBu).Team(Crew, SubBu));

            Assert.StartsWith($"Team {Crew} is in business unit {SubBu}", exception.Message);
        }

        [Fact]
        public void A_business_unit_declared_only_as_a_parent_counts_as_declared()
        {
            new InMemoryOrganizationService().WithSecurity(s => s.BusinessUnit(SubBu, SalesBu).User(Alice, SalesBu));
        }

        [Fact]
        public void Business_units_are_not_checked_when_none_is_declared()
        {
            new InMemoryOrganizationService().WithSecurity(s => s.User(Alice, SalesBu).Team(Crew, SubBu));
        }

        [Fact]
        public void A_manager_link_naming_an_unknown_user_fails()
        {
            InvalidOperationException exception = ConfigurationError(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu).Manager(Alice, Bob));

            Assert.Equal($"Manager names {Bob} as the manager, which is not a declared user. Declare it with User({Bob}, ...).", exception.Message);
            Assert.Contains("as the user", ConfigurationError(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu).Manager(Bob, Alice)).Message);
        }

        [Fact]
        public void A_position_on_an_unknown_user_fails()
        {
            InvalidOperationException exception = ConfigurationError(s => s.BusinessUnit(SalesBu).Position(Bob, Guid.NewGuid()));

            Assert.Equal($"Position names {Bob} as the user, which is not a declared user. Declare it with User({Bob}, ...).", exception.Message);
        }

        [Fact]
        public void A_field_permission_for_an_unknown_principal_fails()
        {
            InvalidOperationException exception = ConfigurationError(s => s
                .BusinessUnit(SalesBu)
                .SecureColumn("account", "creditlimit")
                .FieldPermission(Bob, "account", "creditlimit", read: false, create: true));

            Assert.Equal(
                $"FieldPermission names {Bob}, which is neither a declared user nor a declared team. Declare it with User({Bob}, ...) or Team({Bob}, ...).",
                exception.Message);
        }

        [Fact]
        public void A_field_permission_for_a_declared_team_is_accepted()
        {
            new InMemoryOrganizationService().WithSecurity(s => s
                .BusinessUnit(SalesBu)
                .Team(Crew, SalesBu)
                .FieldPermission(Crew, "account", "creditlimit"));
        }

        [Fact]
        public void Two_roles_with_the_same_id_fail()
        {
            Guid id = Guid.NewGuid();
            InvalidOperationException exception = ConfigurationError(s => s
                .Role("first", id, r => r.Grant("account", AccessLevel.User, Privilege.Read))
                .Role("second", id, r => r.Grant("contact", AccessLevel.User, Privilege.Read)));

            Assert.Equal($"Roles 'first' and 'second' have the same id {id}. Give each role its own id.", exception.Message);
        }

        [Fact]
        public void A_user_id_equal_to_a_team_id_fails()
        {
            InvalidOperationException exception = ConfigurationError(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu).Team(Alice, SalesBu));

            Assert.Equal(
                $"{Alice} is declared both as a user and as a team. A principal id identifies one record, a systemuser or a team.",
                exception.Message);
        }

        // ---- Hierarchy -------------------------------------------------------------------------------------

        // Alice (SalesBu) manages Bob (SubBu, a child of SalesBu); Alice holds only User-level Read on accounts, so
        // a record she does not own is reachable only through the hierarchy.
        private static SecurityConfiguration ManagerOfBob(SecurityConfiguration s) => s
            .BusinessUnit(SalesBu)
            .BusinessUnit(SubBu, SalesBu)
            .BusinessUnit(OtherBu)
            .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read))
            .User(Alice, SalesBu, "basic")
            .User(Bob, SubBu)
            .User(Stranger, OtherBu);

        [Fact]
        public void A_manager_reaches_a_record_shared_with_a_team_of_a_direct_report()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerOfBob(s).Team(Crew, SubBu).Member(Bob, Crew).Manager(Bob, Alice).ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Stranger, OtherBu))
                .WithSharedRecord(new EntityReference("account", record), new EntityReference("team", Crew), AccessRights.ReadAccess);

            // Bob reaches the record only as a member of the team it is shared with; his manager reaches it through him.
            Assert.Equal("seed", svc.Retrieve("account", record, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void A_shared_delete_on_a_direct_report_is_not_conferred_on_the_manager()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerOfBob(s).Manager(Bob, Alice).ManagerHierarchy(1))
                .Seed(AccountOwnedBy(record, Stranger, OtherBu))
                .WithSharedRecord(new EntityReference("account", record), new EntityReference("systemuser", Bob),
                    AccessRights.ReadAccess | AccessRights.DeleteAccess);

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet("name"))); // read flows up the hierarchy
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Delete("account", record)); // delete never does
            Assert.Single(svc.GetAll("account"));
        }

        [Fact]
        public void The_nearer_of_the_manager_and_position_paths_decides_direct_report_write()
        {
            // By the manager chain Bob is two levels below Alice (read-only); by position he is directly below her,
            // so the nearer path (distance 1) confers write.
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => ManagerOfBob(s)
                    .User(Middle, SubBu)
                    .Manager(Bob, Middle)
                    .Manager(Middle, Alice)
                    .Position(Alice, CeoPos)
                    .Position(Bob, SalesPos)
                    .PositionParent(SalesPos, CeoPos)
                    .ManagerHierarchy(2))
                .Seed(AccountOwnedBy(record, Bob, SubBu));

            svc.Update(new Entity("account", record) { ["name"] = "edited by a direct-position manager" });

            Assert.Equal(
                "edited by a direct-position manager",
                svc.Retrieve("account", record, new ColumnSet("name")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public async Task A_cyclic_business_unit_tree_denies_deep_access_instead_of_looping()
        {
            // SubBu and OtherBu name each other as parent; Alice's Deep grant from SalesBu never reaches either.
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(SubBu, OtherBu)
                    .BusinessUnit(OtherBu, SubBu)
                    .Role("deep", r => r.Grant("account", AccessLevel.ParentChild, Privilege.Read))
                    .User(Alice, SalesBu, "deep")
                    .User(Stranger, SubBu))
                .Seed(AccountOwnedBy(record, Stranger, SubBu));

            // Run off the test thread with a deadline, so a mutant that loops on the cycle fails the test instead of
            // hanging the run.
            Task<Exception?> read = Task.Run<Exception?>(() => Record.Exception(() => svc.Retrieve("account", record, new ColumnSet("name"))));

            Task finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(finished == read, "the business-unit walk did not terminate on a cycle");
            Assert.IsType<FaultException<OrganizationServiceFault>>(await read);
        }

        // ---- Rollback restores what existed before the transaction ------------------------------------------

        [Fact]
        public void A_failed_transaction_keeps_the_shares_and_access_team_grants_that_predate_it()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(Template, AccessRights.AppendToAccess)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization,
                        Privilege.Create, Privilege.Read, Privilege.Write, Privilege.Delete,
                        Privilege.Share, Privilege.Assign, Privilege.Append, Privilege.AppendTo))
                    .User(Alice, SalesBu, "admin")
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read, Privilege.Write)) // a share needs the table privilege
                    .User(Bob, SalesBu, "basic")
                    .User(Stranger, SalesBu))
                .Seed(AccountOwnedBy(record, Alice, SalesBu))
                .WithSharedRecord(new EntityReference("account", record), new EntityReference("systemuser", Bob), AccessRights.ReadAccess);
            svc.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", record),
                SystemUserId = Stranger,
                TeamTemplateId = Template,
            });

            ExecuteTransactionRequest tx = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            tx.Requests.Add(new GrantAccessRequest
            {
                Target = new EntityReference("account", record),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", Bob), AccessMask = AccessRights.WriteAccess },
            });
            tx.Requests.Add(new DeleteRequest { Target = new EntityReference("account", Guid.NewGuid()) }); // does not exist -> throws

            Assert.ThrowsAny<Exception>(() => svc.Execute(tx));

            // The rollback undoes the transaction's own share, and restores - not drops - what was there before.
            Assert.Equal(AccessRights.ReadAccess, PrincipalAccess(svc, record, Bob));
            Assert.Equal(AccessRights.AppendToAccess, PrincipalAccess(svc, record, Stranger));
        }
    }
}
