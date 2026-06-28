using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Targets InMemoryOrganizationService branches the main suite leaves red: the builder null-guards, the
    // RetrieveEntity-by-MetadataId path, ExecuteMultiple/Transaction edge cases, metadata-declared N:N
    // association, and the residual file-block protocol branches (default block order, missing optional
    // params, error tokens, inline-vs-fileid content resolution).
    public sealed class InMemoryOrganizationServiceCoverageTests
    {
        // ---- Builder guards ----------------------------------------------------------------------------

        [Fact]
        public void WithClock_and_WithIdSequence_reject_null()
        {
            Assert.Throws<ArgumentNullException>(() => new InMemoryOrganizationService().WithClock(null!));
            Assert.Throws<ArgumentNullException>(() => new InMemoryOrganizationService().WithIdSequence(null!));
        }

        [Fact]
        public void WithEntityMetadata_rejects_null_and_unnamed_metadata()
        {
            Assert.Throws<ArgumentException>(() => new InMemoryOrganizationService().WithEntityMetadata((EntityMetadata)null!));
            Assert.Throws<ArgumentException>(() => new InMemoryOrganizationService().WithEntityMetadata(new EntityMetadata()));
        }

        [Fact]
        public void Seed_assigns_an_id_when_the_record_has_none()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithIdSequence()
                .Seed(new Entity("account") { ["name"] = "NoId" }); // Id left as Guid.Empty

            Entity stored = Assert.Single(crm.GetAll("account"));
            Assert.NotEqual(Guid.Empty, stored.Id);
        }

        [Fact]
        public void Create_and_Update_reject_null_entities()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Assert.Throws<ArgumentNullException>(() => crm.Create(null!));
            Assert.Throws<ArgumentNullException>(() => crm.Update(null!));
        }

        // ---- Metadata reads ----------------------------------------------------------------------------

        [Fact]
        public void RetrieveAttribute_for_an_unregistered_attribute_throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account").Attribute("name", AttributeTypeCode.String).Build());

            Assert.Throws<InvalidOperationException>(() => crm.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = "account",
                LogicalName = "does_not_exist",
            }));
        }

        [Fact]
        public void RetrieveEntity_resolves_by_metadata_id_when_no_logical_name_is_given()
        {
            Guid metadataId = Guid.NewGuid();
            EntityMetadata metadata = FakeEntityMetadata.For("account").Build();
            metadata.MetadataId = metadataId;

            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithEntityMetadata(metadata);

            RetrieveEntityResponse response = (RetrieveEntityResponse)crm.Execute(new RetrieveEntityRequest
            {
                LogicalName = string.Empty,
                MetadataId = metadataId,
            });

            Assert.Equal("account", ((EntityMetadata)response.Results["EntityMetadata"]).LogicalName);
        }

        [Fact]
        public void RetrieveEntity_with_an_unknown_metadata_id_throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account").Build());

            Assert.Throws<InvalidOperationException>(() => crm.Execute(new RetrieveEntityRequest
            {
                LogicalName = string.Empty,
                MetadataId = Guid.NewGuid(),
            }));
        }

        // ---- ExecuteMultiple / ExecuteTransaction ------------------------------------------------------

        [Fact]
        public void ExecuteMultiple_without_settings_defaults_to_no_continue_no_responses()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            ExecuteMultipleRequest request = new ExecuteMultipleRequest
            {
                Settings = null!, // exercise the "Settings?.X ?? false" null path
                Requests = new OrganizationRequestCollection
                {
                    new CreateRequest { Target = new Entity("account") { Id = Guid.NewGuid(), ["name"] = "A" } },
                },
            };

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)crm.Execute(request);
            Assert.False((bool)response.Results["IsFaulted"]);
            Assert.Empty(((ExecuteMultipleResponseItemCollection)response.Results["Responses"]).ToArray()); // ReturnResponses defaulted false
        }

        [Fact]
        public void ExecuteMultiple_stops_at_the_first_fault_when_continue_on_error_is_false()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            ExecuteMultipleRequest request = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true },
                Requests = new OrganizationRequestCollection
                {
                    new UpdateRequest { Target = new Entity("account") { Id = Guid.NewGuid(), ["name"] = "ghost" } }, // throws: not stored
                    new CreateRequest { Target = new Entity("account") { Id = Guid.NewGuid(), ["name"] = "never" } },
                },
            };

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)crm.Execute(request);
            Assert.True((bool)response.Results["IsFaulted"]);
            ExecuteMultipleResponseItemCollection items = (ExecuteMultipleResponseItemCollection)response.Results["Responses"];
            Assert.Single(items.ToArray());            // stopped after the fault, second request not run
            Assert.NotNull(items[0].Fault);
            Assert.Empty(crm.GetAll("account"));        // the second Create never executed
        }

        [Fact]
        public void ExecuteTransaction_returns_child_responses_on_success()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            ExecuteTransactionRequest request = new ExecuteTransactionRequest
            {
                ReturnResponses = true,
                Requests = new OrganizationRequestCollection
                {
                    new CreateRequest { Target = new Entity("account") { Id = Guid.NewGuid(), ["name"] = "A" } },
                    new CreateRequest { Target = new Entity("account") { Id = Guid.NewGuid(), ["name"] = "B" } },
                },
            };

            ExecuteTransactionResponse response = (ExecuteTransactionResponse)crm.Execute(request);
            OrganizationResponseCollection responses = (OrganizationResponseCollection)response.Results["Responses"];
            Assert.Equal(2, responses.Count);
            Assert.Equal(2, crm.GetAll("account").Count);
        }

        // ---- N:N resolved from registered metadata -----------------------------------------------------

        [Fact]
        public void Associate_resolves_the_intersect_from_registered_metadata_and_is_idempotent()
        {
            Guid accountId = Guid.NewGuid();
            Guid tagId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account")
                    .ManyToMany("account_tag", "account_tag", "account", "accountid", "tag", "tagid")
                    .Build())
                .Seed(
                    new Entity("account") { Id = accountId, ["name"] = "Acme" },
                    new Entity("tag") { Id = tagId, ["name"] = "vip" });

            Relationship rel = new Relationship("account_tag");
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("tag", tagId) };

            crm.Associate("account", accountId, rel, related);
            crm.Associate("account", accountId, rel, related); // idempotent: no duplicate intersect row
            Assert.Single(crm.GetAll("account_tag"));

            crm.Disassociate("account", accountId, rel, related);
            Assert.Empty(crm.GetAll("account_tag"));
        }

        // ---- File-block protocol residual branches -----------------------------------------------------

        // Upload with no FileName and commit with no BlockList/FileName/MimeType, then download with no
        // Offset/BlockLength: exercises every optional-parameter default in one round-trip.
        [Fact]
        public void File_round_trip_uses_defaults_for_every_optional_parameter()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Acme" });
            Guid accountId = crm.GetAll("account").Single().Id;

            string token = (string)crm.Execute(new OrganizationRequest("InitializeFileBlocksUpload")
            {
                Parameters = { ["Target"] = new EntityReference("account", accountId), ["FileAttributeName"] = "new_file" },
            }).Results["FileContinuationToken"];

            crm.Execute(new OrganizationRequest("UploadBlock")
            {
                Parameters = { ["FileContinuationToken"] = token, ["BlockId"] = "b1", ["BlockData"] = new byte[] { 1, 2, 3 } },
            });

            // No BlockList -> assembles in upload order; no FileName/MimeType -> falls back to defaults.
            OrganizationResponse commit = crm.Execute(new OrganizationRequest("CommitFileBlocksUpload")
            {
                Parameters = { ["FileContinuationToken"] = token },
            });
            Assert.Equal(3L, (long)commit.Results["FileSizeInBytes"]);

            string downloadToken = (string)crm.Execute(new OrganizationRequest("InitializeFileBlocksDownload")
            {
                Parameters = { ["Target"] = new EntityReference("account", accountId), ["FileAttributeName"] = "new_file" },
            }).Results["FileContinuationToken"];

            // No Offset/BlockLength -> whole file.
            byte[] data = (byte[])crm.Execute(new OrganizationRequest("DownloadBlock")
            {
                Parameters = { ["FileContinuationToken"] = downloadToken },
            }).Results["Data"];
            Assert.Equal(new byte[] { 1, 2, 3 }, data);
        }

        [Fact]
        public void UploadBlock_and_DownloadBlock_reject_unknown_tokens()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Assert.Throws<InvalidOperationException>(() => crm.Execute(new OrganizationRequest("UploadBlock")
            {
                Parameters = { ["FileContinuationToken"] = "nope", ["BlockId"] = "b1", ["BlockData"] = new byte[] { 1 } },
            }));

            Assert.Throws<InvalidOperationException>(() => crm.Execute(new OrganizationRequest("DownloadBlock")
            {
                Parameters = { ["FileContinuationToken"] = "nope" },
            }));
        }

        [Fact]
        public void Execute_with_a_missing_required_parameter_throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            // InitializeFileBlocksUpload requires a "Target" EntityReference; omit it.
            Assert.Throws<InvalidOperationException>(() => crm.Execute(new OrganizationRequest("InitializeFileBlocksUpload")
            {
                Parameters = { ["FileAttributeName"] = "new_file" },
            }));
        }

        [Fact]
        public void Download_of_a_column_holding_an_unknown_value_throws()
        {
            // A File column referencing a fileid that was never uploaded resolves to neither a blob nor bytes.
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(new Entity("account") { Id = accountId, ["new_file"] = Guid.NewGuid() });

            Assert.Throws<InvalidOperationException>(() => crm.Execute(new OrganizationRequest("InitializeFileBlocksDownload")
            {
                Parameters = { ["Target"] = new EntityReference("account", accountId), ["FileAttributeName"] = "new_file" },
            }));
        }
    }
}
