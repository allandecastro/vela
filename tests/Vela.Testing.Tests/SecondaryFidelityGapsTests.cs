using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Vela.Constants;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // #451: the secondary gaps between the harness and Dataverse. Platform behaviour marked "verified live" was
    // probed on a dev environment on 2026-10-11.
    public sealed class SecondaryFidelityGapsTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000451");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-000000000451");

        private static Guid UploadFile(InMemoryOrganizationService crm, string entity, Guid id, string column)
        {
            string token = (string)crm.Execute(new OrganizationRequest("InitializeFileBlocksUpload")
            {
                Parameters = { ["Target"] = new EntityReference(entity, id), ["FileAttributeName"] = column, ["FileName"] = "f.bin" }
            }).Results["FileContinuationToken"];
            crm.Execute(new OrganizationRequest("UploadBlock")
            {
                Parameters = { ["FileContinuationToken"] = token, ["BlockId"] = "b1", ["BlockData"] = new byte[] { 1 } }
            });
            return (Guid)crm.Execute(new OrganizationRequest("CommitFileBlocksUpload")
            {
                Parameters = { ["FileContinuationToken"] = token, ["FileName"] = "f.bin", ["BlockList"] = new[] { "b1" } }
            }).Results["FileId"];
        }

        private static int ErrorCode(Action action) =>
            Assert.Throws<FaultException<OrganizationServiceFault>>(action).Detail.ErrorCode;

        // --- 1: a Delete's PreValidation runs before the Restrict check ------------------------------------

        [Fact]
        public void A_Restrict_blocked_Delete_still_runs_its_PreValidation_step()
        {
            Guid account = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithReferentialRelationship("account", "contact", "parentcustomerid", restrictDelete: true)
                .Register<GapDeletePreValidation>()
                .Seed(new FakeAccount { Id = account, Name = "Parent" })
                .Seed(new Entity("contact", Guid.NewGuid()) { ["parentcustomerid"] = new EntityReference("account", account) });

            Assert.Equal(unchecked((int)0x80040227), ErrorCode(() => pipeline.Service.Delete("account", account)));
            Assert.Equal(new[] { PluginStages.PreValidation }, pipeline.ExecutionLog.Select(e => e.Stage).ToArray());
        }

        // --- 2: a failed write leaves the store untouched --------------------------------------------------

        [Fact]
        public void An_Update_whose_lookup_key_matches_nothing_does_not_half_apply()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");
            Guid contact = crm.Create(new Entity("contact") { ["lastname"] = "Old" });

            Assert.Equal(unchecked((int)0x80060891), ErrorCode(() => crm.Update(new Entity("contact", contact)
            {
                ["lastname"] = "New",
                ["parentcustomerid"] = new EntityReference("account", "accountnumber", "missing"),
            })));

            Entity stored = crm.Retrieve("contact", contact, new ColumnSet(true));
            Assert.Equal("Old", stored.GetAttributeValue<string>("lastname"));
            Assert.False(stored.Contains("parentcustomerid"));
        }

        [Fact]
        public void An_Associate_with_one_already_associated_record_associates_none()
        {
            Guid account = Guid.NewGuid();
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account", account), new Entity("contact", first), new Entity("contact", second));
            crm.Associate("account", account, new Relationship("account_contacts"), new EntityReferenceCollection { new EntityReference("contact", second) });

            Assert.ThrowsAny<Exception>(() => crm.Associate("account", account, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", first), new EntityReference("contact", second) }));

            Assert.Single(crm.GetAll("account_contact")); // only the earlier pair, not "first"
        }

        [Fact]
        public void A_one_to_many_Associate_that_fails_on_a_later_child_links_none()
        {
            Guid account = Guid.NewGuid();
            Guid mine = Guid.NewGuid();
            Guid theirs = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithReferentialRelationship("account", "contact", "parentcustomerid")
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("linker", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.AppendTo)
                        .Grant("contact", AccessLevel.Organization, Privilege.Read, Privilege.Append)
                        .Grant("contact", AccessLevel.User, Privilege.Write))
                    .User(Alice, SalesBu, "linker"))
                .Seed(
                    OwnedBy(new Entity("account", account), Guid.NewGuid()),
                    OwnedBy(new Entity("contact", mine), Alice),
                    OwnedBy(new Entity("contact", theirs), Guid.NewGuid()));

            // Linking "mine" succeeds, then linking "theirs" is denied (no Write on it): neither stays linked.
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => crm.Associate("account", account, new Relationship("account_contact"),
                new EntityReferenceCollection { new EntityReference("contact", mine), new EntityReference("contact", theirs) }));

            Assert.False(crm.Retrieve("contact", mine, new ColumnSet(true)).Contains("parentcustomerid"));
        }

        private static Entity OwnedBy(Entity record, Guid user)
        {
            record["ownerid"] = new EntityReference("systemuser", user);
            record["owningbusinessunit"] = new EntityReference("businessunit", SalesBu);
            return record;
        }

        // --- 3: images carry only the columns the caller may read -----------------------------------------

        [Theory]
        [InlineData(false, "s20 pre:absent|s40 pre:absent post:absent")]
        [InlineData(true, "s20 pre:50000|s40 pre:50000 post:50000")]
        public void Step_images_mask_a_secured_column_the_caller_cannot_read(bool canRead, string expected)
        {
            Guid account = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s =>
                {
                    s.BusinessUnit(SalesBu)
                        .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write))
                        .User(Alice, SalesBu, "editor")
                        .SecureColumn("account", "creditlimit");
                    if (canRead)
                    {
                        s.FieldPermission(Alice, "account", "creditlimit", read: true);
                    }
                })
                .Register<GapSecuredImages>()
                .Seed(OwnedBy(new Entity("account", account) { ["name"] = "Zava", ["creditlimit"] = new Money(50000m) }, Alice));
            GapSecuredImages.Seen.Clear();

            pipeline.Service.Update(new Entity("account", account) { ["name"] = "Renamed" });

            Assert.Equal(expected, string.Join("|", GapSecuredImages.Seen));
        }

        [Fact]
        public void Bulk_step_images_mask_a_secured_column_the_caller_cannot_read()
        {
            Guid account = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("editor", r => r.Grant("account", AccessLevel.Organization, Privilege.Read, Privilege.Write))
                    .User(Alice, SalesBu, "editor")
                    .SecureColumn("account", "creditlimit"))
                .Register<GapSecuredBulkImages>()
                .Seed(OwnedBy(new Entity("account", account) { ["name"] = "Zava", ["creditlimit"] = new Money(50000m) }, Alice));
            GapSecuredBulkImages.Seen.Clear();

            pipeline.Service.Update(new Entity("account", account) { ["name"] = "Renamed" });

            // Every image of the collections, the platform's PreBusinessEntity / PostBusinessEntity included.
            Assert.NotEmpty(GapSecuredBulkImages.Seen);
            Assert.All(GapSecuredBulkImages.Seen, seen => Assert.EndsWith(":absent", seen, StringComparison.Ordinal));
        }

        // --- 4: DeleteFile clears its own column and checks access -----------------------------------------

        [Fact]
        public void DeleteFile_clears_only_the_column_the_file_was_uploaded_to()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid account = crm.Create(new Entity("account"));
            Guid fileId = UploadFile(crm, "account", account, "vela_document");
            Guid bystander = crm.Create(new Entity("contact") { ["vela_reference"] = fileId });

            crm.Execute(new OrganizationRequest("DeleteFile") { Parameters = { ["FileId"] = fileId } });

            Assert.False(crm.Retrieve("account", account, new ColumnSet(true)).Contains("vela_document"));
            Assert.Equal(fileId, crm.Retrieve("contact", bystander, new ColumnSet(true)).GetAttributeValue<Guid>("vela_reference"));
            Assert.Equal(unchecked((int)0x80040217), ErrorCode(() => crm.Execute(new OrganizationRequest("DeleteFile") { Parameters = { ["FileId"] = fileId } })));
        }

        [Fact]
        public void DeleteFile_needs_write_access_on_the_record()
        {
            Guid account = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("owner", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Read)
                        .Grant("account", AccessLevel.User, Privilege.Write, Privilege.Assign))
                    .User(Alice, SalesBu, "owner"))
                .Seed(OwnedBy(new Entity("account", account), Alice));
            Guid fileId = UploadFile(crm, "account", account, "vela_document");
            crm.Update(new Entity("account", account) { ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()) });

            // Alice no longer owns the record, so she has no Write on it: the file stays.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Execute(new OrganizationRequest("DeleteFile") { Parameters = { ["FileId"] = fileId } }));
            Assert.Equal(fileId, crm.Retrieve("account", account, new ColumnSet(true)).GetAttributeValue<Guid>("vela_document"));
        }

        // --- 6: ownerid cannot be cleared ------------------------------------------------------------------

        [Fact]
        public void An_Update_that_clears_ownerid_faults_instead_of_reassigning_to_the_caller()
        {
            Guid owner = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithCurrentUser(Guid.NewGuid());
            Guid account = crm.Create(new Entity("account") { ["ownerid"] = new EntityReference("systemuser", owner) });

            Assert.Equal(unchecked((int)0x80040203), ErrorCode(() => crm.Update(new Entity("account", account) { ["ownerid"] = null })));
            Assert.Equal(owner, crm.Retrieve("account", account, new ColumnSet("ownerid")).GetAttributeValue<EntityReference>("ownerid").Id);
        }

        // --- 7: with security on, a lookup must point at an existing record -------------------------------

        [Fact]
        public void With_security_on_a_lookup_to_a_missing_record_faults_as_on_the_platform()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("all", r => r
                        .Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read, Privilege.Write, Privilege.Append, Privilege.AppendTo))
                    .User(Alice, SalesBu, "all"));
            Guid account = crm.Create(new Entity("account") { ["name"] = "A" });
            EntityReference missing = new EntityReference("account", Guid.NewGuid());

            Assert.Equal(unchecked((int)0x80040217), ErrorCode(() => crm.Create(new Entity("account") { ["parentaccountid"] = missing })));
            Assert.Equal(unchecked((int)0x80040502), ErrorCode(() => crm.Update(new Entity("account", account) { ["parentaccountid"] = missing })));

            crm.Update(new Entity("account", account) { ["parentaccountid"] = new EntityReference("account", crm.Create(new Entity("account"))) });
        }

        [Fact]
        public void With_security_off_a_lookup_may_still_point_at_a_record_the_test_never_seeded()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid task = crm.Create(new Entity("task") { ["regardingobjectid"] = new EntityReference("account", Guid.NewGuid()) });
            Assert.True(crm.Retrieve("task", task, new ColumnSet(true)).Contains("regardingobjectid"));
        }

        // --- 8: sharing and record teams need the record ---------------------------------------------------

        [Fact]
        public void Sharing_requests_on_a_missing_record_fault_with_not_found()
        {
            Guid templateId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAccessTeamTemplate(templateId, AccessRights.ReadAccess);
            EntityReference ghost = new EntityReference("account", Guid.NewGuid());
            EntityReference user = new EntityReference("systemuser", Guid.NewGuid());
            PrincipalAccess read = new PrincipalAccess { Principal = user, AccessMask = AccessRights.ReadAccess };

            const int notFound = unchecked((int)0x80040217);
            Assert.Equal(notFound, ErrorCode(() => crm.Execute(new GrantAccessRequest { Target = ghost, PrincipalAccess = read })));
            Assert.Equal(notFound, ErrorCode(() => crm.Execute(new ModifyAccessRequest { Target = ghost, PrincipalAccess = read })));
            Assert.Equal(notFound, ErrorCode(() => crm.Execute(new RevokeAccessRequest { Target = ghost, Revokee = user })));
            Assert.Equal(notFound, ErrorCode(() => crm.Execute(new AddUserToRecordTeamRequest { Record = ghost, SystemUserId = user.Id, TeamTemplateId = templateId })));
            Assert.Equal(notFound, ErrorCode(() => crm.Execute(new RemoveUserFromRecordTeamRequest { Record = ghost, SystemUserId = user.Id, TeamTemplateId = templateId })));
        }

        // --- 11: Distinct is case-insensitive --------------------------------------------------------------

        [Fact]
        public void Distinct_treats_values_differing_only_in_case_as_one_row()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account", Guid.NewGuid()) { ["name"] = "Abc" },
                new Entity("account", Guid.NewGuid()) { ["name"] = "ABC" },
                new Entity("account", Guid.NewGuid()) { ["name"] = "Other" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), Distinct = true };
            query.AddOrder("name", OrderType.Ascending);

            Assert.Equal(2, crm.RetrieveMultiple(query).Entities.Count);
        }

        // --- 12: paging limits -----------------------------------------------------------------------------

        [Fact]
        public void A_query_with_no_page_size_returns_a_5000_row_page()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(Enumerable.Range(0, 5001).Select(_ => new Entity("account", Guid.NewGuid())).ToArray());

            EntityCollection first = crm.RetrieveMultiple(new QueryExpression("account"));
            Assert.Equal(5000, first.Entities.Count);
            Assert.True(first.MoreRecords);

            QueryExpression next = new QueryExpression("account") { PageInfo = new PagingInfo { PageNumber = 2, Count = 10000 } };
            Assert.Single(crm.RetrieveMultiple(next).Entities); // 10,000 is capped at 5,000 too
        }

        [Fact]
        public void FetchXml_top_with_returntotalrecordcount_is_rejected()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Assert.Equal(unchecked((int)0x80040203), ErrorCode(() => crm.RetrieveMultiple(new FetchExpression(
                "<fetch top='2' returntotalrecordcount='true'><entity name='account'><attribute name='name'/></entity></fetch>"))));
        }

        // --- 13: Merge follows the relationship's Merge behaviour and moves N:N rows -------------------------

        [Fact]
        public void Merge_reparents_only_over_cascading_relationships_and_moves_the_N_to_N_rows()
        {
            Guid master = Guid.NewGuid();
            Guid subordinate = Guid.NewGuid();
            Guid lead = Guid.NewGuid();
            Guid sharedLead = Guid.NewGuid();
            Guid referential = Guid.NewGuid();
            Guid pinned = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid")
                .WithRelationship("account", "task", "regardingobjectid", new CascadeConfiguration { Merge = CascadeType.NoCascade })
                .WithManyToMany("accountleads_association", "accountleads", "account", "accountid", "lead", "leadid")
                .Seed(
                    new Entity("account", master) { ["name"] = "Master" },
                    new Entity("account", subordinate) { ["name"] = "Sub" },
                    new Entity("lead", lead),
                    new Entity("lead", sharedLead),
                    new Entity("contact", referential) { ["parentcustomerid"] = new EntityReference("account", subordinate) },
                    new Entity("task", pinned) { ["regardingobjectid"] = new EntityReference("account", subordinate) });
            Relationship accountLeads = new Relationship("accountleads_association");
            crm.Associate("account", subordinate, accountLeads, new EntityReferenceCollection { new EntityReference("lead", lead), new EntityReference("lead", sharedLead) });
            crm.Associate("account", master, accountLeads, new EntityReferenceCollection { new EntityReference("lead", sharedLead) });

            crm.Execute(new MergeRequest { Target = new EntityReference("account", master), SubordinateId = subordinate, UpdateContent = new Entity("account") });

            Assert.Equal(master, crm.Retrieve("contact", referential, new ColumnSet(true)).GetAttributeValue<EntityReference>("parentcustomerid").Id);
            Assert.Equal(subordinate, crm.Retrieve("task", pinned, new ColumnSet(true)).GetAttributeValue<EntityReference>("regardingobjectid").Id);

            List<(Guid Account, Guid Lead)> rows = crm.GetAll("accountleads")
                .Select(row => (row.GetAttributeValue<EntityReference>("accountid").Id, row.GetAttributeValue<EntityReference>("leadid").Id))
                .OrderBy(row => row.Item2)
                .ToList();
            Assert.Equal(new[] { (master, lead), (master, sharedLead) }.OrderBy(row => row.Item2), rows);
        }

        // --- 14: scopes disposed out of order --------------------------------------------------------------

        [Fact]
        public void Disposing_an_outer_scope_first_keeps_the_inner_scope_dimensions()
        {
            RecordingLogger logger = new RecordingLogger();
            IDisposable outer = logger.BeginScope(new Dictionary<string, object?> { ["outer"] = 1 });
            IDisposable inner = logger.BeginScope(new Dictionary<string, object?> { ["inner"] = 2 });

            outer.Dispose();
            logger.LogInformation("after");
            inner.Dispose();
            inner.Dispose(); // a second dispose is a no-op
            logger.LogInformation("none");

            Assert.Equal(new[] { "inner" }, logger.Entries[0].Scope.Keys.ToArray());
            Assert.Empty(logger.Entries[1].Scope);
        }

        // --- 15: SequentialIds bounds ----------------------------------------------------------------------

        [Fact]
        public void SequentialIds_rejects_zero_and_indexes_past_twelve_digits()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SequentialIds.At(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SequentialIds.At(SequentialIds.MaxIndex + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SequentialIds.Starting(0));
            Assert.Equal(new Guid("00000000-0000-0000-0000-999999999999"), SequentialIds.At(SequentialIds.MaxIndex));
        }

        // --- 16: an unreadable registration fails the run --------------------------------------------------

        [Fact]
        public void A_plugin_whose_registration_throws_fails_instead_of_running_under_the_default_trigger()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginContext.For<FakeAccount>().Execute(new GapBrokenRegistration()));
            Assert.Contains(nameof(GapBrokenRegistration), ex.Message, StringComparison.Ordinal);
            Assert.Contains("bad registration", ex.Message, StringComparison.Ordinal);
            Assert.False(GapBrokenRegistration.Ran);
        }
    }

    public sealed class GapSecuredImages : EntityPluginBase<FakeAccount>
    {
        public static readonly List<string> Seen = new List<string>();

        public GapSecuredImages(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).WithPreImage()
                .Handle(ctx => Seen.Add("s20 pre:" + Secured(ctx.PluginExecutionContext.PreEntityImages)));
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage().WithPostImage()
                .Handle(ctx => Seen.Add("s40 pre:" + Secured(ctx.PluginExecutionContext.PreEntityImages)
                    + " post:" + Secured(ctx.PluginExecutionContext.PostEntityImages)));
        }

        private static string Secured(EntityImageCollection images)
        {
            Entity image = images.Values.Single();
            return image.Contains("creditlimit") ? image.GetAttributeValue<Money>("creditlimit").Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "absent";
        }
    }

    public sealed class GapSecuredBulkImages : EntityPluginBase<FakeAccount>
    {
        public static readonly List<string> Seen = new List<string>();

        public GapSecuredBulkImages(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage().WithPostImage()
                .Handle(ctx =>
                {
                    IPluginExecutionContext4 context = (IPluginExecutionContext4)ctx.PluginExecutionContext;
                    foreach (EntityImageCollection images in context.PreEntityImagesCollection.Concat(context.PostEntityImagesCollection))
                    {
                        foreach (KeyValuePair<string, Entity> image in images)
                        {
                            Seen.Add(image.Key + ":" + (image.Value.Contains("creditlimit") ? "has" : "absent"));
                        }
                    }
                });
    }

    public sealed class GapDeletePreValidation : EntityPluginBase<FakeAccount>
    {
        public GapDeletePreValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreValidation().Sync().Handle(_ => { });
    }

    public sealed class GapBrokenRegistration : IPlugin
    {
        public static bool Ran { get; private set; }

        public StepDefinition[] GetStepDefinitions() => throw new InvalidOperationException("bad registration");

        public void Execute(IServiceProvider serviceProvider) => Ran = true;
    }
}
