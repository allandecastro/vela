using System;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class InMemoryOrganizationServiceTests
    {
        [Fact]
        public void Crud_RoundTrips()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Assert.Equal("Zava", crm.Retrieve("account", id, new ColumnSet(true)).GetAttributeValue<string>("name"));

            crm.Update(new Entity("account") { Id = id, ["name"] = "Renamed" });
            Assert.Equal("Renamed", crm.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name"));

            crm.Delete("account", id);
            Assert.Throws<InvalidOperationException>(() => crm.Retrieve("account", id, new ColumnSet(true)));
        }

        [Fact]
        public void FileBlocks_UploadCommitDownload_RoundTripsContent()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            byte[] content = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            string upload = (string)crm.Execute(new OrganizationRequest("InitializeFileBlocksUpload")
            {
                Parameters = { ["Target"] = new EntityReference("account", id), ["FileAttributeName"] = "vela_document", ["FileName"] = "doc.bin" }
            }).Results["FileContinuationToken"];

            crm.Execute(new OrganizationRequest("UploadBlock")
            {
                Parameters = { ["FileContinuationToken"] = upload, ["BlockId"] = "b1", ["BlockData"] = content }
            });

            OrganizationResponse commit = crm.Execute(new OrganizationRequest("CommitFileBlocksUpload")
            {
                Parameters = { ["FileContinuationToken"] = upload, ["FileName"] = "doc.bin", ["MimeType"] = "application/octet-stream", ["BlockList"] = new[] { "b1" } }
            });
            Guid fileId = (Guid)commit.Results["FileId"];
            Assert.Equal((long)content.Length, (long)commit.Results["FileSizeInBytes"]);

            // The File column round-trips the fileid (not the content), like the platform.
            Entity stored = crm.Retrieve("account", id, new ColumnSet("vela_document"));
            Assert.Equal(fileId, stored.GetAttributeValue<Guid>("vela_document"));

            // The content is fetched through the download protocol.
            OrganizationResponse download = crm.Execute(new OrganizationRequest("InitializeFileBlocksDownload")
            {
                Parameters = { ["Target"] = new EntityReference("account", id), ["FileAttributeName"] = "vela_document" }
            });
            string token = (string)download.Results["FileContinuationToken"];
            Assert.Equal("doc.bin", download.Results["FileName"]);
            Assert.Equal((long)content.Length, (long)download.Results["FileSizeInBytes"]);

            byte[] downloaded = (byte[])crm.Execute(new OrganizationRequest("DownloadBlock")
            {
                Parameters = { ["FileContinuationToken"] = token, ["Offset"] = 0L, ["BlockLength"] = (long)content.Length }
            }).Results["Data"];
            Assert.Equal(content, downloaded);
        }

        [Fact]
        public void DeleteFile_RemovesContentAndClearsTheColumn()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });

            string upload = (string)crm.Execute(new OrganizationRequest("InitializeFileBlocksUpload")
            {
                Parameters = { ["Target"] = new EntityReference("account", id), ["FileAttributeName"] = "vela_document", ["FileName"] = "doc.bin" }
            }).Results["FileContinuationToken"];
            crm.Execute(new OrganizationRequest("UploadBlock")
            {
                Parameters = { ["FileContinuationToken"] = upload, ["BlockId"] = "b1", ["BlockData"] = new byte[] { 9, 9 } }
            });
            Guid fileId = (Guid)crm.Execute(new OrganizationRequest("CommitFileBlocksUpload")
            {
                Parameters = { ["FileContinuationToken"] = upload, ["FileName"] = "doc.bin", ["BlockList"] = new[] { "b1" } }
            }).Results["FileId"];

            crm.Execute(new OrganizationRequest("DeleteFile") { Parameters = { ["FileId"] = fileId } });

            Entity stored = crm.Retrieve("account", id, new ColumnSet("vela_document"));
            Assert.False(stored.Contains("vela_document"));
            Assert.Throws<InvalidOperationException>(() => crm.Execute(new OrganizationRequest("InitializeFileBlocksDownload")
            {
                Parameters = { ["Target"] = new EntityReference("account", id), ["FileAttributeName"] = "vela_document" }
            }));
        }

        [Fact]
        public void ImageColumn_HeldInline_IsDownloadedDirectly()
        {
            // An Image column (or any column carrying bytes inline) is its own content/thumbnail — no upload needed.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            byte[] thumbnail = new byte[] { 42, 43, 44 };
            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava", ["vela_photo"] = thumbnail });

            string token = (string)crm.Execute(new OrganizationRequest("InitializeFileBlocksDownload")
            {
                Parameters = { ["Target"] = new EntityReference("account", id), ["FileAttributeName"] = "vela_photo" }
            }).Results["FileContinuationToken"];

            byte[] downloaded = (byte[])crm.Execute(new OrganizationRequest("DownloadBlock")
            {
                Parameters = { ["FileContinuationToken"] = token, ["Offset"] = 0L, ["BlockLength"] = (long)thumbnail.Length }
            }).Results["Data"];
            Assert.Equal(thumbnail, downloaded);
        }

        [Fact]
        public void RetrieveMultiple_FiltersAndProjects()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(
                    new Entity("account") { Id = Guid.NewGuid(), ["name"] = "A", ["statecode"] = new OptionSetValue(0) },
                    new Entity("account") { Id = Guid.NewGuid(), ["name"] = "B", ["statecode"] = new OptionSetValue(1) });

            QueryExpression query = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet("name"),
                Criteria = { Conditions = { new ConditionExpression("statecode", ConditionOperator.Equal, 0) } }
            };

            EntityCollection result = crm.RetrieveMultiple(query);

            Assert.Single(result.Entities);
            Assert.Equal("A", result.Entities[0].GetAttributeValue<string>("name"));
            Assert.False(result.Entities[0].Contains("statecode")); // projected away
        }

        [Fact]
        public void AlternateKey_DuplicateValue_Throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAlternateKey("account", "accountnumber");

            crm.Create(new Entity("account") { ["accountnumber"] = "ACC-1" });

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => crm.Create(new Entity("account") { ["accountnumber"] = "ACC-1" }));
            Assert.Contains("alternate key", ex.Message);
        }

        [Fact]
        public void AutoNumber_PopulatesAttribute_WhenEmpty()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAutoNumber("task", "ticketnumber", n => $"T-{n:000}");

            Guid first = crm.Create(new Entity("task"));
            Guid second = crm.Create(new Entity("task") { ["ticketnumber"] = "MANUAL" });

            Assert.Equal("T-001", crm.Retrieve("task", first, new ColumnSet(true)).GetAttributeValue<string>("ticketnumber"));
            Assert.Equal("MANUAL", crm.Retrieve("task", second, new ColumnSet(true)).GetAttributeValue<string>("ticketnumber"));
        }

        // End-to-end example combining the whole package: typed builder + early-bound entities +
        // in-memory CRM (auto-number + alternate key) + plugin execution + trace capture + state assertions.
        // Note: the trigger (Create/PostOperation) is auto-derived from the plugin's registration — no need
        // to restate OnCreate().Stage(...).Mode(...) here.
        [Fact]
        public void EndToEnd_PluginProvisionsTask_AgainstInMemoryCrm()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAlternateKey("account", "accountnumber")
                .WithAutoNumber("task", "ticketnumber", "T-", 3);

            Guid accountId = Guid.NewGuid();

            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .WithPrimaryEntity(FakeAccount.EntityLogicalNameValue, accountId)
                .WithTarget(new FakeAccount { Id = accountId, Name = "Zava" })
                .WithOrganizationService(crm);

            builder.Execute(new ProvisionAccountPlugin());

            // The plugin created a task referencing the account (read back as an early-bound entity)...
            FakeTask task = Assert.Single(crm.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Welcome Zava", task.Subject);
            Assert.Equal(accountId, task.RegardingObjectId.Id);

            // ...the auto-number was applied by the in-memory CRM...
            Assert.Equal("T-001", task.TicketNumber);

            // ...and the plugin's trace was captured.
            Assert.Contains("Provisioning account Zava", builder.TraceCapture.Output);
        }

        [Fact]
        public void AlternateKey_Composite_EnforcedOnlyWhenAllAttributesPopulated()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAlternateKey("widget", "code", "region");

            crm.Create(new Entity("widget") { ["code"] = "W1", ["region"] = "EU" });

            // Same code, different region: not a duplicate.
            crm.Create(new Entity("widget") { ["code"] = "W1", ["region"] = "US" });

            // Region missing: the key isn't fully populated, so it is not enforced.
            crm.Create(new Entity("widget") { ["code"] = "W1" });

            // All attributes match an existing row: duplicate.
            Assert.Throws<InvalidOperationException>(
                () => crm.Create(new Entity("widget") { ["code"] = "W1", ["region"] = "EU" }));
        }

        [Fact]
        public void AlternateKey_UpdateIntoDuplicate_ThrowsAndLeavesStoreIntact()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAlternateKey("account", "accountnumber");

            crm.Create(new Entity("account") { ["accountnumber"] = "ACC-1" });
            Guid second = crm.Create(new Entity("account") { ["accountnumber"] = "ACC-2" });

            Assert.Throws<InvalidOperationException>(
                () => crm.Update(new Entity("account") { Id = second, ["accountnumber"] = "ACC-1" }));

            // The failed update must not have mutated the record.
            Assert.Equal("ACC-2", crm.Retrieve("account", second, new ColumnSet(true)).GetAttributeValue<string>("accountnumber"));
        }

        [Fact]
        public void AlternateKey_DeclaredOnOneEntity_DoesNotAffectAnother()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAlternateKey("account", "code");

            crm.Create(new Entity("account") { ["code"] = "X" });

            // Same value on a different entity type is unaffected by the account key.
            crm.Create(new Entity("contact") { ["code"] = "X" });
            crm.Create(new Entity("contact") { ["code"] = "X" });
        }

        [Fact]
        public void AutoNumber_Sequential_PadsAndIncrementsPerRule()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAutoNumber("invoice", "number", "INV-", 4, "-Z");

            Guid first = crm.Create(new Entity("invoice"));
            Guid second = crm.Create(new Entity("invoice"));

            Assert.Equal("INV-0001-Z", crm.Retrieve("invoice", first, new ColumnSet(true)).GetAttributeValue<string>("number"));
            Assert.Equal("INV-0002-Z", crm.Retrieve("invoice", second, new ColumnSet(true)).GetAttributeValue<string>("number"));
        }

        [Fact]
        public void AutoNumber_OnlyAppliesToMatchingEntity_AndTreatsEmptyStringAsEmpty()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithAutoNumber("task", "ticketnumber", n => $"T-{n:000}");

            // Empty string counts as empty and gets populated.
            Guid task = crm.Create(new Entity("task") { ["ticketnumber"] = string.Empty });
            // A different entity is untouched by the rule.
            Guid account = crm.Create(new Entity("account"));

            Assert.Equal("T-001", crm.Retrieve("task", task, new ColumnSet(true)).GetAttributeValue<string>("ticketnumber"));
            Assert.False(crm.Retrieve("account", account, new ColumnSet(true)).Contains("ticketnumber"));
        }

        [Fact]
        public void Create_UserOwnedByDefault_StampsAuditAndOwnerColumns()
        {
            Guid user = Guid.NewGuid();
            Guid businessUnit = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(user)
                .WithCurrentBusinessUnit(businessUnit);

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.True(stored.Contains("createdon"));
            Assert.True(stored.Contains("modifiedon"));
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("modifiedby").Id);
            Assert.Equal("systemuser", stored.GetAttributeValue<EntityReference>("ownerid").LogicalName);
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("owninguser").Id);
            Assert.Equal(businessUnit, stored.GetAttributeValue<EntityReference>("owningbusinessunit").Id);
            Assert.False(stored.Contains("owningteam"));
        }

        [Fact]
        public void Create_DoesNotOverwriteCallerSuppliedOwner()
        {
            Guid caller = Guid.NewGuid();
            Guid explicitOwner = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(caller);

            Guid id = crm.Create(new Entity("account")
            {
                ["name"] = "Zava",
                ["ownerid"] = new EntityReference("systemuser", explicitOwner),
            });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            // A caller-supplied owner is honoured, and owninguser is derived from it (not the calling user).
            Assert.Equal(explicitOwner, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(explicitOwner, stored.GetAttributeValue<EntityReference>("owninguser").Id);
        }

        [Fact]
        public void Create_OrganizationOwned_StampsAuditButNoOwnerColumns()
        {
            Guid user = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(user)
                .AsOrganizationOwned("savedquery");

            Guid id = crm.Create(new Entity("savedquery") { ["name"] = "View" });
            Entity stored = crm.Retrieve("savedquery", id, new ColumnSet(true));

            Assert.True(stored.Contains("createdon"));
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.False(stored.Contains("ownerid"));
            Assert.False(stored.Contains("owninguser"));
            Assert.False(stored.Contains("owningbusinessunit"));
        }

        [Fact]
        public void Create_Virtual_PopulatesNoSystemColumns()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Guid.NewGuid())
                .AsVirtual("zava_externaldata");

            Guid id = crm.Create(new Entity("zava_externaldata") { ["name"] = "x" });
            Entity stored = crm.Retrieve("zava_externaldata", id, new ColumnSet(true));

            Assert.False(stored.Contains("createdon"));
            Assert.False(stored.Contains("createdby"));
            Assert.False(stored.Contains("ownerid"));
        }

        [Fact]
        public void Create_Elastic_StampsAuditAndOwnerColumns()
        {
            Guid user = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(user)
                .AsElastic("zava_telemetry");

            Guid id = crm.Create(new Entity("zava_telemetry") { ["name"] = "ping" });
            Entity stored = crm.Retrieve("zava_telemetry", id, new ColumnSet(true));

            // Elastic is a storage type, not an ownership type — elastic tables are ownable.
            Assert.True(stored.Contains("createdon"));
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("owninguser").Id);
        }

        [Fact]
        public void Update_RefreshesModifiedBy_AndKeepsCreatedBy()
        {
            Guid creator = Guid.NewGuid();
            Guid editor = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(creator);

            Guid id = crm.Create(new Entity("account") { ["name"] = "A" });
            crm.WithCurrentUser(editor);
            crm.Update(new Entity("account") { Id = id, ["name"] = "B" });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(creator, stored.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.Equal(editor, stored.GetAttributeValue<EntityReference>("modifiedby").Id);
        }

        [Fact]
        public void Update_AssigningTeamOwner_RederivesOwningColumns()
        {
            Guid user = Guid.NewGuid();
            Guid team = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(user);

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            crm.Update(new Entity("account") { Id = id, ["ownerid"] = new EntityReference("team", team) });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(team, stored.GetAttributeValue<EntityReference>("owningteam").Id);
            Assert.Equal("team", stored.GetAttributeValue<EntityReference>("owningteam").LogicalName);
            Assert.False(stored.Contains("owninguser"));
        }

        [Fact]
        public void EarlyBound_OwnershipInferredFromOwnerIdProperty()
        {
            Guid user = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(user);

            // OwnedEntity exposes an OwnerId property -> inferred user-owned (gets owner columns).
            Guid owned = crm.Create(new OwnedEntity());
            // FakeAccount is a minimal early-bound stub with no OwnerId property -> inferred organization-owned.
            Guid bare = crm.Create(new FakeAccount { Name = "x" });

            Assert.True(crm.Retrieve("zava_owned", owned, new ColumnSet(true)).Contains("ownerid"));
            Assert.False(crm.Retrieve("account", bare, new ColumnSet(true)).Contains("ownerid"));
        }

        [Fact]
        public void Create_OrganizationOwned_StampsOrganizationId()
        {
            Guid org = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .AsOrganizationOwned("savedquery")
                .WithOrganizationId(org);

            Guid id = crm.Create(new Entity("savedquery") { ["name"] = "View" });
            Entity stored = crm.Retrieve("savedquery", id, new ColumnSet(true));

            Assert.Equal(org, stored.GetAttributeValue<EntityReference>("organizationid").Id);
            Assert.Equal("organization", stored.GetAttributeValue<EntityReference>("organizationid").LogicalName);
            Assert.False(stored.Contains("ownerid"));
        }

        [Fact]
        public void Clock_MakesTimestampsDeterministic()
        {
            DateTime fixedNow = new DateTime(2026, 6, 23, 8, 30, 0, DateTimeKind.Utc);
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithClock(() => fixedNow);

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(fixedNow, stored.GetAttributeValue<DateTime>("createdon"));
            Assert.Equal(fixedNow, stored.GetAttributeValue<DateTime>("modifiedon"));
        }

        [Fact]
        public void Update_AlwaysServerStampsModifiedOn_IgnoringCallerSuppliedValue()
        {
            DateTime serverNow = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithClock(() => serverNow);
            Guid id = crm.Create(new Entity("account") { ["name"] = "A" });

            // Dataverse never honours a caller-supplied modifiedon on Update; it always stamps server time.
            DateTime callerSupplied = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            crm.Update(new Entity("account") { Id = id, ["name"] = "B", ["modifiedon"] = callerSupplied });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(serverNow, stored.GetAttributeValue<DateTime>("modifiedon"));
        }

        [Fact]
        public void Retrieve_ReturnsDeepCopy_MutatingRetrievedCollectionDoesNotAffectStore()
        {
            // Real Dataverse returns an independent copy on every read. A retrieved EntityCollection must not
            // alias the stored row, or a plugin mutating it in place would silently corrupt the store.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            EntityCollection parties = new EntityCollection { EntityName = "activityparty" };
            parties.Entities.Add(new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()) });
            Guid id = crm.Create(new Entity("email") { ["to"] = parties });

            Entity first = crm.Retrieve("email", id, new ColumnSet(true));
            first.GetAttributeValue<EntityCollection>("to").Entities.Clear();

            Entity second = crm.Retrieve("email", id, new ColumnSet(true));
            Assert.Single(second.GetAttributeValue<EntityCollection>("to").Entities);
        }

        [Fact]
        public void Create_StampsDefaultStateStatusAndVersion()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(0, stored.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(1, stored.GetAttributeValue<OptionSetValue>("statuscode").Value);
            Assert.True(stored.GetAttributeValue<long>("versionnumber") > 0);
        }

        [Fact]
        public void Create_HonoursCallerSuppliedStateAndStatus()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Guid id = crm.Create(new Entity("account")
            {
                ["name"] = "Zava",
                ["statecode"] = new OptionSetValue(1),
                ["statuscode"] = new OptionSetValue(2),
            });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(1, stored.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(2, stored.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        [Fact]
        public void Create_OverriddenCreatedOn_BackdatesCreatedOn()
        {
            DateTime historical = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithClock(() => new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc));

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava", ["overriddencreatedon"] = historical });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(historical, stored.GetAttributeValue<DateTime>("createdon"));
        }

        [Fact]
        public void Update_BumpsVersionNumber()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Guid id = crm.Create(new Entity("account") { ["name"] = "A" });
            long created = crm.Retrieve("account", id, new ColumnSet("versionnumber")).GetAttributeValue<long>("versionnumber");

            crm.Update(new Entity("account") { Id = id, ["name"] = "B" });
            long updated = crm.Retrieve("account", id, new ColumnSet("versionnumber")).GetAttributeValue<long>("versionnumber");

            Assert.True(updated > created);
        }

        [Fact]
        public void Create_Virtual_HasNoStateStatusOrVersion()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().AsVirtual("zava_externaldata");

            Guid id = crm.Create(new Entity("zava_externaldata") { ["name"] = "x" });
            Entity stored = crm.Retrieve("zava_externaldata", id, new ColumnSet(true));

            Assert.False(stored.Contains("statecode"));
            Assert.False(stored.Contains("versionnumber"));
        }

        [Fact]
        public void WithIdSequence_UsesProvidedGenerator()
        {
            Guid[] ids = { Guid.NewGuid(), Guid.NewGuid() };
            int next = 0;
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithIdSequence(() => ids[next++]);

            Assert.Equal(ids[0], crm.Create(new Entity("account") { ["name"] = "A" }));
            Assert.Equal(ids[1], crm.Create(new Entity("account") { ["name"] = "B" }));
        }

        [Fact]
        public void WithIdSequence_Sequential_ProducesDeterministicIds()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithIdSequence();

            Assert.Equal(SequentialIds.At(1), crm.Create(new Entity("account") { ["name"] = "A" }));
            Assert.Equal(SequentialIds.At(2), crm.Create(new Entity("account") { ["name"] = "B" }));
        }

        [Fact]
        public void Execute_AssignRequest_RederivesOwningColumns()
        {
            Guid user = Guid.NewGuid();
            Guid team = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(user);

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            crm.Execute(new AssignRequest
            {
                Target = new EntityReference("account", id),
                Assignee = new EntityReference("team", team),
            });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(team, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(team, stored.GetAttributeValue<EntityReference>("owningteam").Id);
            Assert.False(stored.Contains("owninguser"));
        }

        [Fact]
        public void Bulk_CreateMultiple_StampsSystemColumns()
        {
            Guid user = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(user);

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new Entity("account") { ["name"] = "A" });
            targets.Entities.Add(new Entity("account") { ["name"] = "B" });

            CreateMultipleResponse response = (CreateMultipleResponse)crm.Execute(new CreateMultipleRequest { Targets = targets });

            Assert.Equal(2, response.Ids.Length);
            foreach (Guid id in response.Ids)
            {
                Entity stored = crm.Retrieve("account", id, new ColumnSet(true));
                Assert.Equal(user, stored.GetAttributeValue<EntityReference>("createdby").Id);
                Assert.Equal(user, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            }
        }

        [Fact]
        public void Associate_CreatesIntersectRows_AndNToNQueryJoins()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

            Guid accountId = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Guid contactId = crm.Create(new Entity("contact") { ["fullname"] = "Jane" });
            crm.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Assert.Single(crm.GetAll("account_contact"));

            // Retrieve the account's contacts by joining through the intersect entity.
            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account_contact", "contactid", "contactid", JoinOperator.Inner);
            link.LinkCriteria.AddCondition("accountid", ConditionOperator.Equal, accountId);

            Entity related = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Jane", related.GetAttributeValue<string>("fullname"));
        }

        [Fact]
        public void Associate_IsIdempotent()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

            Guid accountId = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Guid contactId = crm.Create(new Entity("contact") { ["fullname"] = "Jane" });
            Relationship rel = new Relationship("account_contacts");
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("contact", contactId) };

            crm.Associate("account", accountId, rel, related);
            crm.Associate("account", accountId, rel, related);

            Assert.Single(crm.GetAll("account_contact"));
        }

        [Fact]
        public void Disassociate_RemovesIntersectRows()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

            Guid accountId = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Guid contactId = crm.Create(new Entity("contact") { ["fullname"] = "Jane" });
            Relationship rel = new Relationship("account_contacts");
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("contact", contactId) };

            crm.Associate("account", accountId, rel, related);
            crm.Disassociate("account", accountId, rel, related);

            Assert.Empty(crm.GetAll("account_contact"));
        }

        [Fact]
        public void WithManyToMany_TypedOverload_DerivesNamesFromEarlyBound()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany<FakeAccountContact, FakeAccount, FakeContact>(
                    "account_contacts", ac => ac.AccountId, ac => ac.ContactId);

            Guid accountId = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Guid contactId = crm.Create(new Entity("contact") { ["fullname"] = "Jane" });
            crm.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Entity row = Assert.Single(crm.GetAll("account_contact"));
            Assert.Equal(accountId, row.GetAttributeValue<EntityReference>("accountid").Id);
            Assert.Equal(contactId, row.GetAttributeValue<EntityReference>("contactid").Id);
        }

        [Fact]
        public void Associate_UndeclaredRelationship_Throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid accountId = crm.Create(new Entity("account") { ["name"] = "Zava" });

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                crm.Associate("account", accountId, new Relationship("account_contacts"),
                    new EntityReferenceCollection { new EntityReference("contact", Guid.NewGuid()) }));
            Assert.Contains("WithManyToMany", ex.Message);
        }

        [Fact]
        public void Execute_RetrieveEntityRequest_ReturnsRegisteredMetadata()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account")
                    .PrimaryId("accountid")
                    .PrimaryName("name")
                    .Ownership(OwnershipTypes.UserOwned)
                    .Picklist("statuscode", (1, "Active"), (2, "Inactive"))
                    .Build());

            RetrieveEntityResponse response = (RetrieveEntityResponse)crm.Execute(new RetrieveEntityRequest
            {
                LogicalName = "account",
                EntityFilters = EntityFilters.All,
            });
            EntityMetadata metadata = response.EntityMetadata;

            Assert.Equal("account", metadata.LogicalName);
            Assert.Equal("accountid", metadata.PrimaryIdAttribute);
            Assert.Equal("name", metadata.PrimaryNameAttribute);
            Assert.Equal(OwnershipTypes.UserOwned, metadata.OwnershipType);
            Assert.Contains(metadata.Attributes, a => a.LogicalName == "statuscode" && a.AttributeType == AttributeTypeCode.Picklist);
        }

        [Fact]
        public void Execute_RetrieveAttributeRequest_ReturnsTheAttribute()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("account")
                    .PrimaryId("accountid")
                    .Attribute("name", AttributeTypeCode.String)
                    .Build());

            RetrieveAttributeResponse response = (RetrieveAttributeResponse)crm.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = "account",
                LogicalName = "name",
            });

            Assert.Equal("name", response.AttributeMetadata.LogicalName);
            Assert.Equal(AttributeTypeCode.String, response.AttributeMetadata.AttributeType);
        }

        [Fact]
        public void Execute_RetrieveAllEntitiesRequest_ReturnsEverythingRegistered()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(
                    FakeEntityMetadata.For("account").PrimaryId("accountid").Build(),
                    FakeEntityMetadata.For("contact").PrimaryId("contactid").Build());

            RetrieveAllEntitiesResponse response = (RetrieveAllEntitiesResponse)crm.Execute(new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
            });

            Assert.Equal(2, response.EntityMetadata.Length);
        }

        [Fact]
        public void Execute_RetrieveEntityRequest_Unregistered_Throws()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                crm.Execute(new RetrieveEntityRequest { LogicalName = "account", EntityFilters = EntityFilters.Entity }));
            Assert.Contains("WithEntityMetadata", ex.Message);
        }

        [Fact]
        public void Create_WithoutCurrentUser_UsesStableDefaultCallingUser()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Entity stored = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal(InMemoryOrganizationService.DefaultCallingUser, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(InMemoryOrganizationService.DefaultCallingUser, stored.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.NotEqual(Guid.Empty, InMemoryOrganizationService.DefaultCallingUser);
        }

        [Fact]
        public void AsVirtual_ReadOnly_RejectsWrites()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().AsVirtual("zava_external", readOnly: true);

            Assert.Throws<InvalidOperationException>(() => crm.Create(new Entity("zava_external") { ["name"] = "x" }));
        }

        [Fact]
        public void AsVirtual_WithoutReadOnly_StillAllowsWrites()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().AsVirtual("zava_external");

            Guid id = crm.Create(new Entity("zava_external") { ["name"] = "x" });

            Assert.NotEqual(Guid.Empty, id);
        }

        [Fact]
        public void Execute_ExecuteMultiple_RunsAllChildRequests_AndReturnsResponses()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            ExecuteMultipleRequest request = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true },
                Requests = new OrganizationRequestCollection(),
            };
            request.Requests.Add(new CreateRequest { Target = new Entity("account") { ["name"] = "A" } });
            request.Requests.Add(new CreateRequest { Target = new Entity("account") { ["name"] = "B" } });

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)crm.Execute(request);

            Assert.False(response.IsFaulted);
            Assert.Equal(2, response.Responses.Count);
            Assert.Equal(2, crm.GetAll("account").Count);
        }

        [Fact]
        public void Execute_ExecuteMultiple_ContinueOnError_CollectsFaultsAndKeepsGoing()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            ExecuteMultipleRequest request = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
                Requests = new OrganizationRequestCollection(),
            };
            request.Requests.Add(new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "missing" } });
            request.Requests.Add(new CreateRequest { Target = new Entity("account") { ["name"] = "B" } });

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)crm.Execute(request);

            Assert.True(response.IsFaulted);
            Assert.NotNull(response.Responses[0].Fault);
            // The second request still ran despite the first fault.
            Assert.Single(crm.GetAll("account"));
        }

        [Fact]
        public void Execute_ExecuteTransaction_RollsBackEverythingWhenAnyRequestFails()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            crm.Create(new Entity("account") { ["name"] = "Seed" });

            ExecuteTransactionRequest request = new ExecuteTransactionRequest
            {
                Requests = new OrganizationRequestCollection(),
                ReturnResponses = true,
            };
            request.Requests.Add(new CreateRequest { Target = new Entity("account") { ["name"] = "New" } });
            request.Requests.Add(new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "missing" } });

            Assert.Throws<InvalidOperationException>(() => crm.Execute(request));

            // The "New" account created earlier in the batch was rolled back; only the seed remains.
            Assert.Single(crm.GetAll("account"));
        }

        [Fact]
        public void Associate_ResolvesIntersectFromRegisteredMetadata_WithoutWithManyToMany()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(
                    FakeEntityMetadata.For("account")
                        .ManyToMany("account_contacts", "accountcontact", "account", "accountid", "contact", "contactid")
                        .Build());
            crm.Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId });

            crm.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Entity row = Assert.Single(crm.GetAll("accountcontact"));
            Assert.Equal(accountId, row.GetAttributeValue<EntityReference>("accountid").Id);
            Assert.Equal(contactId, row.GetAttributeValue<EntityReference>("contactid").Id);
        }

        // Early-bound stubs for the typed WithManyToMany overload (intersect + the two side tables).
        [EntityLogicalName("contact")]
        private sealed class FakeContact : Entity
        {
            public FakeContact()
                : base("contact")
            {
            }
        }

        [EntityLogicalName("account_contact")]
        private sealed class FakeAccountContact : Entity
        {
            public FakeAccountContact()
                : base("account_contact")
            {
            }

            [AttributeLogicalName("accountid")]
            public Guid? AccountId
            {
                get => GetAttributeValue<Guid?>("accountid");
                set => SetAttributeValue("accountid", value);
            }

            [AttributeLogicalName("contactid")]
            public Guid? ContactId
            {
                get => GetAttributeValue<Guid?>("contactid");
                set => SetAttributeValue("contactid", value);
            }
        }

        // Early-bound stub WITH an OwnerId property, to exercise user-owned inference.
        [EntityLogicalName("zava_owned")]
        private sealed class OwnedEntity : Entity
        {
            public OwnedEntity()
                : base("zava_owned")
            {
            }

            [AttributeLogicalName("ownerid")]
            public EntityReference OwnerId
            {
                get => GetAttributeValue<EntityReference>("ownerid");
                set => SetAttributeValue("ownerid", value);
            }
        }
    }
}
