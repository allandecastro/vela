// The role-membership query of the models guide (docs/guides/models.md). Edit the regions, not the page.
namespace Vela.DocSamples
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.DocSamples.Models;
    using Vela.Plugin;
    using Vela.Querying;
    using Vela.Testing;
    using Xunit;

    // A stand-in for the Roles class `vela models --roles Salesperson` generates.
    public static class Roles
    {
        public const string SalespersonName = "Salesperson";
        public static readonly Guid SalespersonId = new Guid("11111111-1111-1111-1111-111111111111");
    }

    public sealed partial class SalespersonCheck(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    #region role-membership
                    bool isSalesperson = ctx.SystemService.RetrieveMultiple(
                        Query.For<Role>()
                            .Where(r => r.ParentRootRoleId, ConditionOperator.Equal, Roles.SalespersonId)
                            .Link<SystemUserRoles>(r => r.RoleId, ur => ur.RoleId,
                                configure: l => l.Where(ur => ur.SystemUserId, ConditionOperator.Equal, ctx.InitiatingUserId))
                            .Top(1)).Entities.Count > 0;
                    #endregion

                    ctx.Trace("salesperson: {0}", isSalesperson);
                });
    }

    public sealed class SalespersonCheckTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Finds_A_Role_Held_In_A_Child_Business_Unit_By_Its_Root_Id(bool assigned)
        {
            Guid userId = Guid.NewGuid();
            Guid buCopyId = Guid.NewGuid();
            InMemoryOrganizationService store = new InMemoryOrganizationService();
            store.Create(new Role { Id = buCopyId, ParentRootRoleId = new EntityReference(Role.EntityLogicalNameValue, Roles.SalespersonId) });
            if (assigned)
            {
                store.Create(new SystemUserRoles { Id = Guid.NewGuid(), RoleId = buCopyId, SystemUserId = userId });
            }

            RecordingTracingService trace = new RecordingTracingService();
            PluginContext.For<Account>()
                .WithInitiatingUserId(userId)
                .WithSystemService(store)
                .WithTracingService(trace)
                .WithTarget(new Account { Id = Guid.NewGuid(), Name = "Contoso" })
                .Execute<SalespersonCheck>();

            Assert.Contains($"salesperson: {assigned}", trace.Traces);
        }
    }
}
