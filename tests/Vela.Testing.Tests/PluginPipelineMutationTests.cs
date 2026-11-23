using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.Manifest;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Mutation-survivor kills for the in-memory plugin pipeline (PluginPipeline.cs / .Stages.cs / .Execution.cs,
    // issue #448): registration guards, the step row and OwningExtension names, stage dispatch (ordering, FromSandbox,
    // output parameters, bulk image collections, filtering), the stage-30 ParentContext, correlation and request ids,
    // the identity stamped on each context, the async drain (rollback, dropped follow-ups, no re-entrancy) and the
    // transaction fault messages. Each test asserts what a handler observes, through the pipeline's public surface.
    public sealed class PluginPipelineMutationTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000481");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000482");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000004b1");
        private static readonly Guid ServiceBu = new Guid("b0000000-0000-0000-0000-0000000004b2");

        public PluginPipelineMutationTests()
        {
            PmRecorder.Reset();
        }

        // ---- Registration guards (PluginPipeline.cs) ------------------------------------------------------------

        [Fact]
        public void FromAssembly_and_Register_reject_null_with_the_parameter_name()
        {
            Assert.Equal("assembly", Assert.Throws<ArgumentNullException>(() => PluginPipeline.FromAssembly(null!)).ParamName);
            Assert.Equal("pluginType", Assert.Throws<ArgumentNullException>(() => PluginPipeline.Empty().Register(null!)).ParamName);
        }

        // The second registration is refused with the reason and the way out, not just "already registered".
        [Fact]
        public void Registering_a_plugin_twice_explains_the_double_firing_and_the_way_out()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmRawSteps>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => pipeline.Register<PmRawSteps>());

            Assert.Contains("so registering it again would fire each of its steps twice.", error.Message);
            Assert.Contains("Register it once, or start from PluginPipeline.Empty() to name the plugins of the test.", error.Message);
        }

        // A manifest entry with an empty type name is reported as unresolved (it would never fire), like a renamed
        // type, instead of failing in Assembly.GetType.
        [Fact]
        public void A_definition_without_a_type_name_is_reported_as_unresolved()
        {
            FrameworkManifest manifest = new FrameworkManifest
            {
                PluginSteps = new[] { new StepDefinition { PluginTypeName = string.Empty, Message = "Create", Stage = PluginStages.PostOperation } },
            };
            System.Reflection.MethodInfo fromManifest = typeof(PluginPipeline).GetMethod("FromManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

            System.Reflection.TargetInvocationException wrapped = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => fromManifest.Invoke(null, new object[] { typeof(PluginPipelineMutationTests).Assembly, manifest }));

            InvalidOperationException error = Assert.IsType<InvalidOperationException>(wrapped.InnerException);
            Assert.Contains("could not register 1 definition(s)", error.Message);
        }

        // Without WithEnvironmentId, every step's context carries an empty environment id, not null.
        [Fact]
        public void A_step_context_has_an_empty_environment_id_by_default()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmRawSteps>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            IPluginExecutionContext6 context = Assert.IsAssignableFrom<IPluginExecutionContext6>(PmRecorder.One("raw-preop").PluginExecutionContext);
            Assert.Equal(string.Empty, context.EnvironmentId);
        }

        [Fact]
        public void Registering_a_type_without_the_configuration_constructor_names_the_purpose()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PluginPipeline.Empty().Register<PmNoConfigCtor>());

            Assert.Contains("constructor to be registered", ex.Message);
        }

        [Fact]
        public void Registering_a_type_that_declares_nothing_explains_how_to_declare_a_step()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PluginPipeline.Empty().Register<PmNoSteps>());

            Assert.Contains("Declare its steps in RegisterPluginSteps() (or its Custom API definition).", ex.Message);
        }

        // A Custom API whose definition is missing registers nothing: the setup fails with the "declares nothing"
        // guard, not a NullReferenceException.
        [Fact]
        public void Registering_a_custom_api_without_a_definition_fails_the_setup()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PluginPipeline.Empty().Register<PmApiWithoutDefinition>());

            Assert.Contains("declares no step and no Custom API", ex.Message);
        }

        [Fact]
        public void WithSecureConfig_rejects_an_empty_name()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() => PluginPipeline.Empty().WithSecureConfig(string.Empty, "value"));

            Assert.Equal("name", ex.ParamName);
            Assert.StartsWith("A secure configuration name is required.", ex.Message);
        }

        // A cap below 1 is clamped to 1: the top-level operation (depth 1) runs, its cascade (depth 2) is stopped.
        [Fact]
        public void WithMaxDepth_below_one_is_clamped_to_one()
        {
            PluginPipeline plain = PluginPipeline.Empty().WithMaxDepth(0);
            plain.Service.Create(new FakeAccount { Name = "Zava" });
            Assert.Single(plain.GetAll("account"));

            PluginPipeline cascading = PluginPipeline.Empty().WithMaxDepth(0).Register<CreateTaskOnAccountCreate>();
            InvalidPluginExecutionException ex = Assert.ThrowsAny<InvalidPluginExecutionException>(() => cascading.Service.Create(new FakeAccount { Name = "Zava" }));
            Assert.Contains("(cap 1)", ex.Message);
            Assert.Empty(cascading.GetAll("account"));
        }

        // FromAssembly registers the discovered Custom APIs (they execute) and seeds each step's
        // sdkmessageprocessingstep row under the name vela apply gives it.
        [Fact]
        public void FromAssembly_registers_custom_apis_and_seeds_the_step_rows()
        {
            PluginPipeline pipeline = PluginPipeline.FromAssemblyOf<FakeAuto>();

            OrganizationRequest request = new OrganizationRequest("vela_Greet");
            request["Name"] = "Zava";
            Assert.Equal("Hello Zava", pipeline.Service.Execute(request).Results["Message"]);

            string[] names = pipeline.GetAll("sdkmessageprocessingstep").Select(row => row.GetAttributeValue<string>("name")).ToArray();
            string asyncKey = Assert.Single(new AsyncCreateNote(null!, null!).GetStepDefinitions()).StepKey;
            Assert.Contains($"{typeof(AsyncCreateNote).FullName} - PostOpAsync: {asyncKey}", names);
        }

        // ---- Step names and OwningExtension (PluginPipeline.Stages.cs) -----------------------------------------

        // A step registered without a key is named after its message and entity, lower-cased (vela apply's name).
        [Fact]
        public void Each_step_context_carries_its_step_registration_name_by_stage_mode_and_key()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmRawSteps>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            string type = typeof(PmRawSteps).FullName!;
            EntityReference preVal = PmRecorder.One("raw-preval").PluginExecutionContext.OwningExtension;
            Assert.Equal("sdkmessageprocessingstep", preVal.LogicalName);
            Assert.Equal($"{type} - PreValSync: Create of account", preVal.Name);
            Assert.Equal($"{type} - PreOpSync: Create of account", PmRecorder.One("raw-preop").PluginExecutionContext.OwningExtension.Name);
            Assert.Equal($"{type} - PostOpSync: {PmRawSteps.Key}", PmRecorder.One("raw-keyed").PluginExecutionContext.OwningExtension.Name);

            // An async step's OwningExtension carries no name; the seeded step row it points at does.
            EntityReference async = PmRecorder.One("raw-async").PluginExecutionContext.OwningExtension;
            Assert.Null(async.Name);
            Entity row = Assert.Single(pipeline.GetAll("sdkmessageprocessingstep"), r => r.Id == async.Id);
            Assert.Equal($"{type} - PostOpAsync: Create of account", row.GetAttributeValue<string>("name"));
        }

        // A step registered without an entity (any table) is named after its message alone and sees the primary
        // entity of the record it fires on.
        [Fact]
        public void An_entity_less_step_is_named_by_message_and_sees_the_operations_entity()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmRawSteps>().Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Delete("account", id);

            IPluginExecutionContext ctx = PmRecorder.One("raw-any-delete").PluginExecutionContext;
            Assert.Equal($"{typeof(PmRawSteps).FullName} - PostOpSync: Delete", ctx.OwningExtension.Name);
            Assert.Equal("account", ctx.PrimaryEntityName);
            Assert.Equal(id, ctx.PrimaryEntityId);
        }

        [Fact]
        public void A_global_message_step_is_named_without_an_entity()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account", accountId), new Entity("contact", contactId))
                .Register<PmAssociateSteps>();

            pipeline.Service.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", contactId) });

            Assert.Equal($"{typeof(PmAssociateSteps).FullName} - PostOpSync: {PmAssociateSteps.SyncKey}",
                PmRecorder.One("assoc-sync").PluginExecutionContext.OwningExtension.Name);
        }

        // ---- Stage dispatch -------------------------------------------------------------------------------------

        [Fact]
        public void Delete_steps_fire_in_execution_order_and_see_fromsandbox_and_the_main_stage_parent()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmDeleteSteps>().Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Delete("account", id);

            // Registered rank 10 first, rank 1 second: rank 1 fires first.
            Assert.Equal(new[] { "rank1", "rank10" }, PmRecorder.Seen.Where(s => s.Tag.StartsWith("rank", StringComparison.Ordinal)).Select(s => s.Tag).ToArray());
            // A top-level Delete is not from the sandbox.
            Assert.False(PmRecorder.One("delete-preval").PluginExecutionContext.InputParameters.Contains("FromSandbox"));
            // The PostOperation step's parent is the stage-30 main operation of the Delete.
            IPluginExecutionContext parent = PmRecorder.Seen.Single(s => s.Tag == "rank1").Context.PluginExecutionContext.ParentContext;
            Assert.Equal((int)PluginStages.MainOperation, parent.Stage);
            Assert.Equal("Delete", parent.MessageName);
        }

        [Fact]
        public void A_delete_issued_by_a_plugin_reaches_prevalidation_from_the_sandbox()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmDeleteSteps>().Register<PmDeleteAccountOnTaskCreate>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Create(new FakeTask { Subject = id.ToString() });

            Assert.Equal(true, PmRecorder.One("delete-preval").PluginExecutionContext.InputParameters["FromSandbox"]);
            Assert.Empty(pipeline.GetAll("account"));
        }

        [Fact]
        public void Only_a_create_post_step_sees_the_created_id_output()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmCreateOutputs>().Register<PmUpdateOutputs>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            Guid created = pipeline.Service.Create(new FakeAccount { Name = "Zava" });
            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            Assert.False(PmRecorder.One("create-pre").PluginExecutionContext.OutputParameters.Contains("id"));
            Assert.Equal(created, PmRecorder.One("create-post").PluginExecutionContext.OutputParameters["id"]);
            Assert.False(PmRecorder.One("update-post").PluginExecutionContext.OutputParameters.Contains("id"));
        }

        // The stage-30 context a Create PreOperation/PostOperation step sees as its parent: in the transaction, with
        // the Target.
        [Fact]
        public void A_create_post_step_parent_is_the_main_operation_in_the_transaction_with_the_target()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmCreateOutputs>();

            Guid created = pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            IPluginExecutionContext parent = PmRecorder.One("create-post").PluginExecutionContext.ParentContext;
            Assert.Equal((int)PluginStages.MainOperation, parent.Stage);
            Assert.True(parent.IsInTransaction);
            Entity target = Assert.IsAssignableFrom<Entity>(parent.InputParameters["Target"]);
            Assert.Equal(created, target.Id);
        }

        [Fact]
        public void An_update_step_without_filtering_attributes_fires_on_any_change()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmRawSteps>().Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, NumberOfEmployees = 5 });

            Assert.Single(PmRecorder.Seen, s => s.Tag == "raw-unfiltered-update");
        }

        [Fact]
        public void An_image_restricted_to_attributes_carries_only_those()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmNameImage>()
                .Seed(new FakeAccount { Id = id, Name = "Old", NumberOfEmployees = 3 });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            Entity pre = PmRecorder.One("name-image").PluginExecutionContext.PreEntityImages["Before"];
            Assert.Equal("Old", pre.GetAttributeValue<string>("name"));
            Assert.False(pre.Contains("numberofemployees"));
        }

        [Fact]
        public void An_async_step_gets_the_primary_name_post_image_of_the_record()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmRawSteps>();

            Guid id = pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity image = PmRecorder.One("raw-async").PluginExecutionContext.PostEntityImages["AsynchronousStepPrimaryName"];
            Assert.Equal(id, image.Id);
            Assert.Equal("Zava", image.GetAttributeValue<string>("name"));
        }

        // ---- Bulk stages ----------------------------------------------------------------------------------------

        [Fact]
        public void An_UpdateMultiple_step_fires_once_when_any_row_matches_its_filter_and_not_when_none_does()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmBulkUpdateOnName>()
                .Seed(new FakeAccount { Id = a, Name = "A", NumberOfEmployees = 1 }, new FakeAccount { Id = b, Name = "B", NumberOfEmployees = 1 });

            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = Targets(new FakeAccount { Id = a, Name = "A2" }, new FakeAccount { Id = b, NumberOfEmployees = 2 }) });

            ILocalPluginContext fired = PmRecorder.One("bulk-update").Local!;
            Assert.Equal(2, fired.BulkTargets!.Entities.Count);
            Assert.Equal("account", fired.BulkTargets.EntityName);
            Assert.False(fired.PluginExecutionContext.OutputParameters.Contains("Ids"));

            PmRecorder.Reset();
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = Targets(new FakeAccount { Id = a, NumberOfEmployees = 7 }, new FakeAccount { Id = b, NumberOfEmployees = 8 }) });

            Assert.DoesNotContain(PmRecorder.Seen, s => s.Tag == "bulk-update");
        }

        [Fact]
        public void Only_a_CreateMultiple_post_step_sees_the_created_ids_and_only_prevalidation_from_a_plugin_sees_fromsandbox()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmBulkCreateStages>();

            pipeline.Service.Execute(new CreateMultipleRequest { Targets = Targets(new FakeAccount { Name = "One" }, new FakeAccount { Name = "Two" }) });

            Guid[] ids = pipeline.GetAll("account").Select(e => e.Id).OrderBy(g => g).ToArray();
            Assert.False(PmRecorder.One("bulk-pre").PluginExecutionContext.OutputParameters.Contains("Ids"));
            Assert.Equal(ids, ((Guid[])PmRecorder.One("bulk-post").PluginExecutionContext.OutputParameters["Ids"]).OrderBy(g => g).ToArray());
            // Issued by the test, not a plugin: no FromSandbox at PreValidation.
            Assert.False(PmRecorder.One("bulk-preval").PluginExecutionContext.InputParameters.Contains("FromSandbox"));
        }

        [Fact]
        public void A_CreateMultiple_issued_by_a_plugin_carries_fromsandbox_at_prevalidation_only()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmBulkCreateStages>().Register<PmCreateMultipleOnTaskCreate>();

            pipeline.Service.Create(new FakeTask { Subject = "go" });

            Assert.Equal(true, PmRecorder.One("bulk-preval").PluginExecutionContext.InputParameters["FromSandbox"]);
            Assert.False(PmRecorder.One("bulk-post").PluginExecutionContext.InputParameters.Contains("FromSandbox"));
            Assert.Equal(2, pipeline.GetAll("account").Count);
        }

        // A bulk image collection: the step's own images, its sibling's (same plugin type and message, pre images in
        // the pre collection and post images in the post one), then the platform image - never those of another
        // plugin type or of another message.
        [Fact]
        public void A_bulk_image_collection_takes_sibling_images_of_the_same_kind_only_from_the_same_plugin_and_message()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmBulkSiblings>().Register<PmBulkForeign>()
                .Seed(new FakeAccount { Id = id, Name = "Old", NumberOfEmployees = 1 });

            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = Targets(new FakeAccount { Id = id, Name = "New" }) });

            ILocalPluginContext ctx = PmRecorder.One("siblings").Local!;
            Assert.Equal(new[] { "Mine", "PreBusinessEntity", "Sib" }, ctx.BulkPreEntityImagesCollection[0].Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "MinePost", "PostBusinessEntity", "SibPost" }, ctx.BulkPostEntityImagesCollection[0].Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        // ---- ParentContext and the context stack ----------------------------------------------------------------

        // An Associate a plugin issues: its sync and async steps see that plugin's context as ParentContext; a later
        // top-level Associate has none (nothing leaks on the context stack).
        [Fact]
        public void Associate_steps_fired_by_a_plugin_see_its_context_as_parent_and_a_later_top_level_one_sees_none()
        {
            Guid contactId = Guid.NewGuid();
            Guid otherContact = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("contact", contactId), new Entity("contact", otherContact))
                .Register<PmAssociateSteps>()
                .Register<PmAssociateOnAccountCreate>();

            Guid accountId = pipeline.Service.Create(new FakeAccount { Name = contactId.ToString() });

            foreach (string tag in new[] { "assoc-sync", "assoc-async" })
            {
                IPluginExecutionContext parent = PmRecorder.One(tag).PluginExecutionContext.ParentContext;
                Assert.NotNull(parent);
                Assert.Equal("Create", parent.MessageName);
                Assert.Equal((int)PluginStages.PostOperation, parent.Stage);
            }

            Assert.Empty(pipeline.AsyncStepFailures);
            PmRecorder.Reset();
            pipeline.Service.Associate("account", accountId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("contact", otherContact) });

            Assert.Null(PmRecorder.One("assoc-sync").PluginExecutionContext.ParentContext);
        }

        // ---- Correlation and request ids ------------------------------------------------------------------------

        [Fact]
        public void An_ExecuteTransaction_shares_one_new_correlation_id_and_the_next_operation_gets_its_own()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmAccountCorrelation>();

            pipeline.Service.Create(new FakeAccount { Name = "before" });
            pipeline.Service.Execute(new ExecuteTransactionRequest
            {
                Requests = new OrganizationRequestCollection
                {
                    new CreateRequest { Target = new FakeAccount { Name = "in-1" } },
                    new CreateRequest { Target = new FakeAccount { Name = "in-2" } },
                },
            });
            pipeline.Service.Create(new FakeAccount { Name = "after" });

            Guid[] ids = PmRecorder.Seen.Where(s => s.Tag == "corr").Select(s => s.Context.CorrelationId).ToArray();
            Assert.Equal(4, ids.Length);
            Assert.NotEqual(ids[0], ids[1]);
            Assert.Equal(ids[1], ids[2]);
            Assert.NotEqual(ids[2], ids[3]);
        }

        // An ExecuteTransaction a PreValidation plugin issues (outside the transaction) stays in the issuing
        // operation's correlation.
        [Fact]
        public void An_ExecuteTransaction_issued_by_a_plugin_keeps_the_callers_correlation_id()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmTransactionFromPreValidation>().Register<PmTaskCorrelation>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal(PmRecorder.One("account-preval").CorrelationId, PmRecorder.One("task-corr").CorrelationId);
            Assert.Single(pipeline.GetAll("task"));
        }

        // An ExecuteTransaction issued inside the transaction joins it: when the issuing plugin then fails, the
        // child's write and its queued async step are discarded with the whole transaction.
        [Fact]
        public void An_ExecuteTransaction_issued_inside_a_transaction_joins_it_and_rolls_back_with_it()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmTransactionThenThrow>().Register<PmAsyncOnTask>();

            Assert.ThrowsAny<Exception>(() => pipeline.Service.Create(new FakeAccount { Name = "Zava" }));

            Assert.Empty(pipeline.GetAll("account"));
            Assert.Empty(pipeline.GetAll("task"));
            Assert.DoesNotContain(PmRecorder.Seen, s => s.Tag == "task-async");
        }

        [Fact]
        public void A_cascaded_step_carries_the_request_id_of_the_outer_call()
        {
            // The account step records its context before the cascading step (same rank: registration order) issues
            // the nested call.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmAccountCorrelation>().Register<CreateTaskOnAccountCreate>().Register<PmTaskCorrelation>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Guid? outer = PmRecorder.One("corr").PluginExecutionContext.RequestId;
            Assert.NotNull(outer);
            Assert.Equal(outer, PmRecorder.One("task-corr").PluginExecutionContext.RequestId);
        }

        // ---- The asynchronous drain -----------------------------------------------------------------------------

        [Fact]
        public void A_failed_async_step_keeps_its_writes()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmAsyncWriteThenThrow>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            // Verified live: no transaction around an async step, so its write before the failure stays.
            Assert.Single(pipeline.AsyncStepFailures);
            Assert.Single(pipeline.GetAll("annotation"));
            Assert.Single(pipeline.GetAll("account"));
        }

        // A failed async step keeps its writes, so the async steps they queued run too, and the async step queued
        // before it still runs, exactly once.
        [Fact]
        public void A_failed_async_step_keeps_the_async_steps_its_writes_queued()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmAsyncPair>().Register<PmAsyncOnTask>();

            pipeline.Service.Create(new FakeAccount { Name = "fail" });

            Assert.Single(pipeline.AsyncStepFailures);
            Assert.Single(PmRecorder.Seen, s => s.Tag == "async-second");
            Assert.Single(PmRecorder.Seen, s => s.Tag == "task-async");
            Assert.Single(pipeline.GetAll("task"));
        }

        // The committed async steps run one after the other: a write inside one async step does not start the next
        // one before the first has finished.
        [Fact]
        public void An_async_step_runs_after_the_previous_one_has_finished()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmAsyncPair>();

            pipeline.Service.Create(new FakeAccount { Name = "ok" });

            Assert.Empty(pipeline.AsyncStepFailures);
            Assert.Equal(1, PmRecorder.Counts["async-second"]);
        }

        // ---- Transaction faults ---------------------------------------------------------------------------------

        [Fact]
        public void A_plugin_calling_again_after_a_swallowed_failure_gets_the_no_active_transaction_fault()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmSwallowThenCall>();

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Create(new FakeAccount { Name = "again" }));

            Assert.StartsWith("There is no active transaction.", fault.Detail.Message);
        }

        [Fact]
        public void A_plugin_returning_after_a_swallowed_failure_gets_the_isv_fault()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PmSwallowThenCall>();

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Create(new FakeAccount { Name = "return" }));

            Assert.StartsWith("ISV code reduced the open transaction count.", fault.Detail.Message);
        }

        // ---- Identity -------------------------------------------------------------------------------------------

        // Alice creates an account; its step (impersonating Bob, in another business unit) creates a task through
        // ctx.Service. The task's sync and async steps run as Bob, in Bob's business unit, initiated by Alice.
        [Fact]
        public void A_write_made_as_an_impersonated_user_runs_its_steps_as_that_user_initiated_by_the_caller()
        {
            PluginPipeline pipeline = SecuredPipeline().Register<PmImpersonatedTaskCreator>().Register<PmTaskIdentity>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            IPluginExecutionContext impersonated = PmRecorder.One("impersonated").PluginExecutionContext;
            Assert.Equal(Bob, impersonated.UserId);
            Assert.Equal(ServiceBu, impersonated.BusinessUnitId);
            Assert.Equal(Alice, impersonated.InitiatingUserId);

            foreach (string tag in new[] { "task-sync", "task-async" })
            {
                IPluginExecutionContext ctx = PmRecorder.One(tag).PluginExecutionContext;
                Assert.Equal(Bob, ctx.UserId);
                Assert.Equal(ServiceBu, ctx.BusinessUnitId);
                Assert.Equal(Alice, ctx.InitiatingUserId);
            }

            // The task Create's stage-30 parent keeps the initiating user too.
            Assert.Equal(Alice, PmRecorder.One("task-sync").PluginExecutionContext.ParentContext.InitiatingUserId);
        }

        // A write through ctx.SystemService runs its steps as SYSTEM (not the caller, not an empty id); a write those
        // steps make keeps SYSTEM and the original initiating user, and the caller's identity is restored for the
        // steps that follow.
        [Fact]
        public void A_system_write_runs_its_steps_as_system_and_restores_the_caller_afterwards()
        {
            PluginPipeline pipeline = SecuredPipeline().Register<PmSystemTaskCreator>().Register<PmTaskIdentity>().Register<PmNoteOnTaskViaService>().Register<PmNoteIdentity>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            IPluginExecutionContext task = PmRecorder.One("task-sync").PluginExecutionContext;
            Assert.NotEqual(Alice, task.UserId);
            Assert.NotEqual(Guid.Empty, task.UserId);
            Assert.Equal(Alice, task.InitiatingUserId);

            IPluginExecutionContext note = PmRecorder.One("note").PluginExecutionContext;
            Assert.Equal(task.UserId, note.UserId);
            Assert.Equal(Alice, note.InitiatingUserId);

            // The account step that runs after the SYSTEM write is back to Alice.
            Assert.Equal(Alice, PmRecorder.One("after-system").PluginExecutionContext.UserId);
            Assert.Equal(Alice, PmRecorder.One("after-system").PluginExecutionContext.InitiatingUserId);
        }

        private static PluginPipeline SecuredPipeline() => PluginPipeline.Empty()
            .WithCurrentUser(Alice)
            .WithSecurity(s => s
                .BusinessUnit(SalesBu)
                .BusinessUnit(ServiceBu)
                .Role("accounts", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read))
                .Role("tasks", r => r.Grant("task", AccessLevel.Organization, Privilege.Create, Privilege.Read))
                .User(Alice, SalesBu, "accounts")
                .User(Bob, ServiceBu, "tasks"));

        private static EntityCollection Targets(params Entity[] entities)
        {
            EntityCollection collection = new EntityCollection { EntityName = "account" };
            collection.Entities.AddRange(entities);
            return collection;
        }
    }

    // Records the contexts the handlers saw (per thread: the pipeline runs every step, async ones included, inline).
    internal static class PmRecorder
    {
        [ThreadStatic]
        private static List<(string Tag, PmSeen Context)>? _seen;

        [ThreadStatic]
        private static Dictionary<string, int>? _counts;

        public static List<(string Tag, PmSeen Context)> Seen => _seen ??= new List<(string, PmSeen)>();

        public static Dictionary<string, int> Counts => _counts ??= new Dictionary<string, int>();

        public static void Reset()
        {
            _seen = new List<(string, PmSeen)>();
            _counts = new Dictionary<string, int>();
        }

        public static void Add(string tag, ILocalPluginContext ctx) => Seen.Add((tag, new PmSeen(ctx.PluginExecutionContext, ctx)));

        public static void Add(string tag, IPluginExecutionContext ctx) => Seen.Add((tag, new PmSeen(ctx, null)));

        public static PmSeen One(string tag) => Assert.Single(Seen, s => s.Tag == tag).Context;
    }

    internal sealed class PmSeen
    {
        public PmSeen(IPluginExecutionContext context, ILocalPluginContext? local)
        {
            PluginExecutionContext = context;
            Local = local;
        }

        public IPluginExecutionContext PluginExecutionContext { get; }

        public ILocalPluginContext? Local { get; }

        public Guid CorrelationId => PluginExecutionContext.CorrelationId;
    }

    [EntityLogicalName("annotation")]
    public sealed class PmNote() : Entity("annotation")
    {
        [AttributeLogicalName("subject")]
        public string Subject
        {
            get => GetAttributeValue<string>("subject");
            set => SetAttributeValue("subject", value);
        }
    }

    public sealed class PmNoConfigCtor : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider) { }
    }

    public sealed class PmNoSteps : IPlugin
    {
        public PmNoSteps(string unsecure, string secure) { }

        public void Execute(IServiceProvider serviceProvider) { }
    }

    public sealed class PmApiWithoutDefinition : CustomApiBase
    {
        public PmApiWithoutDefinition(string u, string s) : base(u, s) { }

        protected override CustomApiDefinition BuildDefinitionCore() => null!;
    }

    // Hand-declared step definitions (no key, no entity, no filter) the fluent builder never produces, read by
    // Register through GetStepDefinitions like any plugin's.
    public sealed class PmRawSteps : IPlugin
    {
        public const string Key = "pm-raw-keyed";

        public PmRawSteps(string unsecure, string secure) { }

        public StepDefinition[] GetStepDefinitions() => new[]
        {
            Step("Create", PluginStages.PreValidation, ExecutionMode.Synchronous, "account"),
            Step("Create", PluginStages.PreOperation, ExecutionMode.Synchronous, "account"),
            Step("Create", PluginStages.PostOperation, ExecutionMode.Synchronous, "account", Key),
            Step("Create", PluginStages.PostOperation, ExecutionMode.Asynchronous, "account"),
            Step("Delete", PluginStages.PostOperation, ExecutionMode.Synchronous, string.Empty),
            // No filter, and no image arrays at all.
            NoImages(Step("Update", PluginStages.PostOperation, ExecutionMode.Synchronous, "account")),
        };

        private static StepDefinition NoImages(StepDefinition step)
        {
            step.PreImages = null!;
            step.PostImages = null!;
            return step;
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            IPluginExecutionContext ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            string tag = ctx.MessageName switch
            {
                "Delete" => "raw-any-delete",
                "Update" => "raw-unfiltered-update",
                _ => ctx.Mode == (int)ExecutionMode.Asynchronous ? "raw-async"
                    : ctx.Stage == (int)PluginStages.PreValidation ? "raw-preval"
                    : ctx.Stage == (int)PluginStages.PreOperation ? "raw-preop"
                    : "raw-keyed",
            };
            PmRecorder.Add(tag, ctx);
        }

        private static StepDefinition Step(string message, PluginStages stage, ExecutionMode mode, string entity, string key = "") => new StepDefinition
        {
            PluginTypeName = typeof(PmRawSteps).FullName!,
            Message = message,
            Stage = stage,
            Mode = mode,
            EntityLogicalName = entity,
            StepKey = key,
        };
    }

    public sealed class PmAssociateSteps : EntityPluginBase<FakeAccount>
    {
        public const string SyncKey = "pm-assoc-sync";

        public PmAssociateSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnAssociate().PostOperation().Sync().WithStepKey(SyncKey).Handle(ctx => PmRecorder.Add("assoc-sync", ctx));
            RegisterStep().OnAssociate().PostOperation().Async().Handle(ctx => PmRecorder.Add("assoc-async", ctx));
        }
    }

    // Associates the created account with the contact whose id is the account name.
    public sealed class PmAssociateOnAccountCreate : EntityPluginBase<FakeAccount>
    {
        public PmAssociateOnAccountCreate(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
                ctx.SystemService.Associate("account", ctx.PrimaryEntityId, new Relationship("account_contacts"),
                    new EntityReferenceCollection { new EntityReference("contact", new Guid(ctx.PluginExecutionContext.InputParameters["Target"] is Entity t ? t.GetAttributeValue<string>("name") : string.Empty)) }));
    }

    public sealed class PmDeleteSteps : EntityPluginBase<FakeAccount>
    {
        public PmDeleteSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnDelete().PostOperation().Sync().WithExecutionOrder(10).WithStepKey("pm-rank10").Handle(ctx => PmRecorder.Add("rank10", ctx));
            RegisterStep().OnDelete().PostOperation().Sync().WithExecutionOrder(1).WithStepKey("pm-rank1").Handle(ctx => PmRecorder.Add("rank1", ctx));
            RegisterStep().OnDelete().PreValidation().Sync().Handle(ctx => PmRecorder.Add("delete-preval", ctx));
        }
    }

    // Deletes the account whose id is the task subject.
    public sealed class PmDeleteAccountOnTaskCreate : EntityPluginBase<FakeTask>
    {
        public PmDeleteAccountOnTaskCreate(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(t => t.Subject)
                .Handle(ctx => ctx.SystemService.Delete("account", new Guid(((Entity)ctx.PluginExecutionContext.InputParameters["Target"]).GetAttributeValue<string>("subject"))));
    }

    public sealed class PmCreateOutputs : EntityPluginBase<FakeAccount>
    {
        public PmCreateOutputs(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("create-pre", ctx));
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("create-post", ctx));
        }
    }

    public sealed class PmUpdateOutputs : EntityPluginBase<FakeAccount>
    {
        public PmUpdateOutputs(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("update-post", ctx));
    }

    public sealed class PmNameImage : EntityPluginBase<FakeAccount>
    {
        public PmNameImage(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage("Before", a => a.Name)
                .Handle(ctx => PmRecorder.Add("name-image", ctx));
    }

    public sealed class PmBulkUpdateOnName : EntityPluginBase<FakeAccount>
    {
        public PmBulkUpdateOnName(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("bulk-update", ctx));
    }

    public sealed class PmBulkCreateStages : EntityPluginBase<FakeAccount>
    {
        public PmBulkCreateStages(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreateMultiple().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("bulk-preval", ctx));
            RegisterStep().OnCreateMultiple().PreOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("bulk-pre", ctx));
            RegisterStep().OnCreateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("bulk-post", ctx));
        }
    }

    public sealed class PmCreateMultipleOnTaskCreate : EntityPluginBase<FakeTask>
    {
        public PmCreateMultipleOnTaskCreate(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(t => t.Subject).Handle(ctx =>
            {
                EntityCollection targets = new EntityCollection { EntityName = "account" };
                targets.Entities.Add(new FakeAccount { Name = "One" });
                targets.Entities.Add(new FakeAccount { Name = "Two" });
                ctx.SystemService.Execute(new CreateMultipleRequest { Targets = targets });
            });
    }

    public sealed class PmBulkSiblings : EntityPluginBase<FakeAccount>
    {
        public PmBulkSiblings(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name)
                .WithPreImage("Mine", a => a.Name).WithPostImage("MinePost", a => a.Name)
                .Handle(ctx => PmRecorder.Add("siblings", ctx));
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Revenue)
                .WithPreImage("Sib", a => a.NumberOfEmployees).WithPostImage("SibPost", a => a.NumberOfEmployees)
                .Handle(_ => { });
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Revenue)
                .WithPreImage("OtherMessage", a => a.NumberOfEmployees)
                .Handle(_ => { });
        }
    }

    public sealed class PmBulkForeign : EntityPluginBase<FakeAccount>
    {
        public PmBulkForeign(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Revenue)
                .WithPreImage("Foreign", a => a.NumberOfEmployees).WithPostImage("ForeignPost", a => a.NumberOfEmployees)
                .Handle(_ => { });
    }

    public sealed class PmAccountCorrelation : EntityPluginBase<FakeAccount>
    {
        public PmAccountCorrelation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => PmRecorder.Add("corr", ctx));
    }

    public sealed class PmTaskCorrelation : EntityPluginBase<FakeTask>
    {
        public PmTaskCorrelation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(t => t.Subject).Handle(ctx => PmRecorder.Add("task-corr", ctx));
    }

    public sealed class PmTransactionFromPreValidation : EntityPluginBase<FakeAccount>
    {
        public PmTransactionFromPreValidation(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PreValidation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                PmRecorder.Add("account-preval", ctx);
                ctx.SystemService.Execute(new ExecuteTransactionRequest
                {
                    Requests = new OrganizationRequestCollection { new CreateRequest { Target = new FakeTask { Subject = "from-preval" } } },
                });
            });
    }

    public sealed class PmTransactionThenThrow : EntityPluginBase<FakeAccount>
    {
        public PmTransactionThenThrow(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                ctx.SystemService.Execute(new ExecuteTransactionRequest
                {
                    Requests = new OrganizationRequestCollection { new CreateRequest { Target = new FakeTask { Subject = "nested" } } },
                });
                throw new InvalidPluginExecutionException("rejected after the nested transaction");
            });
    }

    public sealed class PmAsyncOnTask : EntityPluginBase<FakeTask>
    {
        public PmAsyncOnTask(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Async().FilterOn(t => t.Subject).Handle(ctx => PmRecorder.Add("task-async", ctx));
    }

    public sealed class PmAsyncWriteThenThrow : EntityPluginBase<FakeAccount>
    {
        public PmAsyncWriteThenThrow(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).Handle(ctx =>
            {
                ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "written before the failure" });
                throw new InvalidPluginExecutionException("async failure");
            });
    }

    // Two async steps on one trigger. The first creates a task, then (account name "fail") throws or ("ok") creates
    // a "first-done" note; the second counts the "first-done" notes it sees.
    public sealed class PmAsyncPair : EntityPluginBase<FakeAccount>
    {
        public PmAsyncPair(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).WithExecutionOrder(1).WithStepKey("pm-first").Handle(ctx =>
            {
                ctx.SystemService.Create(new FakeTask { Subject = "from-first" });
                if (((Entity)ctx.PluginExecutionContext.InputParameters["Target"]).GetAttributeValue<string>("name") == "fail")
                {
                    throw new InvalidPluginExecutionException("first async step failed");
                }

                ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "first-done" });
            });
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).WithExecutionOrder(2).WithStepKey("pm-second").Handle(ctx =>
            {
                PmRecorder.Add("async-second", ctx);
                Microsoft.Xrm.Sdk.Query.QueryExpression query = new Microsoft.Xrm.Sdk.Query.QueryExpression("annotation") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("subject") };
                PmRecorder.Counts["async-second"] = ctx.SystemService.RetrieveMultiple(query).Entities.Count(e => e.GetAttributeValue<string>("subject") == "first-done");
            });
        }
    }

    // Swallows a failed write; then (account name "again") calls the service again or ("return") returns normally.
    public sealed class PmSwallowThenCall : EntityPluginBase<FakeAccount>
    {
        public PmSwallowThenCall(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx =>
            {
                try
                {
                    ctx.SystemService.Delete("task", Guid.NewGuid());
                }
                catch (Exception)
                {
                    // Swallowed on purpose.
                }

                if (((Entity)ctx.PluginExecutionContext.InputParameters["Target"]).GetAttributeValue<string>("name") == "again")
                {
                    ctx.SystemService.Create(new FakeTask { Subject = "after the failure" });
                }
            });
    }

    public sealed class PmImpersonatedTaskCreator : EntityPluginBase<FakeAccount>
    {
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000482");

        public PmImpersonatedTaskCreator(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithImpersonation(Bob).Handle(ctx =>
            {
                PmRecorder.Add("impersonated", ctx);
                ctx.Service.Create(new FakeTask { Subject = "as bob" });
            });
    }

    public sealed class PmSystemTaskCreator : EntityPluginBase<FakeAccount>
    {
        public PmSystemTaskCreator(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1).WithStepKey("pm-system-write")
                .Handle(ctx => ctx.SystemService.Create(new FakeTask { Subject = "as system" }));
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(2).WithStepKey("pm-after-system")
                .Handle(ctx => PmRecorder.Add("after-system", ctx));
        }
    }

    public sealed class PmTaskIdentity : EntityPluginBase<FakeTask>
    {
        public PmTaskIdentity(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(t => t.Subject).WithExecutionOrder(1).Handle(ctx => PmRecorder.Add("task-sync", ctx));
            RegisterStep().OnCreate().PostOperation().Async().FilterOn(t => t.Subject).Handle(ctx => PmRecorder.Add("task-async", ctx));
        }
    }

    // Creates a note through ctx.Service (the identity the task step runs as).
    public sealed class PmNoteOnTaskViaService : EntityPluginBase<FakeTask>
    {
        public PmNoteOnTaskViaService(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(t => t.Subject).WithExecutionOrder(2)
                .Handle(ctx => ctx.Service.Create(new Entity("annotation") { ["subject"] = "note" }));
    }

    public sealed class PmNoteIdentity : EntityPluginBase<PmNote>
    {
        public PmNoteIdentity(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(n => n.Subject).Handle(ctx => PmRecorder.Add("note", ctx));
    }
}
