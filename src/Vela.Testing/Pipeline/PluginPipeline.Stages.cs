using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Steps;
using Vela.Testing.Security;

namespace Vela.Testing
{
    // Stage dispatch: selects the registered steps for a stage, builds each execution context (inputs, outputs,
    // images) and runs the plugin, synchronously or after the commit for an asynchronous step.
    public sealed partial class PluginPipeline
    {
        // Whether an enabled step runs for the message on this entity. A generic modelled message may not resolve a
        // primary entity from the request (null or empty entityName); its steps then match by message alone.
        // Likewise an entity-none step (empty logical name, an intentionally global step) matches any entity, and a
        // global message (Associate/Disassociate: sdkmessagefilter "none") fires regardless of the entity the step
        // was declared on - mirroring the runtime's ResolveSingleMatchingStep and the CLI's registration. So does an
        // execution on a table-less filter (entity "none": a global Custom API).
        private static bool StepMatches(StepDefinition step, string message, string? entityName)
        {
            return step.Enabled
                && string.Equals(step.Message, message, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(entityName)
                    || string.Equals(entityName, PluginMessages.NoPrimaryEntity, StringComparison.OrdinalIgnoreCase)
                    || PluginMessages.IsGlobalMessage(message)
                    || string.IsNullOrEmpty(step.EntityLogicalName)
                    || string.Equals(step.EntityLogicalName, entityName, StringComparison.OrdinalIgnoreCase));
        }

        private void FireStage(StageInvocation invocation, List<Action> asyncQueue)
        {
            CaptureOperationContext(invocation);
            List<PipelineRegistration> steps = _registrations
                // A disabled step (.Disabled()) is registered but dormant: it never fires, as on a real org.
                .Where(registration => registration.Step.Stage == invocation.Stage
                    && StepMatches(registration.Step, invocation.Message, invocation.EntityLogicalName))
                .Where(registration => !_bypass.ShouldBypass(registration.Step))
                .OrderBy(registration => registration.Step.ExecutionOrder)
                .ToList();

            foreach (PipelineRegistration registration in steps)
            {
                PipelineRegistration captured = registration;
                Emit(captured, asyncQueue, () => Dispatch(captured, invocation));
            }
        }

        // The stage sequence of one message: PreValidation, then the transaction opens (a read stays out of it),
        // PreOperation, the main operation, and PostOperation, which sees the main operation's result. firePre fires
        // one pre stage; a platform-generated Update (the main operation of Assign or SetState) has no PreValidation.
        private T RunStages<T>(Action<PluginStages> firePre, Func<T> mainOperation, Action<T> firePost, bool transactional = true, bool preValidation = true)
        {
            if (preValidation)
            {
                firePre(PluginStages.PreValidation);
            }

            if (transactional)
            {
                EnterTransaction();
            }

            firePre(PluginStages.PreOperation);
            T result = mainOperation();
            firePost(result);
            return result;
        }

        // Fires one stage of a Create/Update batch. The two registration messages are merged: a bulk step
        // (CreateMultiple/UpdateMultiple) fires once with the whole Targets collection; a single step
        // (Create/Update) fires once per row. Both are ordered by execution order and filtered by FilterOn.
        // Their SharedVariables and ParentContext depend on the request (verified live). A single Create/Update: its
        // Create/Update and CreateMultiple/UpdateMultiple steps share the operation's collections (one at
        // PreValidation, one for PreOperation and PostOperation) and stage-30 context. A CreateMultiple/UpdateMultiple
        // request: its bulk steps have the operation's, and each row's Create/Update steps have their own (one at
        // PreValidation, one for PreOperation and PostOperation), with the CreateMultiple/UpdateMultiple context of the
        // same stage as ParentContext.
        private void FireBatchStage(string singleMessage, string bulkMessage, PluginStages stage, string entityName, IReadOnlyList<BatchItem> items, List<Action> asyncQueue)
        {
            bool isUpdate = string.Equals(singleMessage, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);

            List<PipelineRegistration> matching = _registrations
                .Where(registration => registration.Step.Enabled
                    && registration.Step.Stage == stage
                    && string.Equals(registration.Step.EntityLogicalName, entityName, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(registration.Step.Message, singleMessage, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(registration.Step.Message, bulkMessage, StringComparison.OrdinalIgnoreCase)))
                .Where(registration => !_bypass.ShouldBypass(registration.Step))
                .OrderBy(registration => registration.Step.ExecutionOrder)
                .ToList();

            if (matching.Count == 0)
            {
                return;
            }

            EntityCollection? targets = null;
            OperationFrame frame = _frames.Peek();
            bool bulkRequest = frame.BulkMessage != null;
            StageInvocation operation = NewInvocation(bulkRequest ? bulkMessage : singleMessage, stage, entityName, bulkRequest ? Guid.Empty : items[0].Target.Id, null);
            CaptureOperationContext(operation, () => bulkRequest
                ? new ParameterCollection { ["Targets"] = targets ??= BuildTargets(entityName, items) }
                : new ParameterCollection { ["Target"] = items[0].Target });

            foreach (PipelineRegistration registration in matching)
            {
                PipelineRegistration captured = registration;
                bool isBulk = string.Equals(registration.Step.Message, bulkMessage, StringComparison.OrdinalIgnoreCase);
                if (isBulk)
                {
                    // A bulk step fires once for the batch. For UpdateMultiple, fire if any row matches the filter.
                    if (!items.Any(item => FiltersMatch(captured.Step, item.ChangedAttributes)))
                    {
                        continue;
                    }

                    EntityCollection capturedTargets = targets ??= BuildTargets(entityName, items);
                    Emit(captured, asyncQueue, () => DispatchBulk(captured, bulkMessage, stage, entityName, capturedTargets, items, operation));
                }
                else
                {
                    foreach (BatchItem item in items)
                    {
                        if (!FiltersMatch(captured.Step, item.ChangedAttributes))
                        {
                            continue;
                        }

                        BatchItem capturedItem = item;
                        StageInvocation invocation = NewInvocation(singleMessage, stage, entityName, capturedItem.Target.Id, capturedItem.Target);
                        invocation.PreState = capturedItem.PreState;
                        invocation.PostState = capturedItem.PostState;
                        invocation.FromSandbox = operation.FromSandbox;
                        if (bulkRequest)
                        {
                            invocation.SharedVariables = stage == PluginStages.PreValidation
                                ? capturedItem.ValidationShared ??= new ParameterCollection()
                                : capturedItem.MainShared ??= new ParameterCollection();
                            invocation.ParentContext = BulkStageContext(frame, stage, operation, targets ??= BuildTargets(entityName, items));
                        }
                        else
                        {
                            invocation.SharedVariables = operation.SharedVariables;
                            invocation.ParentContext = operation.ParentContext;
                        }

                        // A Create Post step sees the created id as OutputParameters["id"], exactly as Dataverse
                        // surfaces it, so ctx.Response.id resolves in a post-create handler.
                        if (!isUpdate && stage == PluginStages.PostOperation)
                        {
                            invocation.Outputs = new ParameterCollection { { "id", capturedItem.Target.Id } };
                        }
                        Emit(captured, asyncQueue, () => Dispatch(captured, invocation));
                    }
                }
            }
        }

        // The CreateMultiple/UpdateMultiple context of a stage, which a row's Create/Update steps have as ParentContext
        // under a bulk request (verified live): the bulk steps' SharedVariables at that stage, and the stage-30 context
        // (or, at PreValidation, the operation's parent) as its own ParentContext.
        private FakePluginExecutionContext BulkStageContext(OperationFrame frame, PluginStages stage, StageInvocation operation, EntityCollection targets)
        {
            if (!frame.BulkStageContexts.TryGetValue((int)stage, out FakePluginExecutionContext? context))
            {
                context = PlatformContext(
                    operation.Message,
                    stage,
                    operation.EntityLogicalName,
                    Guid.Empty,
                    operation.SharedVariables ?? _sharedVariables,
                    operation.ParentContext ?? (_contextStack.Count > 0 ? _contextStack.Peek() : null),
                    new ParameterCollection { ["Targets"] = targets });
                frame.BulkStageContexts[(int)stage] = context;
            }

            return context;
        }

        // Runs the dispatch now for a synchronous step, or queues it for an asynchronous one: it then runs, with the
        // context it was queued with, after the transaction commits (and never if it rolls back).
        private void Emit(PipelineRegistration registration, List<Action> asyncQueue, Action dispatch)
        {
            if (registration.Step.Mode == ExecutionMode.Asynchronous)
            {
                asyncQueue.Add(Deferred(dispatch));
            }
            else
            {
                dispatch();
            }
        }

        private static EntityCollection BuildTargets(string entityName, IReadOnlyList<BatchItem> items)
        {
            EntityCollection collection = new EntityCollection { EntityName = entityName };
            foreach (BatchItem item in items)
            {
                collection.Entities.Add(item.Target);
            }

            return collection;
        }

        private void DispatchBulk(PipelineRegistration registration, string message, PluginStages stage, string entityName, EntityCollection targets, IReadOnlyList<BatchItem> items, StageInvocation operation)
        {
            FakePluginExecutionContext context = new FakePluginExecutionContext
            {
                MessageName = message,
                Stage = (int)stage,
                Mode = (int)registration.Step.Mode,
                Depth = _depth,
                PrimaryEntityName = entityName,
                PrimaryEntityId = Guid.Empty,
                CorrelationId = _correlationId,
                SharedVariables = operation.SharedVariables ?? _sharedVariables
            };

            // Bulk steps read the batch via ctx.BulkTargets (InputParameters["Targets"]) and per-row pre/post
            // images via the image collections (aligned by target index).
            context.InputParameters["Targets"] = targets;
            if (operation.FromSandbox && stage == PluginStages.PreValidation)
            {
                // Verified live at PreValidation only (see Dispatch).
                context.InputParameters[FromSandboxInput] = true;
            }

            StampOwningExtension(context, registration.Step);
            context.PreEntityImagesCollection = items.Select(item => BuildBulkImageCollection(registration, message, isPre: true, item.PreState)).ToArray();
            context.PostEntityImagesCollection = items.Select(item => BuildBulkImageCollection(registration, message, isPre: false, item.PostState)).ToArray();
            MaskSecuredImageColumns(registration.Step, context.PreEntityImagesCollection.Concat(context.PostEntityImagesCollection));

            // A CreateMultiple Post step sees the created ids as OutputParameters["Ids"], exactly as Dataverse
            // surfaces them, so ctx.Response.Ids resolves in a post-create-multiple handler.
            if (string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase)
                && stage == PluginStages.PostOperation)
            {
                context.OutputParameters["Ids"] = targets.Entities.Select(entity => entity.Id).ToArray();
            }

            PipelineExecutionEntry entry = new PipelineExecutionEntry(registration.Step.PluginTypeName, message, stage, registration.Step.Mode, entityName, _depth);
            _log.Add(entry);
            IPlugin plugin = registration.GetInstance(SecureConfigFor(registration.Step));
            ExecuteWithContext(context, plugin, entry, registration.Step, operation.ParentContext);
        }

        // One target's image collection for a bulk step, as Dataverse fills it (verified live): the step's own images,
        // then those of the other steps of the same plugin type on the same message (aliases already taken are not
        // overwritten), then the platform's full-record image (PreBusinessEntity / PostBusinessEntity), in the Pre and
        // the Post collection alike. A handler must therefore read its image by its own alias, never "the first one".
        private EntityImageCollection BuildBulkImageCollection(PipelineRegistration registration, string message, bool isPre, Entity? state)
        {
            EntityImageCollection collection = new EntityImageCollection();
            if (state == null)
            {
                return collection;
            }

            BuildImages(collection, isPre ? registration.Step.PreImages : registration.Step.PostImages, state);
            foreach (PipelineRegistration sibling in _registrations)
            {
                if (ReferenceEquals(sibling, registration)
                    || !sibling.Step.Enabled
                    || sibling.PluginType != registration.PluginType
                    || !string.Equals(sibling.Step.Message, message, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EntityImageCollection siblingImages = new EntityImageCollection();
                BuildImages(siblingImages, isPre ? sibling.Step.PreImages : sibling.Step.PostImages, state);
                foreach (KeyValuePair<string, Entity> image in siblingImages)
                {
                    if (!collection.ContainsKey(image.Key))
                    {
                        collection[image.Key] = image.Value;
                    }
                }
            }

            string platformAlias = isPre ? "PreBusinessEntity" : "PostBusinessEntity";
            if (!collection.ContainsKey(platformAlias))
            {
                collection[platformAlias] = ProjectImage(state, Array.Empty<string>());
            }

            return collection;
        }

        private void Dispatch(PipelineRegistration registration, StageInvocation invocation)
        {
            // A global message (Associate/Disassociate) runs on the "none" sdkmessagefilter: on Dataverse its steps
            // see PrimaryEntityName "none" and an empty PrimaryEntityId, whatever the Target (verified live at s10,
            // s20, s40 sync and s40 async). The Target input still carries the record.
            bool globalMessage = PluginMessages.IsGlobalMessage(invocation.Message);

            FakePluginExecutionContext context = new FakePluginExecutionContext
            {
                MessageName = invocation.Message,
                Stage = (int)invocation.Stage,
                Mode = (int)registration.Step.Mode,
                Depth = _depth,
                // When the invocation could not resolve a primary entity (a generic message with no Target), use
                // the step's own entity so the plugin's own step matching (which compares PrimaryEntityName to the
                // step's EntityLogicalName) still resolves this step.
                PrimaryEntityName = globalMessage
                    ? "none"
                    : string.IsNullOrEmpty(invocation.EntityLogicalName)
                        ? registration.Step.EntityLogicalName
                        : invocation.EntityLogicalName,
                // A Custom API's asynchronous step sees an empty PrimaryEntityId, even on an entity-bound API whose
                // synchronous steps see the Target id (verified live; the Target input is still there).
                PrimaryEntityId = globalMessage || (invocation.AsyncDropsPrimaryId && registration.Step.Mode == ExecutionMode.Asynchronous)
                    ? Guid.Empty
                    : invocation.PrimaryId,
                CorrelationId = _correlationId,
                SharedVariables = invocation.SharedVariables ?? _sharedVariables
            };

            if (invocation.Target is Entity entityTarget)
            {
                context.InputParameters["Target"] = entityTarget;
            }
            else if (invocation.Target is EntityReference referenceTarget)
            {
                context.InputParameters["Target"] = referenceTarget;
            }

            if (invocation.ExtraInputs != null)
            {
                foreach (KeyValuePair<string, object> input in invocation.ExtraInputs)
                {
                    context.InputParameters[input.Key] = input.Value;
                }
            }

            // Generic modelled message: surface the whole request as InputParameters (so ctx.Request is fully
            // typed) and, in the Post stage, the response Results as OutputParameters (so ctx.Response works).
            if (invocation.AllInputs != null)
            {
                foreach (KeyValuePair<string, object> input in invocation.AllInputs)
                {
                    context.InputParameters[input.Key] = input.Value;
                }
            }

            if (invocation.Outputs != null)
            {
                foreach (KeyValuePair<string, object> output in invocation.Outputs)
                {
                    context.OutputParameters[output.Key] = output.Value;
                }
            }

            // A plugin's own operation gets InputParameters["FromSandbox"] = true at its PreValidation stage (verified
            // live). Only the PreValidation stage was observed to carry it; the other stages were not, so they do not get it.
            if (invocation.FromSandbox && invocation.Stage == PluginStages.PreValidation)
            {
                context.InputParameters[FromSandboxInput] = true;
            }

            BuildImages(context.PreEntityImages, registration.Step.PreImages, invocation.PreState, invocation.SubordinatePreState);
            BuildImages(context.PostEntityImages, registration.Step.PostImages, invocation.PostState, invocation.SubordinatePostState);
            StampOwningExtension(context, registration.Step);
            if (registration.Step.Mode == ExecutionMode.Asynchronous && invocation.PostState != null)
            {
                // The platform adds this post image to every asynchronous step: the record's primary name and id.
                context.PostEntityImages[AsyncPrimaryNameImage] = PrimaryNameImage(invocation.PostState);
            }

            MaskSecuredImageColumns(registration.Step, new[] { context.PreEntityImages, context.PostEntityImages });

            PipelineExecutionEntry entry = new PipelineExecutionEntry(registration.Step.PluginTypeName, invocation.Message, invocation.Stage, registration.Step.Mode, invocation.EntityLogicalName, _depth);
            _log.Add(entry);
            IPlugin plugin = registration.GetInstance(SecureConfigFor(registration.Step));
            ExecuteWithContext(context, plugin, entry, registration.Step, invocation.ParentContext, invocation.OutsideTransaction);

            // A synchronous step changes the message as it flows: a PreValidation/PreOperation step that replaces an
            // input (a Retrieve's ColumnSet, a RetrieveMultiple's Query, ...) changes what the main operation runs, and
            // a PostOperation step that sets an output changes what the caller receives. (An async step cannot.)
            if (registration.Step.Mode == ExecutionMode.Synchronous)
            {
                CopyBack(context.InputParameters, invocation.AllInputs);
                CopyBack(context.OutputParameters, invocation.Outputs);
            }
        }

        private static void CopyBack(ParameterCollection from, ParameterCollection? to)
        {
            if (to == null)
            {
                return;
            }

            // A removed parameter is removed for the next phase too.
            foreach (string key in to.Keys.Where(key => !from.ContainsKey(key)).ToList())
            {
                to.Remove(key);
            }

            foreach (KeyValuePair<string, object> pair in from)
            {
                if (pair.Key != FromSandboxInput)
                {
                    to[pair.Key] = pair.Value;
                }
            }
        }

        private const string FromSandboxInput = "FromSandbox";
        private const string AsyncPrimaryNameImage = "AsynchronousStepPrimaryName";

        // The messages whose PreValidation-stage shared variables the PreOperation/PostOperation steps see only through
        // ParentContext.SharedVariables (MS Learn; verified live for Create, Update, Delete, CreateMultiple and
        // UpdateMultiple).
        private static bool SplitsPreValidationSharedVariables(string message)
            => string.Equals(message, PluginMessages.Create, StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, PluginMessages.Delete, StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, PluginMessages.UpdateMultiple, StringComparison.OrdinalIgnoreCase);

        // Records, on the invocation, what its step contexts need from the operation that fires it (so an async step
        // dispatched later still sees them), keeping what the caller already set: whether a plugin issued the
        // operation, the ParentContext the operation's contexts have when it is not the calling plugin's (an Upsert's
        // inner Create/Update), and for Create/Update/Delete and the bulk messages the PreOperation/PostOperation
        // shared variables and their stage-30 ParentContext: the operation's main operation, or that of the message
        // whose main operation the Update is (Assign, SetState, Merge). mainInputs gives the stage-30 context's inputs
        // when no platform message set them (by default, the invocation's Target).
        private void CaptureOperationContext(StageInvocation invocation, Func<ParameterCollection>? mainInputs = null)
        {
            if (_frames.Count == 0)
            {
                return;
            }

            OperationFrame frame = _frames.Peek();
            invocation.FromSandbox = frame.FromSandbox;
            if (invocation.Stage == PluginStages.PreValidation || !SplitsPreValidationSharedVariables(invocation.Message))
            {
                invocation.ParentContext ??= frame.ParentContext;
                return;
            }

            frame.MainStageShared ??= CopyParameters(frame.InitialShared);
            invocation.SharedVariables ??= frame.MainStageShared;
            invocation.ParentContext ??= MainStageContext(frame, invocation.Message, invocation.EntityLogicalName, invocation.PrimaryId,
                mainInputs ?? (() => invocation.Target != null ? new ParameterCollection { ["Target"] = invocation.Target } : new ParameterCollection()));
        }

        // The operation's stage-30 context, created on first use (a Delete creates it up front for its root record: its
        // cascaded children's steps run first and have it as their ParentContext too).
        private FakePluginExecutionContext MainStageContext(OperationFrame frame, string message, string entityName, Guid entityId, Func<ParameterCollection> inputs)
        {
            return frame.MainStageContext ??= PlatformContext(
                frame.MainMessage ?? message,
                PluginStages.MainOperation,
                entityName,
                entityId,
                _sharedVariables,
                frame.ParentContext ?? (_contextStack.Count > 0 ? _contextStack.Peek() : null),
                frame.MainInputs ?? inputs());
        }

        // A context no step runs under but that steps see in ParentContext (a stage-30 main operation, a
        // CreateMultiple/UpdateMultiple stage), stamped with the depth, identity and transaction in force now.
        private FakePluginExecutionContext PlatformContext(string message, PluginStages stage, string entityName, Guid entityId, ParameterCollection shared, FakePluginExecutionContext? parent, ParameterCollection inputs)
        {
            FakePluginExecutionContext context = new FakePluginExecutionContext
            {
                MessageName = message,
                Stage = (int)stage,
                Mode = (int)ExecutionMode.Synchronous,
                Depth = _depth,
                PrimaryEntityName = entityName,
                PrimaryEntityId = entityId,
                CorrelationId = _correlationId,
                SharedVariables = shared,
                ParentContext = parent,
                IsInTransaction = _transaction != null,
            };
            (context.UserId, context.BusinessUnitId, context.OrganizationId) = _store.CurrentActor();
            context.InitiatingUserId = _initiatingUserId ?? context.UserId;
            foreach (KeyValuePair<string, object> input in inputs)
            {
                context.InputParameters[input.Key] = input.Value;
            }

            return context;
        }

        internal static ParameterCollection CopyParameters(ParameterCollection? source)
        {
            ParameterCollection copy = new ParameterCollection();
            if (source != null)
            {
                foreach (KeyValuePair<string, object> pair in source)
                {
                    copy[pair.Key] = pair.Value;
                }
            }

            return copy;
        }

        // The sdkmessageprocessingstep row an asynchronous step's OwningExtension points at (by id only), so the runtime
        // can read the step's name when several steps of the plugin share the trigger, as it does on Dataverse.
        private void SeedStepRow(StepDefinition step)
        {
            string name = StepName(step);
            Guid id = Security.DeterministicGuid.From("sdkmessageprocessingstep", name);
            if (!_store.TryGet("sdkmessageprocessingstep", id, out _))
            {
                _store.Seed(new Entity("sdkmessageprocessingstep", id) { ["name"] = name });
            }
        }

        // The step registration the context runs under, as the platform reports it in OwningExtension: the step's id,
        // and its name for a synchronous step. An asynchronous step's context carries no name (verified live).
        private static void StampOwningExtension(FakePluginExecutionContext context, StepDefinition step)
        {
            string name = StepName(step);
            context.OwningExtension = new EntityReference("sdkmessageprocessingstep", Security.DeterministicGuid.From("sdkmessageprocessingstep", name))
            {
                Name = step.Mode == ExecutionMode.Asynchronous ? null : name,
            };
        }

        // The name vela apply gives the step ({plugin} - {lifecycle}: {step key, or message [of entity]}).
        internal static string StepName(StepDefinition step)
        {
            string stage = step.Stage switch
            {
                PluginStages.PreValidation => "PreVal",
                PluginStages.PreOperation => "PreOp",
                PluginStages.MainOperation => "MainOp",
                _ => "PostOp",
            };
            string lifecycle = stage + (step.Mode == ExecutionMode.Asynchronous ? "Async" : "Sync");
            string subject = !string.IsNullOrWhiteSpace(step.StepKey)
                ? step.StepKey
                : step.Message + (string.IsNullOrWhiteSpace(step.EntityLogicalName) ? string.Empty : " of " + step.EntityLogicalName.ToLowerInvariant());
            return $"{step.PluginTypeName} - {lifecycle}: {subject}";
        }

        // Images carry only the columns the calling user may read, as a Retrieve does (verified live: a secured column
        // the caller has no read access to is absent from the pre- and post-images, present for an administrator).
        // A step that impersonates a user is masked for that user, whatever the caller's access (verified live).
        private void MaskSecuredImageColumns(StepDefinition step, IEnumerable<EntityImageCollection> collections)
        {
            Guid? impersonated = step.ImpersonatingUserId is Guid user && user != Guid.Empty ? user : (Guid?)null;
            foreach (EntityImageCollection collection in collections)
            {
                foreach (Entity image in collection.Values)
                {
                    if (impersonated.HasValue)
                    {
                        _store.RunAs<object?>(impersonated, () =>
                        {
                            _store.MaskSecuredImageColumns(image);
                            return null;
                        });
                    }
                    else
                    {
                        _store.MaskSecuredImageColumns(image);
                    }
                }
            }
        }

        private Entity PrimaryNameImage(Entity state)
        {
            Entity image = new Entity(state.LogicalName) { Id = state.Id };
            image[_store.PrimaryIdAttribute(state.LogicalName)] = state.Id;
            string nameAttribute = _store.PrimaryNameAttributeName(state.LogicalName);
            if (state.Contains(nameAttribute))
            {
                image[nameAttribute] = state[nameAttribute];
            }

            return image;
        }

        private string? SecureConfigFor(StepDefinition step)
        {
            return !string.IsNullOrEmpty(step.SecureConfigName) && _secureConfigs.TryGetValue(step.SecureConfigName, out string? value)
                ? value
                : null;
        }

        private static void BuildImages(EntityImageCollection target, ImageDefinition[] definitions, Entity? state, Entity? subordinateState = null)
        {
            if (definitions == null)
            {
                return;
            }

            foreach (ImageDefinition definition in definitions)
            {
                // An image registered against SubordinateId (a Merge step) is taken from the subordinate record; every
                // other image from the step's primary record.
                Entity? source = string.Equals(definition.MessagePropertyName, StepImageRules.SubordinateId, StringComparison.OrdinalIgnoreCase)
                    ? subordinateState
                    : state;
                if (source != null)
                {
                    target[definition.Alias] = ProjectImage(source, definition.Attributes);
                }
            }
        }

        private static Entity ProjectImage(Entity state, string[] attributes)
        {
            Entity image = new Entity(state.LogicalName) { Id = state.Id };
            if (attributes == null || attributes.Length == 0)
            {
                foreach (KeyValuePair<string, object> attribute in state.Attributes)
                {
                    image[attribute.Key] = attribute.Value;
                }
            }
            else
            {
                foreach (string name in attributes)
                {
                    if (state.Contains(name))
                    {
                        image[name] = state[name];
                    }
                }
            }

            return image;
        }

        private static bool FiltersMatch(StepDefinition step, ISet<string>? changedAttributes)
        {
            if (step.FilteringAttributes == null || step.FilteringAttributes.Length == 0 || changedAttributes == null)
            {
                return true;
            }

            return step.FilteringAttributes.Any(changedAttributes.Contains);
        }

        private static StageInvocation NewInvocation(string message, PluginStages stage, string entityLogicalName, Guid primaryId, object? target)
        {
            return new StageInvocation
            {
                Message = message,
                Stage = stage,
                EntityLogicalName = entityLogicalName,
                PrimaryId = primaryId,
                Target = target
            };
        }
    }
}
