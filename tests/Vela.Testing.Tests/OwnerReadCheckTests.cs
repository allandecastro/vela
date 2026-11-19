using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity (verified live): a record's owner must hold Read on its table. Create with ownerid, an Update of
    // ownerid, Assign and a PreOperation plugin writing Target["ownerid"] all fail with 0x80040299, or 0x80042f0a for
    // an owner team with no role on Update/Assign, and an Update writes nothing. Read at User level is enough, except
    // for a user owner whose Read comes only from a "team privileges only" team: its level must reach the owner's
    // business unit from the team's.
    public sealed class OwnerReadCheckTests
    {
        private const int TargetPrincipalLacksReadPrivilege = unchecked((int)0x80040299);
        private const int TeamNotAssignedRoles = unchecked((int)0x80042f0a);

        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000c01");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000c02");
        private static readonly Guid NoRoleTeam = new Guid("7ea00000-0000-0000-0000-000000000c03");
        private static readonly Guid NoReadTeam = new Guid("7ea00000-0000-0000-0000-000000000c04");
        private static readonly Guid ReaderTeam = new Guid("7ea00000-0000-0000-0000-000000000c05");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-000000000cb1");

        private static Action<SecurityConfiguration> Security => s => s
            .BusinessUnit(SalesBu)
            .Role("admin", r => r.Grant("account", AccessLevel.Organization,
                Privilege.Create, Privilege.Read, Privilege.Write, Privilege.Assign))
            .Role("reader", r => r.Grant("account", AccessLevel.User, Privilege.Read))
            .Role("contacts", r => r.Grant("contact", AccessLevel.Organization, Privilege.Read))
            .User(Alice, SalesBu, "admin")
            .User(Bob, SalesBu, "reader")
            .Team(NoRoleTeam, SalesBu)
            .Team(NoReadTeam, SalesBu, "contacts")
            .Team(ReaderTeam, SalesBu, TeamPrivilegeInheritance.TeamOnly, "reader");

        private static EntityReference TeamRef(Guid id) => new EntityReference("team", id);

        private static Entity AccountOwnedByAlice(Guid id) => new Entity("account", id)
        {
            ["name"] = "seed",
            ["ownerid"] = new EntityReference("systemuser", Alice),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static InMemoryOrganizationService Store(Guid seeded) =>
            new InMemoryOrganizationService().WithCurrentUser(Alice).WithSecurity(Security).Seed(AccountOwnedByAlice(seeded));

        private static OrganizationServiceFault Fault(Action action) =>
            Assert.Throws<FaultException<OrganizationServiceFault>>(action).Detail;

        // --- Create ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("7ea00000-0000-0000-0000-000000000c03")] // no role at all: still 0x80040299 on Create
        [InlineData("7ea00000-0000-0000-0000-000000000c04")] // roles, but no Read on account
        public void Create_for_an_owner_without_read_fails(string team)
        {
            InMemoryOrganizationService svc = Store(Guid.NewGuid());

            OrganizationServiceFault fault = Fault(() => svc.Create(new Entity("account") { ["ownerid"] = TeamRef(new Guid(team)) }));

            Assert.Equal(TargetPrincipalLacksReadPrivilege, fault.ErrorCode);
            Assert.StartsWith("Read Privilege Check For Owner failed with exception: Principal team (Id=" + team + ")", fault.Message);
            Assert.Contains("prvReadaccount", fault.Message);
            Assert.Single(svc.GetAll("account")); // only the seed
        }

        [Fact]
        public void Create_for_an_owner_with_user_level_read_succeeds()
        {
            InMemoryOrganizationService svc = Store(Guid.NewGuid());

            svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", Bob) });
            svc.Create(new Entity("account") { ["ownerid"] = TeamRef(ReaderTeam) }); // inheritance mode does not matter

            Assert.Equal(3, svc.GetAll("account").Count);
        }

        // Verified live: the level is counted from the team's business unit. Same business unit: Business Unit and
        // Parent: Child suffice. Team in a child business unit of the owner's: only Organization does.
        [Theory]
        [InlineData(AccessLevel.User, false, false)]
        [InlineData(AccessLevel.BusinessUnit, false, true)]
        [InlineData(AccessLevel.ParentChild, false, true)]
        [InlineData(AccessLevel.Organization, false, true)]
        [InlineData(AccessLevel.User, true, false)]
        [InlineData(AccessLevel.BusinessUnit, true, false)]
        [InlineData(AccessLevel.ParentChild, true, false)]
        [InlineData(AccessLevel.Organization, true, true)]
        public void A_user_owner_whose_read_comes_from_a_team_privileges_only_team_needs_a_level_reaching_its_business_unit(AccessLevel level, bool teamInChildBu, bool succeeds)
        {
            Guid carol = new Guid("ca401000-0000-0000-0000-000000000c06");
            Guid team = new Guid("7ea00000-0000-0000-0000-000000000c07");
            Guid childBu = new Guid("b0000000-0000-0000-0000-000000000cb2");
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .BusinessUnit(childBu, SalesBu)
                    .Role("admin", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read, Privilege.Write, Privilege.Assign))
                    .Role("teamreader", r => r.Grant("account", level, Privilege.Read))
                    .User(Alice, SalesBu, "admin")
                    .User(carol, SalesBu)
                    .Team(team, teamInChildBu ? childBu : SalesBu, TeamPrivilegeInheritance.TeamOnly, "teamreader")
                    .Member(carol, team));

            Action create = () => svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", carol) });

            if (succeeds)
            {
                create();
                Assert.Single(svc.GetAll("account"));
            }
            else
            {
                Assert.Equal(TargetPrincipalLacksReadPrivilege, Fault(create).ErrorCode);
                Assert.Empty(svc.GetAll("account"));
            }
        }

        [Fact]
        public void Owners_unknown_to_the_security_model_are_not_checked()
        {
            InMemoryOrganizationService svc = Store(Guid.NewGuid());

            svc.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()) });

            Assert.Equal(2, svc.GetAll("account").Count);
        }

        // --- Update / Assign ------------------------------------------------------------------------

        [Fact]
        public void Update_of_ownerid_to_an_owner_without_read_fails_and_writes_nothing()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(record);

            OrganizationServiceFault fault = Fault(() => svc.Update(new Entity("account", record)
            {
                ["name"] = "changed",
                ["ownerid"] = TeamRef(NoReadTeam),
            }));

            Assert.Equal(TargetPrincipalLacksReadPrivilege, fault.ErrorCode);
            Assert.StartsWith("Principal team (Id=" + NoReadTeam + ") is missing prvReadaccount privilege", fault.Message);
            Entity stored = Assert.Single(svc.GetAll("account"));
            Assert.Equal("seed", stored.GetAttributeValue<string>("name"));
            Assert.Equal(Alice, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Update_of_ownerid_to_a_team_with_no_role_fails_with_team_not_assigned_roles()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(record);

            OrganizationServiceFault fault = Fault(() => svc.Update(new Entity("account", record) { ["ownerid"] = TeamRef(NoRoleTeam) }));

            Assert.Equal(TeamNotAssignedRoles, fault.ErrorCode);
        }

        [Fact]
        public void Assign_to_an_owner_without_read_fails()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(record);

            Assert.Equal(TeamNotAssignedRoles, Fault(() => svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = TeamRef(NoRoleTeam),
            })).ErrorCode);
            Assert.Equal(TargetPrincipalLacksReadPrivilege, Fault(() => svc.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = TeamRef(NoReadTeam),
            })).ErrorCode);
            Assert.Equal(Alice, Assert.Single(svc.GetAll("account")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Assign_and_update_to_an_owner_with_user_level_read_succeed()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = Store(record);

            svc.Execute(new AssignRequest { Target = new EntityReference("account", record), Assignee = TeamRef(ReaderTeam) });
            Assert.Equal(ReaderTeam, Assert.Single(svc.GetAll("account")).GetAttributeValue<EntityReference>("ownerid").Id);

            svc.Update(new Entity("account", record) { ["ownerid"] = new EntityReference("systemuser", Bob) });
            Assert.Equal(Bob, Assert.Single(svc.GetAll("account")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Pipeline_assign_to_an_owner_without_read_fails()
        {
            Guid record = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().WithCurrentUser(Alice).WithSecurity(Security).Seed(AccountOwnedByAlice(record));

            Assert.Equal(TeamNotAssignedRoles, Fault(() => pipeline.Service.Execute(new AssignRequest
            {
                Target = new EntityReference("account", record),
                Assignee = TeamRef(NoRoleTeam),
            })).ErrorCode);
            Assert.Equal(Alice, Assert.Single(pipeline.GetAll("account")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        // --- A PreOperation plugin setting Target["ownerid"]: checked on the final Target --------------

        [Fact]
        public void PreOperation_owner_without_read_fails_a_create()
        {
            OrcSetOwnerOnCreate.Owner = TeamRef(NoReadTeam);
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrcSetOwnerOnCreate>().WithCurrentUser(Alice).WithSecurity(Security);

            OrganizationServiceFault fault = Fault(() => pipeline.Service.Create(new FakeAccount { Name = "new" }));

            Assert.Equal(TargetPrincipalLacksReadPrivilege, fault.ErrorCode);
            Assert.Empty(pipeline.GetAll("account"));
        }

        [Fact]
        public void PreOperation_owner_with_user_level_read_creates_the_record()
        {
            OrcSetOwnerOnCreate.Owner = TeamRef(ReaderTeam);
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrcSetOwnerOnCreate>().WithCurrentUser(Alice).WithSecurity(Security);

            pipeline.Service.Create(new FakeAccount { Name = "new" });

            Assert.Equal(ReaderTeam, Assert.Single(pipeline.GetAll("account")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Theory]
        [InlineData("7ea00000-0000-0000-0000-000000000c03", unchecked((int)0x80042f0a))] // no role at all
        [InlineData("7ea00000-0000-0000-0000-000000000c04", unchecked((int)0x80040299))] // no Read on account
        public void PreOperation_owner_without_read_fails_an_update_atomically(string team, int expectedCode)
        {
            Guid record = Guid.NewGuid();
            OrcSetOwnerOnUpdate.Owner = TeamRef(new Guid(team));
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrcSetOwnerOnUpdate>()
                .WithCurrentUser(Alice).WithSecurity(Security).Seed(AccountOwnedByAlice(record));

            OrganizationServiceFault fault = Fault(() => pipeline.Service.Update(new FakeAccount { Id = record, Name = "changed" }));

            Assert.Equal(expectedCode, fault.ErrorCode);
            Assert.DoesNotContain("Read Privilege Check For Owner", fault.Message);
            Entity stored = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal("seed", stored.GetAttributeValue<string>("name")); // nothing written
            Assert.Equal(Alice, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void PreOperation_owner_with_user_level_read_updates_the_record()
        {
            Guid record = Guid.NewGuid();
            OrcSetOwnerOnUpdate.Owner = new EntityReference("systemuser", Bob);
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrcSetOwnerOnUpdate>()
                .WithCurrentUser(Alice).WithSecurity(Security).Seed(AccountOwnedByAlice(record));

            pipeline.Service.Update(new FakeAccount { Id = record, Name = "changed" });

            Entity stored = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal("changed", stored.GetAttributeValue<string>("name"));
            Assert.Equal(Bob, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        // --- SYSTEM: the check is on the owner, not the caller (verified live through ctx.SystemService) -----

        [Theory]
        [InlineData("7ea00000-0000-0000-0000-000000000c03", false, unchecked((int)0x80040299))] // Create, no role
        [InlineData("7ea00000-0000-0000-0000-000000000c04", false, unchecked((int)0x80040299))] // Create, no Read
        [InlineData("7ea00000-0000-0000-0000-000000000c03", true, unchecked((int)0x80042f0a))]  // Assign, no role
        [InlineData("7ea00000-0000-0000-0000-000000000c04", true, unchecked((int)0x80040299))]  // Assign, no Read
        public void System_service_does_not_bypass_the_owner_read_check(string team, bool assign, int expectedCode)
        {
            OrcSystemSetsOwner.Owner = TeamRef(new Guid(team));
            OrcSystemSetsOwner.Assign = assign;
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrcSystemSetsOwner>().WithCurrentUser(Alice).WithSecurity(Security);

            OrganizationServiceFault fault = Fault(() => pipeline.Service.Create(new FakeAccount { Name = "trigger" }));

            Assert.Equal(expectedCode, fault.ErrorCode);
            Assert.Empty(pipeline.GetAll("account"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void System_service_can_hand_a_record_to_an_owner_with_read(bool assign)
        {
            OrcSystemSetsOwner.Owner = TeamRef(ReaderTeam);
            OrcSystemSetsOwner.Assign = assign;
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OrcSystemSetsOwner>().WithCurrentUser(Alice).WithSecurity(Security);

            pipeline.Service.Create(new FakeAccount { Name = "trigger" });

            Assert.Contains(pipeline.GetAll("account"), a => a.GetAttributeValue<EntityReference>("ownerid")?.Id == ReaderTeam);
        }
    }

    // Account post-create: as SYSTEM, assigns the new record to Owner, or creates another account owned by Owner.
    public sealed class OrcSystemSetsOwner : EntityPluginBase<FakeAccount>
    {
        public static EntityReference? Owner;
        public static bool Assign;

        public OrcSystemSetsOwner(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    if (Assign)
                    {
                        ctx.SystemService.Execute(new AssignRequest { Target = new EntityReference("account", ctx.PrimaryEntityId), Assignee = Owner });
                    }
                    else if (ctx.GetTarget<FakeAccount>().Name == "trigger")
                    {
                        ctx.SystemService.Create(new Entity("account") { ["name"] = "system-created", ["ownerid"] = Owner });
                    }
                });
    }

    // Account pre-create: hands the record to Owner.
    public sealed class OrcSetOwnerOnCreate : EntityPluginBase<FakeAccount>
    {
        public static EntityReference? Owner;

        public OrcSetOwnerOnCreate(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => ctx.GetTarget<FakeAccount>()["ownerid"] = Owner);
    }

    // Account pre-update: hands the record to Owner.
    public sealed class OrcSetOwnerOnUpdate : EntityPluginBase<FakeAccount>
    {
        public static EntityReference? Owner;

        public OrcSetOwnerOnUpdate(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => ctx.GetTarget<FakeAccount>()["ownerid"] = Owner);
    }
}
