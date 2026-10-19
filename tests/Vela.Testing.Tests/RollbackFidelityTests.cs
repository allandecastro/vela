using System;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: a synchronous failure rolls back EVERYTHING the operation touched — not just records, but the
    // shares (PoA), access-team grants and file blobs it created — because Snapshot/RestoreTo captures every
    // mutable store. A successful operation persists. Exercises the Snapshot/RestoreTo completeness fix through
    // both rollback mechanisms (the pipeline's RunOperation and the store's ExecuteTransaction).
    public sealed class RollbackFidelityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000a01");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000a02");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-000000000ab1");
        private static readonly Guid Template = new Guid("7e770000-0000-0000-0000-000000000a99");

        private static Action<SecurityConfiguration> FullRights => s => s
            .BusinessUnit(SalesBu)
            .Role("admin", r => r.Grant("account", AccessLevel.Organization,
                Privilege.Create, Privilege.Read, Privilege.Write, Privilege.Delete,
                Privilege.Share, Privilege.Assign, Privilege.Append, Privilege.AppendTo))
            .User(Alice, SalesBu, "admin")
            .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read)) // a share needs the table privilege
            .User(Bob, SalesBu, "basic");

        private static Entity AccountOwnedByAlice(Guid id) => new Entity("account", id)
        {
            ["name"] = "seed",
            ["ownerid"] = new EntityReference("systemuser", Alice),
            ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
        };

        private static AccessRights PrincipalAccess(IOrganizationService svc, Guid record, Guid principal) =>
            ((RetrievePrincipalAccessResponse)svc.Execute(new RetrievePrincipalAccessRequest
            {
                Target = new EntityReference("account", record),
                Principal = new EntityReference("systemuser", principal),
            })).AccessRights;

        // --- Pipeline: a sync plugin throw rolls back the record, its GrantAccess and its file change ----

        [Fact]
        public void A_sync_plugin_throw_rolls_back_the_record_and_the_share_and_file_it_made()
        {
            Guid existing = Guid.NewGuid();
            RfShareAndFileThenThrow.TargetRecordId = existing;
            RfShareAndFileThenThrow.Grantee = Bob;

            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<RfShareAndFileThenThrow>()
                .WithCurrentUser(Alice)
                .WithSecurity(FullRights)
                .Seed(AccountOwnedByAlice(existing));

            // The create fires the plugin, which shares + uploads a file to the seeded record, then throws.
            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new FakeAccount { Name = "new" }));

            // The new record was not created (only the seeded one remains).
            Entity onlyRecord = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal(existing, onlyRecord.Id);

            // The share the plugin made was rolled back (Bob has no role, so no share => no access).
            Assert.Equal(AccessRights.None, PrincipalAccess(pipeline.Service, existing, Bob));

            // The file column the plugin committed was rolled back.
            Entity reread = pipeline.Service.Retrieve("account", existing, new ColumnSet(true));
            Assert.False(reread.Contains("vela_file"));
        }

        // --- ExecuteTransaction: a late failure rolls back the record, share and access-team grant --------

        [Fact]
        public void ExecuteTransaction_rolls_back_record_share_and_access_team_on_a_late_failure()
        {
            Guid existing = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(Template, AccessRights.ReadAccess)
                .WithSecurity(FullRights)
                .Seed(AccountOwnedByAlice(existing));

            ExecuteTransactionRequest tx = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            tx.Requests.Add(new GrantAccessRequest
            {
                Target = new EntityReference("account", existing),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", Bob), AccessMask = AccessRights.ReadAccess },
            });
            tx.Requests.Add(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", existing),
                SystemUserId = Bob,
                TeamTemplateId = Template,
            });
            tx.Requests.Add(new CreateRequest { Target = AccountOwnedByAlice(Guid.NewGuid()) });
            tx.Requests.Add(new DeleteRequest { Target = new EntityReference("account", Guid.NewGuid()) }); // does not exist -> throws

            Assert.ThrowsAny<Exception>(() => svc.Execute(tx));

            // Everything the earlier (committed) children did is undone with the whole batch.
            Entity onlyRecord = Assert.Single(svc.GetAll("account"));
            Assert.Equal(existing, onlyRecord.Id);            // the CreateRequest child was rolled back
            Assert.Equal(AccessRights.None, PrincipalAccess(svc, existing, Bob)); // share + access team reverted
        }

        // --- A successful transaction persists every child's effect --------------------------------------

        [Fact]
        public void A_successful_transaction_persists_the_record_and_the_share()
        {
            Guid existing = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithAccessTeamTemplate(Template, AccessRights.ReadAccess)
                .WithSecurity(FullRights)
                .Seed(AccountOwnedByAlice(existing));

            ExecuteTransactionRequest tx = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            tx.Requests.Add(new GrantAccessRequest
            {
                Target = new EntityReference("account", existing),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", Bob), AccessMask = AccessRights.ReadAccess },
            });
            tx.Requests.Add(new CreateRequest { Target = AccountOwnedByAlice(Guid.NewGuid()) });

            svc.Execute(tx);

            Assert.Equal(2, svc.GetAll("account").Count);                        // both records persist
            Assert.True(PrincipalAccess(svc, existing, Bob).HasFlag(AccessRights.ReadAccess)); // the share persists
        }
    }

    // A post-create plugin that shares a pre-existing record and uploads a file to it, then throws — used to
    // prove the surrounding operation rolls back the record, the share AND the file it made.
    public sealed class RfShareAndFileThenThrow : EntityPluginBase<FakeAccount>
    {
        public static Guid TargetRecordId;
        public static Guid Grantee;

        public RfShareAndFileThenThrow(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);

        private void OnCreated(ICreatePostContext<FakeAccount> ctx)
        {
            EntityReference target = new EntityReference("account", TargetRecordId);

            ctx.SystemService.Execute(new GrantAccessRequest
            {
                Target = target,
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", Grantee), AccessMask = AccessRights.ReadAccess },
            });

            OrganizationResponse init = ctx.SystemService.Execute(new OrganizationRequest("InitializeFileBlocksUpload")
            {
                Parameters = { { "Target", target }, { "FileAttributeName", "vela_file" }, { "FileName", "f.txt" } },
            });
            string token = (string)init.Results["FileContinuationToken"];
            ctx.SystemService.Execute(new OrganizationRequest("UploadBlock")
            {
                Parameters = { { "FileContinuationToken", token }, { "BlockId", "1" }, { "BlockData", new byte[] { 1, 2, 3 } } },
            });
            ctx.SystemService.Execute(new OrganizationRequest("CommitFileBlocksUpload")
            {
                Parameters = { { "FileContinuationToken", token }, { "FileName", "f.txt" }, { "MimeType", "text/plain" } },
            });

            throw new PluginBusinessException("boom after side effects");
        }
    }
}
