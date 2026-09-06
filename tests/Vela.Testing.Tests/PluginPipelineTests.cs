using System;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.StepBuilder;
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
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

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
        public void Recursion_TripsTheDepthGuard_AndRollsBack()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecurseOnUpdate>()
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
        public void Update_CarryingOwnerAndStandardFields_FiresTheStepTwice()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecordUpdatePasses>()
                .Seed(new FakeOwnable { Id = id, Name = "Old" });

            FakeOwnable update = new FakeOwnable { Id = id, Name = "New" };
            update["ownerid"] = new EntityReference("systemuser", Guid.NewGuid());
            pipeline.Service.Update(update);

            // One execution for the standard fields, a separate one for the owner field — like the platform.
            string[] passes = pipeline.GetAll("task")
                .Select(t => t.GetAttributeValue<string>("subject"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "pass:owner", "pass:standard" }, passes);

            // Both writes still land.
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

            // No special field present, so no split.
            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("pass:standard", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void SetState_MapsToAStatecodeUpdate_AndFiresUpdatePlugins()
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
            // SendEmail has no data simulation in the fake, but a registered SendEmail step must still fire like
            // any other plugin when the message is executed, with ctx.Request typed as the SendEmailRequest.
            Guid emailId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnAccountEmailSent>();

            pipeline.Service.Execute(new SendEmailRequest { EmailId = emailId, IssueSend = true });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal($"sent:{emailId}", task.GetAttributeValue<string>("subject"));
            Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("OnAccountEmailSent"));
        }

        [Fact]
        public void ModelledMessage_WithTargetReference_DerivesEntityAndFires()
        {
            // AddToQueue carries a Target EntityReference; the primary entity is derived from it and ctx.TargetReference
            // / ctx.Request.DestinationQueueId are typed.
            Guid recordId = Guid.NewGuid();
            Guid queueId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnAccountAddedToQueue>();

            pipeline.Service.Execute(new AddToQueueRequest
            {
                Target = new EntityReference("account", recordId),
                DestinationQueueId = queueId
            });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal($"queue:{recordId}:{queueId}", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void FromAssembly_AutoDiscoversAndFiresPlugins()
        {
            PluginPipeline pipeline = PluginPipeline.FromAssemblyOf<FakeAuto>();

            pipeline.Service.Create(new FakeAuto());

            Assert.Contains(pipeline.ExecutionLog, entry => entry.PluginTypeName.Contains("AutoDiscoveredPlugin"));
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
    }
}
