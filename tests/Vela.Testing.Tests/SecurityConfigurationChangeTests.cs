using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // The security model keeps reading the configuration's users, teams, memberships and roles (grants and member
    // privilege inheritance included) after WithSecurity, and memoizes the grants it resolves. A change made to the
    // configuration afterwards must reach the next privilege check: each test checks once (which fills the memo),
    // changes the configuration, and checks again.
    public sealed class SecurityConfigurationChangeTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-0000000000c1");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-0000000000c2");
        private static readonly Guid Team = new Guid("7ea11000-0000-0000-0000-0000000000c3");
        private static readonly Guid Bu = new Guid("b0000000-0000-0000-0000-0000000000c4");
        private static readonly Guid AccountId = new Guid("acc00000-0000-0000-0000-0000000000c5");

        // A record owned by Bob in the shared business unit: Alice reads it only with Read at BusinessUnit depth or more.
        private static InMemoryOrganizationService Service(Action<SecurityConfiguration> configure)
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(configure);
            service.Seed(new Entity("account", AccountId)
            {
                ["ownerid"] = new EntityReference("systemuser", Bob),
                ["owningbusinessunit"] = new EntityReference("businessunit", Bu),
            });
            return service;
        }

        private static bool CanRead(InMemoryOrganizationService service) => CanRead(service, AccountId);

        private static bool CanRead(InMemoryOrganizationService service, Guid accountId)
        {
            try
            {
                service.Retrieve("account", accountId, new ColumnSet(true));
                return true;
            }
            catch (FaultException<OrganizationServiceFault>)
            {
                return false;
            }
        }

        [Fact]
        public void A_grant_added_to_a_role_after_WithSecurity_applies_to_the_next_check()
        {
            RoleBuilder? role = null;
            InMemoryOrganizationService service = Service(s => s
                .BusinessUnit(Bu)
                .Role("reader", r => role = r.Grant("contact", AccessLevel.Organization, Privilege.Read))
                .User(Alice, Bu, "reader")
                .User(Bob, Bu));

            Assert.False(CanRead(service));

            role!.Grant("account", AccessLevel.BusinessUnit, Privilege.Read);

            Assert.True(CanRead(service));
        }

        [Fact]
        public void A_membership_added_after_WithSecurity_applies_to_the_next_check()
        {
            SecurityConfiguration? configuration = null;
            InMemoryOrganizationService service = Service(s => configuration = s
                .BusinessUnit(Bu)
                .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .Team(Team, Bu, "reader")
                .User(Alice, Bu)
                .User(Bob, Bu));

            Assert.False(CanRead(service));

            configuration!.Member(Alice, Team);

            Assert.True(CanRead(service));
        }

        [Fact]
        public void A_user_redeclared_with_a_role_after_WithSecurity_applies_to_the_next_check()
        {
            SecurityConfiguration? configuration = null;
            InMemoryOrganizationService service = Service(s => configuration = s
                .BusinessUnit(Bu)
                .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .User(Alice, Bu)
                .User(Bob, Bu));

            Assert.False(CanRead(service));

            configuration!.User(Alice, Bu, "reader");

            Assert.True(CanRead(service));
        }

        [Fact]
        public void A_team_redeclared_with_a_role_after_WithSecurity_applies_to_its_members_next_check()
        {
            SecurityConfiguration? configuration = null;
            InMemoryOrganizationService service = Service(s => configuration = s
                .BusinessUnit(Bu)
                .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .Team(Team, Bu)
                .User(Alice, Bu)
                .User(Bob, Bu)
                .Member(Alice, Team));

            Assert.False(CanRead(service));

            configuration!.Team(Team, Bu, "reader");

            Assert.True(CanRead(service));
        }

        [Fact]
        public void A_role_declared_after_WithSecurity_then_assigned_applies_to_the_next_check()
        {
            SecurityConfiguration? configuration = null;
            InMemoryOrganizationService service = Service(s => configuration = s
                .BusinessUnit(Bu)
                .User(Alice, Bu)
                .User(Bob, Bu));

            Assert.False(CanRead(service));

            configuration!
                .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read))
                .User(Alice, Bu, "reader");

            Assert.True(CanRead(service));
        }

        [Fact]
        public void A_role_redefined_after_WithSecurity_applies_to_the_next_check()
        {
            SecurityConfiguration? configuration = null;
            InMemoryOrganizationService service = Service(s => configuration = s
                .BusinessUnit(Bu)
                .Role("reader", r => r.Grant("contact", AccessLevel.Organization, Privilege.Read))
                .User(Alice, Bu, "reader")
                .User(Bob, Bu));

            Assert.False(CanRead(service));

            configuration!.Role("reader", r => r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read));

            Assert.True(CanRead(service));

            configuration.Role("reader", r => r.Grant("contact", AccessLevel.Organization, Privilege.Read));

            Assert.False(CanRead(service));
        }

        [Fact]
        public void A_member_privilege_inheritance_switched_after_WithSecurity_applies_to_the_next_check()
        {
            // Alice holds a User-level Read only through her team. With "member and team" it covers the records she
            // owns herself; with "team privileges only" it covers only the team's records, so her own one drops out.
            Guid ownRecord = new Guid("acc00000-0000-0000-0000-0000000000c6");
            RoleBuilder? role = null;
            InMemoryOrganizationService service = Service(s => s
                .BusinessUnit(Bu)
                .Role("reader", r => role = r.Grant("account", AccessLevel.User, Privilege.Read))
                .Team(Team, Bu, "reader")
                .User(Alice, Bu)
                .User(Bob, Bu)
                .Member(Alice, Team));
            service.Seed(new Entity("account", ownRecord)
            {
                ["ownerid"] = new EntityReference("systemuser", Alice),
                ["owningbusinessunit"] = new EntityReference("businessunit", Bu),
            });

            Assert.True(CanRead(service, ownRecord));

            role!.MemberPrivilegeInheritance(TeamPrivilegeInheritance.TeamOnly);

            Assert.False(CanRead(service, ownRecord));

            role.MemberPrivilegeInheritance(TeamPrivilegeInheritance.MemberAndTeam);

            Assert.True(CanRead(service, ownRecord));
        }
    }
}
