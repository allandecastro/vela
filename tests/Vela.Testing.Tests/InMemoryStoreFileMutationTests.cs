using System;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Mutation-survivor kills for the in-memory store (InMemoryOrganizationService, #448): the File/Image block
    // protocol (response names, file-name precedence, block order, slicing, token lifetime, DeleteFile) and the
    // store snapshot an ExecuteTransaction rolls back to (shares, row versions, file content).
    public sealed class InMemoryStoreFileMutationTests
    {
        private const string Column = "new_document";

        private static OrganizationResponse Run(InMemoryOrganizationService svc, string message, params (string Name, object Value)[] parameters)
        {
            OrganizationRequest request = new OrganizationRequest(message);
            foreach ((string name, object value) in parameters)
            {
                request.Parameters[name] = value;
            }

            return svc.Execute(request);
        }

        private static string StartUpload(InMemoryOrganizationService svc, Guid account, string? fileName = null)
        {
            OrganizationResponse response = fileName == null
                ? Run(svc, "InitializeFileBlocksUpload", ("Target", new EntityReference("account", account)), ("FileAttributeName", Column))
                : Run(svc, "InitializeFileBlocksUpload", ("Target", new EntityReference("account", account)), ("FileAttributeName", Column), ("FileName", fileName));
            Assert.Equal("InitializeFileBlocksUpload", response.ResponseName);
            return (string)response.Results["FileContinuationToken"];
        }

        private static void Block(InMemoryOrganizationService svc, string token, string blockId, params byte[] data)
        {
            OrganizationResponse response = Run(svc, "UploadBlock", ("FileContinuationToken", token), ("BlockId", blockId), ("BlockData", data));
            Assert.Equal("UploadBlock", response.ResponseName);
        }

        private static Guid Commit(InMemoryOrganizationService svc, string token, params (string Name, object Value)[] extra)
        {
            (string, object)[] parameters = new (string, object)[extra.Length + 1];
            parameters[0] = ("FileContinuationToken", token);
            Array.Copy(extra, 0, parameters, 1, extra.Length);
            OrganizationResponse response = Run(svc, "CommitFileBlocksUpload", parameters);
            Assert.Equal("CommitFileBlocksUpload", response.ResponseName);
            return (Guid)response.Results["FileId"];
        }

        private static OrganizationResponse StartDownload(InMemoryOrganizationService svc, Guid account, string column = Column) =>
            Run(svc, "InitializeFileBlocksDownload", ("Target", new EntityReference("account", account)), ("FileAttributeName", column));

        private static byte[] Download(InMemoryOrganizationService svc, Guid account, params (string Name, object Value)[] extra)
        {
            string token = (string)StartDownload(svc, account).Results["FileContinuationToken"];
            (string, object)[] parameters = new (string, object)[extra.Length + 1];
            parameters[0] = ("FileContinuationToken", token);
            Array.Copy(extra, 0, parameters, 1, extra.Length);
            OrganizationResponse response = Run(svc, "DownloadBlock", parameters);
            Assert.Equal("DownloadBlock", response.ResponseName);
            return (byte[])response.Results["Data"];
        }

        private static (InMemoryOrganizationService Svc, Guid Account) WithAccount()
        {
            Guid account = Guid.NewGuid();
            return (new InMemoryOrganizationService().Seed(new Entity("account", account) { ["name"] = "Zava" }), account);
        }

        private static Guid UploadFile(InMemoryOrganizationService svc, Guid account, params byte[] content)
        {
            string token = StartUpload(svc, account);
            Block(svc, token, "b1", content);
            return Commit(svc, token);
        }

        // ---- Upload ----------------------------------------------------------------------------------------

        [Fact]
        public void Commit_assembles_blocks_in_the_listed_order()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            string token = StartUpload(svc, account);
            Block(svc, token, "b2", 3, 4);
            Block(svc, token, "b1", 1, 2);

            Commit(svc, token, ("BlockList", new[] { "b1", "b2" }));

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, Download(svc, account));
        }

        [Fact]
        public void The_file_name_given_at_commit_wins_over_the_one_given_at_initialize()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            string token = StartUpload(svc, account, "draft.txt");
            Block(svc, token, "b1", 1);

            Commit(svc, token, ("FileName", "final.txt"));

            Assert.Equal("final.txt", StartDownload(svc, account).Results["FileName"]);
        }

        [Fact]
        public void Without_a_commit_file_name_the_initialize_file_name_is_kept()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            string token = StartUpload(svc, account, "draft.txt");
            Block(svc, token, "b1", 1);

            Commit(svc, token);

            Assert.Equal("draft.txt", StartDownload(svc, account).Results["FileName"]);
        }

        [Fact]
        public void Without_any_file_name_the_downloaded_file_name_is_empty()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            UploadFile(svc, account, 1);

            Assert.Equal(string.Empty, StartDownload(svc, account).Results["FileName"]);
        }

        [Fact]
        public void A_committed_upload_token_cannot_be_committed_again()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            string token = StartUpload(svc, account);
            Block(svc, token, "b1", 1);
            Commit(svc, token);

            Assert.Throws<InvalidOperationException>(() => Commit(svc, token));
        }

        // ---- Download --------------------------------------------------------------------------------------

        [Fact]
        public void InitializeFileBlocksDownload_reports_the_size_and_chunking_support()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            UploadFile(svc, account, 1, 2, 3);

            OrganizationResponse response = StartDownload(svc, account);

            Assert.Equal("InitializeFileBlocksDownload", response.ResponseName);
            Assert.Equal(3L, response.Results["FileSizeInBytes"]);
            Assert.Equal(true, response.Results["IsChunkingSupported"]);
        }

        [Fact]
        public void DownloadBlock_returns_the_requested_slice()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            UploadFile(svc, account, 1, 2, 3, 4, 5);

            Assert.Equal(new byte[] { 2, 3 }, Download(svc, account, ("Offset", 1L), ("BlockLength", 2L)));
            Assert.Equal(new byte[] { 4, 5 }, Download(svc, account, ("Offset", 3L))); // no length -> to the end
        }

        // An Image column (or any column holding bytes inline) downloads its bytes with no file name.
        [Fact]
        public void Inline_bytes_download_with_an_empty_file_name()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", account) { ["entityimage"] = new byte[] { 9, 8 } });

            OrganizationResponse response = StartDownload(svc, account, "entityimage");

            Assert.Equal(string.Empty, response.Results["FileName"]);
            Assert.Equal(2L, response.Results["FileSizeInBytes"]);
        }

        // Rejected up front rather than crashing further down. The exception type is deliberately not pinned: the
        // store throws InvalidOperationException, while the platform's fault for a missing record is unverified here.
        [Fact]
        public void Download_from_a_missing_record_is_rejected()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            Exception? ex = Record.Exception(() => StartDownload(svc, Guid.NewGuid()));

            Assert.NotNull(ex);
            Assert.IsNotType<NullReferenceException>(ex);
        }

        // ---- DeleteFile ------------------------------------------------------------------------------------

        [Fact]
        public void DeleteFile_clears_the_column_and_discards_the_content()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            Guid fileId = UploadFile(svc, account, 1, 2);

            OrganizationResponse response = Run(svc, "DeleteFile", ("FileId", fileId));

            Assert.Equal("DeleteFile", response.ResponseName);
            Assert.False(svc.Retrieve("account", account, new ColumnSet(true)).Contains(Column));

            // Pointing the column back at the deleted fileid serves nothing: the content is gone.
            svc.Update(new Entity("account", account) { [Column] = fileId });
            Assert.Throws<InvalidOperationException>(() => StartDownload(svc, account));
        }

        // ---- Transaction rollback (store snapshot) ---------------------------------------------------------

        private static void FailingTransaction(InMemoryOrganizationService svc, Guid duplicateAccount, params OrganizationRequest[] before)
        {
            ExecuteTransactionRequest request = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection() };
            request.Requests.AddRange(before);
            request.Requests.Add(new CreateRequest { Target = new Entity("account", duplicateAccount) }); // duplicate id -> fault
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(request));
        }

        [Fact]
        public void A_rolled_back_transaction_keeps_existing_shares_and_row_versions()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();
            Guid first = svc.Create(new Entity("account") { ["name"] = "A" });
            Guid second = svc.Create(new Entity("account") { ["name"] = "B" });
            EntityReference firstRef = new EntityReference("account", first);
            svc.WithSharedRecord(firstRef, new EntityReference("systemuser", Guid.NewGuid()), AccessRights.ReadAccess);
            long secondVersion = svc.Retrieve("account", second, new ColumnSet(true)).GetAttributeValue<long>("versionnumber");

            FailingTransaction(svc, first, new CreateRequest { Target = new Entity("account") { ["name"] = "rolled back" } });

            Assert.Equal(2, svc.GetAll("account").Count);
            Assert.Single(((RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                new RetrieveSharedPrincipalsAndAccessRequest { Target = firstRef })).PrincipalAccesses);
            Guid third = svc.Create(new Entity("account") { ["name"] = "C" });
            Assert.True(svc.Retrieve("account", third, new ColumnSet(true)).GetAttributeValue<long>("versionnumber") > secondVersion);
        }

        [Fact]
        public void A_rolled_back_transaction_keeps_existing_file_content()
        {
            (InMemoryOrganizationService svc, Guid account) = WithAccount();
            UploadFile(svc, account, 1, 2, 3);

            FailingTransaction(svc, account, new UpdateRequest { Target = new Entity("account", account) { ["name"] = "x" } });

            Assert.Equal(new byte[] { 1, 2, 3 }, Download(svc, account));
        }

        [Fact]
        public void A_file_committed_inside_a_rolled_back_transaction_is_discarded()
        {
            Guid lastId = Guid.Empty;
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .WithIdSequence(() => lastId = Guid.NewGuid())
                .Seed(new Entity("account", account) { ["name"] = "Zava" });
            string token = StartUpload(svc, account);
            Block(svc, token, "b1", 7);

            OrganizationRequest commit = new OrganizationRequest("CommitFileBlocksUpload") { ["FileContinuationToken"] = token };
            FailingTransaction(svc, account, commit);

            Assert.False(svc.Retrieve("account", account, new ColumnSet(true)).Contains(Column));
            svc.Update(new Entity("account", account) { [Column] = lastId }); // the fileid the rolled-back commit minted
            Assert.Throws<InvalidOperationException>(() => StartDownload(svc, account));
        }
    }
}
