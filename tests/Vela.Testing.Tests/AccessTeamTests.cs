using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Access teams: adding a user to a record's access team (from a template) grants that user the template's
    // rights on that record — unioned with role and shared access, and removed per template.
    public sealed class AccessTeamTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000801");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000802");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000008b1");

        private static Entity AccountOwnedBy(Guid id, Guid owner) => new Entity("account", id)
        {
            ["ownerid"] = new EntityReference("systemuser", owner),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static void AddToTeam(InMemoryOrganizationService svc, Guid record, Guid user, Guid template) =>
            svc.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", record),
                SystemUserId = user,
                TeamTemplateId = template,
            });

        [Fact]
        public void Adding_a_user_to_a_record_access_team_grants_the_template_rights()
        {
            Guid record = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess | AccessRights.WriteAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo).Grant("account", AccessLevel.User, Privilege.Read)).User(Alice, SalesBu, "appender")) // Read on her own accounts only: Bob's is out of reach
                .Seed(AccountOwnedBy(record, Bob));

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true))); // no access yet

            AddToTeam(svc, record, Alice, template);

            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));       // template granted Read
            svc.Update(new Entity("account", record) { ["name"] = "edited" });          // ...and Write
        }

        [Fact]
        public void Removing_a_user_from_the_access_team_revokes_the_rights()
        {
            Guid record = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo).Grant("account", AccessLevel.User, Privilege.Read)).User(Alice, SalesBu, "appender"))
                .Seed(AccountOwnedBy(record, Bob));
            AddToTeam(svc, record, Alice, template);
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true)));

            svc.Execute(new RemoveUserFromRecordTeamRequest
            {
                Record = new EntityReference("account", record),
                SystemUserId = Alice,
                TeamTemplateId = template,
            });

            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
        }

        [Fact]
        public void Removing_one_access_team_leaves_another_teams_grant_intact()
        {
            Guid record = Guid.NewGuid();
            Guid readTemplate = Guid.NewGuid();
            Guid writeTemplate = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(readTemplate, AccessRights.ReadAccess)
                .WithAccessTeamTemplate(writeTemplate, AccessRights.WriteAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo).Grant("account", AccessLevel.User, Privilege.Read)).User(Alice, SalesBu, "appender")) // Read on her own accounts only: Bob's is reached through the read team alone
                .Seed(AccountOwnedBy(record, Bob));
            AddToTeam(svc, record, Alice, readTemplate);
            AddToTeam(svc, record, Alice, writeTemplate);
            Assert.NotNull(svc.Retrieve("account", record, new ColumnSet(true))); // the read team grants Read

            // Drop the read team only.
            svc.Execute(new RemoveUserFromRecordTeamRequest
            {
                Record = new EntityReference("account", record),
                SystemUserId = Alice,
                TeamTemplateId = readTemplate,
            });

            // Read is gone, but the write team's grant remains.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => svc.Retrieve("account", record, new ColumnSet(true)));
            svc.Update(new Entity("account", record) { ["name"] = "still writable" });
        }

        [Fact]
        public void Access_team_rights_show_up_in_retrieve_principal_access()
        {
            Guid record = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess | AccessRights.DeleteAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo)).User(Alice, SalesBu, "appender"))
                .Seed(AccountOwnedBy(record, Bob));
            AddToTeam(svc, record, Alice, template);

            RetrievePrincipalAccessResponse access = (RetrievePrincipalAccessResponse)svc.Execute(
                new RetrievePrincipalAccessRequest
                {
                    Target = new EntityReference("account", record),
                    Principal = new EntityReference("systemuser", Alice),
                });

            Assert.True(access.AccessRights.HasFlag(AccessRights.ReadAccess));
            Assert.True(access.AccessRights.HasFlag(AccessRights.DeleteAccess));
            Assert.False(access.AccessRights.HasFlag(AccessRights.WriteAccess));
        }

        [Fact]
        public void Overlapping_access_team_rights_are_unioned_not_xored()
        {
            Guid record = Guid.NewGuid();
            Guid readWrite = Guid.NewGuid();
            Guid readDelete = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(readWrite, AccessRights.ReadAccess | AccessRights.WriteAccess)
                .WithAccessTeamTemplate(readDelete, AccessRights.ReadAccess | AccessRights.DeleteAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo)).User(Alice, SalesBu, "appender"))
                .Seed(AccountOwnedBy(record, Bob));
            AddToTeam(svc, record, Alice, readWrite);
            AddToTeam(svc, record, Alice, readDelete);

            RetrievePrincipalAccessResponse access = (RetrievePrincipalAccessResponse)svc.Execute(new RetrievePrincipalAccessRequest
            {
                Target = new EntityReference("account", record),
                Principal = new EntityReference("systemuser", Alice),
            });

            // The two teams both grant Read: a union keeps it, an XOR would cancel it out.
            Assert.True(access.AccessRights.HasFlag(AccessRights.ReadAccess));
            Assert.True(access.AccessRights.HasFlag(AccessRights.WriteAccess));
            Assert.True(access.AccessRights.HasFlag(AccessRights.DeleteAccess));
        }

        [Fact]
        public void Adding_to_an_unregistered_template_fails_fast()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo)).User(Alice, SalesBu, "appender"))
                .Seed(AccountOwnedBy(record, Bob));

            Assert.Throws<InvalidOperationException>(() => AddToTeam(svc, record, Alice, Guid.NewGuid()));
        }

        [Fact]
        public void Managing_a_record_access_team_requires_appendto_on_the_record()
        {
            Guid record = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).User(Alice, SalesBu)) // Alice has no AppendTo on account
                .Seed(AccountOwnedBy(record, Bob));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => AddToTeam(svc, record, Alice, template));
        }

        [Fact]
        public void The_access_team_id_is_stable_per_record_and_template()
        {
            Guid record = Guid.NewGuid();
            Guid template = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(template, AccessRights.ReadAccess)
                .WithSecurity(s => s.BusinessUnit(SalesBu).Role("appender", r => r.Grant("account", AccessLevel.Organization, Privilege.AppendTo)).User(Alice, SalesBu, "appender"))
                .Seed(AccountOwnedBy(record, Bob));

            Guid first = ((AddUserToRecordTeamResponse)svc.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", record), SystemUserId = Alice, TeamTemplateId = template,
            })).AccessTeamId;
            Guid second = ((AddUserToRecordTeamResponse)svc.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", record), SystemUserId = Bob, TeamTemplateId = template,
            })).AccessTeamId;

            Assert.NotEqual(Guid.Empty, first);
            Assert.Equal(first, second); // one access team per (record, template)
        }
    }
}
