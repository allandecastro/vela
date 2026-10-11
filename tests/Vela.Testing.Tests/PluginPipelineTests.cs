using System;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class PluginPipelineTests
    {
        [Fact]
        public void Cascade_delete_fires_the_childrens_delete_plugins()
        {
            Guid account = Guid.NewGuid();
            Guid task = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<OnTaskDelete>()
                .WithParentalRelationship("account", "task", "regardingobjectid")
                .Seed(
                    new Entity("account", account),
                    new Entity("task", task) { ["regardingobjectid"] = new EntityReference("account", account) });

            pipeline.Service.Delete("account", account);

            Assert.Empty(pipeline.GetAll("task")); // cascaded away with the parent
            Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("OnTaskDelete")); // child Delete plugin ran
        }

        [Fact]
        public void SystemColumns_AreStampedOnRecordsWrittenThroughThePipeline()
        {
            Guid user = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(user)
                .AsUserOwned("account");

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity stored = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("createdby").Id);
            Assert.Equal(user, stored.GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Associate_PersistsIntersectRowsThroughThePipeline()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId });

            pipeline.Service.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Assert.Single(pipeline.GetAll("account_contact"));

            pipeline.Service.Disassociate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Assert.Empty(pipeline.GetAll("account_contact"));
        }

        [Fact]
        public void Cascade_OneWriteTriggersDownstreamPlugins()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<CreateTaskOnAccountCreate>()
                .Register<CreateNoteOnTaskCreate>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // Retrieve the precise records the cascade produced and compare their fields.
            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Task for Zava", task.Subject);

            Entity note = Assert.Single(pipeline.GetAll("annotation"));
            Assert.Equal("auto-note", note.GetAttributeValue<string>("subject"));

            Assert.Equal(2, pipeline.ExecutionLog.Count);
            Assert.Contains("CreateTaskOnAccountCreate", pipeline.ExecutionLog[0].PluginTypeName);
            Assert.Equal(1, pipeline.ExecutionLog[0].Depth);
            Assert.Contains("CreateNoteOnTaskCreate", pipeline.ExecutionLog[1].PluginTypeName);
            Assert.Equal(2, pipeline.ExecutionLog[1].Depth);
        }

        [Fact]
        public void Deep_insert_children_re_enter_the_pipeline_and_fire_their_create_plugins()   // #303
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithReferentialRelationship("account", "task", "regardingobjectid")
                .Register<CreateNoteOnTaskCreate>();

            FakeAccount account = new FakeAccount { Name = "Zava" };
            EntityCollection tasks = new EntityCollection();
            tasks.Entities.Add(new FakeTask { Subject = "Child task" });
            account.RelatedEntities[new Relationship("account_tasks")] = tasks;

            pipeline.Service.Create(account);

            // The deep-inserted task re-entered the pipeline: its Create plugin fired and created the note...
            Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("CreateNoteOnTaskCreate"));
            Assert.Single(pipeline.GetAll("annotation"));
            // ...and the task was persisted exactly once (the non-recursing store primitive avoids a double-persist).
            Assert.Single(pipeline.GetAll("task"));
        }

        [Fact]
        public void Owner_update_failure_rolls_back_the_standard_fields_too()   // #303
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ThrowOnOwnerUpdate>()
                .Seed(new FakeOwnable { Id = id, Name = "original" });

            FakeOwnable update = new FakeOwnable { Id = id, Name = "changed" };
            update["ownerid"] = new EntityReference("systemuser", Guid.NewGuid());

            // The step filtered on the owner throws; the name change in the same Update rolls back too - the record is
            // left untouched rather than half-updated.
            Assert.ThrowsAny<Exception>(() => pipeline.Service.Update(update));
            Assert.Equal("original", pipeline.GetAll("vela_ownable").Single().GetAttributeValue<string>("name"));
        }

        [Fact]
        public void CreatePostStep_SeesTheCreatedIdViaCtxResponse()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<StampResponseIdOnCreate>();

            Guid accountId = pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // The Post handler read ctx.Response.id and stamped it onto the follow-up task's regarding lookup.
            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal(accountId, task.GetAttributeValue<EntityReference>("regardingobjectid").Id);
        }

        [Fact]
        public void FilteringAttributes_FireOnlyWhenAFilteredColumnChanges()
        {
            Guid id = Guid.NewGuid();

            PluginPipeline fires = PluginPipeline.Empty().Register<OnNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });
            fires.Service.Update(new FakeAccount { Id = id, Name = "New" });
            Assert.Single(fires.ExecutionLog);

            PluginPipeline skips = PluginPipeline.Empty().Register<OnNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });
            FakeAccount otherColumn = new FakeAccount { Id = id };
            otherColumn["telephone1"] = "555";
            skips.Service.Update(otherColumn);
            Assert.Empty(skips.ExecutionLog);
        }

        [Fact]
        public void Images_PreAndPostAreBuiltFromStoreState()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CaptureImages>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            // The plugin recorded the pre/post image names onto a task.
            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Old->New", task.Subject);

            // The account itself was updated (retrieve the precise record and compare the field).
            FakeAccount account = pipeline.Service.Retrieve("account", id, new ColumnSet(true)).ToEntity<FakeAccount>();
            Assert.Equal("New", account.Name);
        }

        [Fact]
        public void Recursion_TripsThePipelineCap_AtTheDataverseDefaultDepth_AndRollsBack()
        {
            // The pipeline cap defaults to the Dataverse limit (8), the same value as the plugin's own guard, so the
            // pipeline stops the cascade before a step at depth 9 runs, as the platform would.
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecurseOnUpdate>()
                .Seed(new FakeAccount { Id = id, Name = "Seed" });

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Update(new FakeAccount { Id = id, Name = "trigger" }));
            Assert.Contains($"stopped at depth {Vela.Plugin.PluginBase.DefaultMaxDepth + 1} (cap {Vela.Plugin.PluginBase.DefaultMaxDepth})", ex.Message);
            Assert.Equal(Vela.Plugin.PluginBase.DefaultMaxDepth, pipeline.ExecutionLog.Max(e => e.Depth));

            Entity account = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal("Seed", account.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Recursion_TripsThePluginsDepthGuard_UnderARaisedPipelineCap_AndRollsBack()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecurseOnUpdate>()
                .WithMaxDepth(16)
                .Seed(new FakeAccount { Id = id, Name = "Seed" });

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Update(new FakeAccount { Id = id, Name = "trigger" }));
            Assert.Contains("prevent recursion", ex.Message);

            Entity account = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal("Seed", account.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Rollback_SyncExceptionAfterWriteUndoesTheOperation()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ThrowOnPostCreate>();

            Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Create(new FakeAccount { Name = "X" }));

            Assert.Empty(pipeline.GetAll("account"));
        }

        [Fact]
        public void AsyncStep_RunsInlineAfterCommit()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AsyncCreateNote>();

            pipeline.Service.Create(new FakeAccount { Name = "X" });

            Assert.Single(pipeline.GetAll("account"));
            Entity note = Assert.Single(pipeline.GetAll("annotation"));
            Assert.Equal("async-note", note.GetAttributeValue<string>("subject"));
            Assert.Contains(pipeline.ExecutionLog, entry => entry.Mode == ExecutionMode.Asynchronous);
        }

        [Fact]
        public void CustomApi_ExecutedThroughExecuteRequest()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<GreetApi>();

            OrganizationRequest request = new OrganizationRequest("vela_Greet");
            request["Name"] = "World";

            OrganizationResponse response = pipeline.Service.Execute(request);

            Assert.Equal("Hello World", response["Message"]);
        }

        [Fact]
        public void Tag_PassedOnTheRequest_IsVisibleToPluginsAsASharedVariable()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TagCapturingPlugin>();

            // 'tag' can only be carried by a named request + Execute (not the shortcut Create), per Dataverse.
            CreateRequest request = new CreateRequest { Target = new FakeAccount { Name = "Zava" } };
            request["tag"] = "from-client";
            pipeline.Service.Execute(request);

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("from-client", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void NoTag_LeavesTheSharedVariableAbsent()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TagCapturingPlugin>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("<no-tag>", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void BypassCustomPluginExecution_SkipsEveryStep_ButStillWritesTheData()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            UpdateRequest request = new UpdateRequest { Target = new FakeAccount { Id = id, Name = "New" } };
            request["BypassCustomPluginExecution"] = true;
            pipeline.Service.Execute(request);

            Assert.Empty(pipeline.ExecutionLog);
            Assert.Equal("New", pipeline.GetAll("account").Single().GetAttributeValue<string>("name"));
        }

        [Fact]
        public void BypassBusinessLogicExecution_SkipsByMode()
        {
            Guid id = Guid.NewGuid();

            // OnNameChange is synchronous, so CustomSync suppresses it...
            PluginPipeline bypassed = PluginPipeline.Empty().Register<OnNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });
            UpdateRequest sync = new UpdateRequest { Target = new FakeAccount { Id = id, Name = "New" } };
            sync["BypassBusinessLogicExecution"] = "CustomSync";
            bypassed.Service.Execute(sync);
            Assert.Empty(bypassed.ExecutionLog);

            // ...while CustomAsync leaves the synchronous step running.
            PluginPipeline fires = PluginPipeline.Empty().Register<OnNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });
            UpdateRequest async = new UpdateRequest { Target = new FakeAccount { Id = id, Name = "New" } };
            async["BypassBusinessLogicExecution"] = "CustomAsync";
            fires.Service.Execute(async);
            Assert.Single(fires.ExecutionLog);
        }

        [Fact]
        public void BypassBusinessLogicExecutionStepIds_SkipsTheNamedStep()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<KeyedNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            UpdateRequest request = new UpdateRequest { Target = new FakeAccount { Id = id, Name = "New" } };
            request["BypassBusinessLogicExecutionStepIds"] = new[] { KeyedNameChange.StepKey };
            pipeline.Service.Execute(request);

            Assert.Empty(pipeline.ExecutionLog);
        }

        [Fact]
        public void Update_CarryingOwnerAndStandardFields_FiresTheStepOnce()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecordUpdatePasses>()
                .Seed(new FakeOwnable { Id = id, Name = "Old" });

            FakeOwnable update = new FakeOwnable { Id = id, Name = "New" };
            update["ownerid"] = new EntityReference("systemuser", Guid.NewGuid());
            pipeline.Service.Update(update);

            // One execution with both fields in its Target (verified live), not a standard pass then an owner pass.
            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("pass:owner", task.GetAttributeValue<string>("subject"));

            Entity stored = pipeline.GetAll("vela_ownable").Single();
            Assert.Equal("New", stored.GetAttributeValue<string>("name"));
            Assert.Equal("systemuser", stored.GetAttributeValue<EntityReference>("ownerid").LogicalName);
        }

        [Fact]
        public void Update_WithOnlyStandardFields_FiresOnce()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecordUpdatePasses>()
                .Seed(new FakeOwnable { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeOwnable { Id = id, Name = "New" });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("pass:standard", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void SetState_RunsAStatecodeUpdate_AndFiresUpdatePlugins()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnStateChange>()
                .Seed(new FakeOwnable { Id = id, Name = "Zava" });

            SetStateRequest request = new SetStateRequest
            {
                EntityMoniker = new EntityReference("vela_ownable", id),
                State = new OptionSetValue(1),   // inactive
                Status = new OptionSetValue(-1)  // default from state
            };
            pipeline.Service.Execute(request);

            Entity stored = pipeline.GetAll("vela_ownable").Single();
            Assert.Equal(1, stored.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(2, stored.GetAttributeValue<OptionSetValue>("statuscode").Value); // defaulted from state
            Assert.Single(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("OnStateChange"));
        }

        [Fact]
        public void ModelledMessage_WithoutDataSimulation_StillFiresRegisteredSteps()
        {
            // SendEmail has no data simulation in the fake, but a registered SendEmail step must still fire like any
            // other plugin when the message is executed. Like the platform, the SendEmailRequest runs as the "Send"
            // sdkmessage on email, with no PrimaryEntityId, and ctx.Request is the typed SendEmailRequest.
            Guid emailId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnEmailSent>();

            pipeline.Service.Execute(new SendEmailRequest { EmailId = emailId, IssueSend = true });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal($"sent:{emailId}:Send:email:{Guid.Empty}", task.GetAttributeValue<string>("subject"));
            Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("OnEmailSent"));
        }

        [Fact]
        public void ModelledMessage_RunsOnTheTableOfItsMessage_NotOnItsTarget()
        {
            // AddToQueue carries a Target of any table, but the platform runs it on queueitem (verified live), so a
            // queueitem step fires and ctx.Request.Target / ctx.Request.DestinationQueueId are typed.
            Guid recordId = Guid.NewGuid();
            Guid queueId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnQueueItemAdded>();

            pipeline.Service.Execute(new AddToQueueRequest
            {
                Target = new EntityReference("account", recordId),
                DestinationQueueId = queueId
            });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal($"queue:{recordId}:{queueId}:queueitem", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void ModelledMessage_SharingAnSdkMessage_FiresOnlyTheStepOfItsTable()
        {
            // WinOpportunity and WinQuote both run as "Win": a WinQuoteRequest must fire the quote step, not the
            // opportunity one, as the step's table tells them apart.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnQuoteWon>().Register<OnOpportunityWon>();

            pipeline.Service.Execute(new WinQuoteRequest
            {
                QuoteClose = new Entity("quoteclose") { ["quoteid"] = new EntityReference("quote", Guid.NewGuid()) },
                Status = new OptionSetValue(4)
            });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("win:quote:Win", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void FromAssembly_AutoDiscoversAndFiresPlugins()
        {
            PluginPipeline pipeline = PluginPipeline.FromAssemblyOf<FakeAuto>();

            pipeline.Service.Create(new FakeAuto { Name = "auto" });

            Assert.Contains(pipeline.ExecutionLog, entry => entry.PluginTypeName.Contains("AutoDiscoveredPlugin"));
        }

        [Fact]
        public void Create_step_fires_only_when_the_request_sets_a_filtering_attribute()
        {
            // FilterOn applies to Create as it does to Update: the step is filtered on vela_name.
            PluginPipeline pipeline = PluginPipeline.FromAssemblyOf<FakeAuto>();

            pipeline.Service.Create(new FakeAuto());
            Assert.DoesNotContain(pipeline.ExecutionLog, entry => entry.PluginTypeName.Contains("AutoDiscoveredPlugin"));

            pipeline.Service.Create(new FakeAuto { Name = "set" });
            Assert.Single(pipeline.ExecutionLog, entry => entry.PluginTypeName.Contains("AutoDiscoveredPlugin"));
        }

        [Fact]
        public void CreateMultiple_FiresSingleCreateStepsPerRow()
        {
            // A step registered on Create fires once per row of a CreateMultiple batch (Dataverse merges the
            // single- and multiple-operation messages).
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CreateTaskOnAccountCreate>();

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Name = "A" });
            targets.Entities.Add(new FakeAccount { Name = "B" });
            pipeline.Service.Execute(new CreateMultipleRequest { Targets = targets });

            string[] subjects = pipeline.GetAll("task")
                .Select(t => t.GetAttributeValue<string>("subject"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "Task for A", "Task for B" }, subjects);
        }

        [Fact]
        public void CreateMultiple_FiresBulkStepOnceWithTheWholeTargetsCollection()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnAccountCreateMultiple>();

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Name = "A" });
            targets.Entities.Add(new FakeAccount { Name = "B" });
            targets.Entities.Add(new FakeAccount { Name = "C" });
            pipeline.Service.Execute(new CreateMultipleRequest { Targets = targets });

            // The bulk step fired exactly once and saw all three rows via ctx.BulkTargets.
            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("bulk:3", task.Subject);
            Assert.Single(pipeline.ExecutionLog, entry => entry.PluginTypeName.Contains("OnAccountCreateMultiple"));
        }

        [Fact]
        public void SingleCreate_AlsoFiresABulkStep_AsAOneRowBatch()
        {
            // The merge runs both ways: a CreateMultiple-registered step fires for a plain Create, seeing a
            // one-row Targets collection.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnAccountCreateMultiple>();

            pipeline.Service.Create(new FakeAccount { Name = "Solo" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("bulk:1", task.Subject);
        }

        [Fact]
        public void UpdateMultiple_FiresSingleUpdateStepsPerRow_FilteredPerRow()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnNameChange>()
                .Seed(new FakeAccount { Id = a, Name = "A" }, new FakeAccount { Id = b, Name = "B" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Id = a, Name = "A2" }); // name changed -> step fires
            FakeAccount phoneOnly = new FakeAccount { Id = b };
            phoneOnly["telephone1"] = "555"; // no filtered column changed -> step skipped
            targets.Entities.Add(phoneOnly);
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });

            // Only the row whose filtered column changed fired the per-row Update step.
            PipelineExecutionEntry fired = Assert.Single(pipeline.ExecutionLog);
            Assert.Contains("OnNameChange", fired.PluginTypeName);
            Assert.Equal("account", fired.EntityLogicalName);
        }

        [Fact]
        public void UpdateMultiple_BulkStep_ReadsPerRowPreAndPostImages()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CaptureBulkImages>()
                .Seed(new FakeAccount { Id = a, Name = "A-old" }, new FakeAccount { Id = b, Name = "B-old" });

            EntityCollection targets = new EntityCollection { EntityName = "account" };
            targets.Entities.Add(new FakeAccount { Id = a, Name = "A-new" });
            targets.Entities.Add(new FakeAccount { Id = b, Name = "B-new" });
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });

            string[] transitions = pipeline.GetAll("task")
                .Select(t => t.GetAttributeValue<string>("subject"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "A-old->A-new", "B-old->B-new" }, transitions);
        }

        [Fact]
        public void ExecuteMultiple_runs_each_child_through_the_pipeline_so_plugins_fire()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<CreateTaskOnAccountCreate>();

            ExecuteMultipleRequest batch = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true },
                Requests = new OrganizationRequestCollection(),
            };
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "A" } });
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "B" } });

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)pipeline.Service.Execute(batch);

            Assert.False(response.IsFaulted);
            Assert.Equal(2, pipeline.GetAll("account").Count());
            // The account Create plugin fired for each child; via the old store-direct path it would be 0 tasks.
            Assert.Equal(2, pipeline.GetAll("task").Count());
        }

        [Fact]
        public void Async_step_failure_is_recorded_not_surfaced_to_the_caller()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<AsyncThrowOnCreate>();

            // Real Dataverse runs async steps as decoupled system jobs; a failure never reaches the sync caller.
            Guid id = pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.NotEqual(Guid.Empty, id);              // the create returned normally
            Assert.Single(pipeline.GetAll("account"));    // the synchronous operation committed
            Exception failure = Assert.Single(pipeline.AsyncStepFailures);
            Assert.Contains("async boom", failure.Message); // the async failure is observable for assertions
        }

        [Fact]
        public void ExecuteTransaction_rolls_back_committed_children_when_a_later_child_throws()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<ThrowOnPostCreate>();

            ExecuteTransactionRequest batch = new ExecuteTransactionRequest
            {
                Requests = new OrganizationRequestCollection(),
            };
            batch.Requests.Add(new CreateRequest { Target = new FakeTask { Subject = "committed first" } }); // no plugin, succeeds
            batch.Requests.Add(new CreateRequest { Target = new FakeAccount { Name = "boom" } });            // ThrowOnPostCreate throws

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Execute(batch));

            // The earlier successful child is rolled back with the whole batch.
            Assert.Empty(pipeline.GetAll("task"));
            Assert.Empty(pipeline.GetAll("account"));
        }
    }
}
