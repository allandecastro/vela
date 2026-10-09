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
        private void FireStage(StageInvocation invocation)
        {
            List<PipelineRegistration> steps = _registrations
                // A disabled step (.Disabled()) is registered but dormant: it never fires, as on a real org.
                .Where(registration => registration.Step.Enabled
                    && registration.Step.Stage == invocation.Stage
                    && string.Equals(registration.Step.Message, invocation.Message, StringComparison.OrdinalIgnoreCase)
                    // A generic modelled message may not resolve a primary entity from the request; match its
                    // steps by (message, stage) alone in that case. Likewise an entity-none step (empty logical
                    // name, an intentionally global step) matches any entity, and a global message
                    // (Associate/Disassociate: sdkmessagefilter "none") fires regardless of the entity the step was
                    // declared on - mirroring the runtime's ResolveSingleMatchingStep and the CLI's registration.
                    && (string.IsNullOrEmpty(invocation.EntityLogicalName)
                        || PluginMessages.IsGlobalMessage(invocation.Message)
                        || string.IsNullOrEmpty(registration.Step.EntityLogicalName)
                        || string.Equals(registration.Step.EntityLogicalName, invocation.EntityLogicalName, StringComparison.OrdinalIgnoreCase)))
                .Where(registration => !string.Equals(invocation.Message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase)
                    || FiltersMatch(registration.Step, invocation.ChangedAttributes))
                .Where(registration => !_bypass.ShouldBypass(registration.Step))
                .OrderBy(registration => registration.Step.ExecutionOrder)
                .ToList();

            foreach (PipelineRegistration registration in steps)
            {
                PipelineRegistration captured = registration;
                if (captured.Step.Mode == ExecutionMode.Asynchronous)
                {
                    QueueAsyncStep(() => Dispatch(captured, invocation));
                }
                else
                {
                    Dispatch(captured, invocation);
                }
            }
        }

        // Fires one stage of a Create/Update batch. The two registration messages are merged: a bulk step
        // (CreateMultiple/UpdateMultiple) fires once with the whole Targets collection; a single step
        // (Create/Update) fires once per row. Both are ordered by execution order and filtered by FilterOn.
        private void FireBatchStage(string singleMessage, string bulkMessage, PluginStages stage, string entityName, IReadOnlyList<BatchItem> items)
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
                    Emit(captured, () => DispatchBulk(captured, bulkMessage, stage, entityName, capturedTargets, items));
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
                        invocation.ChangedAttributes = capturedItem.ChangedAttributes;
                        // A Create Post step sees the created id as OutputParameters["id"], exactly as Dataverse
                        // surfaces it, so ctx.Response.id resolves in a post-create handler.
                        if (!isUpdate && stage == PluginStages.PostOperation)
                        {
                            invocation.Outputs = new ParameterCollection { { "id", capturedItem.Target.Id } };
                        }
                        Emit(captured, () => Dispatch(captured, invocation));
                    }
                }
            }
        }

        // Runs the dispatch now for a synchronous step, or defers it to after the synchronous commit for an
        // asynchronous one — the same sync/async split FireStage applies to single-operation steps.
        private void Emit(PipelineRegistration registration, Action dispatch)
        {
            if (registration.Step.Mode == ExecutionMode.Asynchronous)
            {
                QueueAsyncStep(dispatch);
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

        private void DispatchBulk(PipelineRegistration registration, string message, PluginStages stage, string entityName, EntityCollection targets, IReadOnlyList<BatchItem> items)
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
                IsInTransaction = IsInTransactionFor(stage, registration.Step.Mode),
                SharedVariables = _sharedVariables
            };

            // Bulk steps read the batch via ctx.BulkTargets (InputParameters["Targets"]) and per-row pre/post
            // images via the image collections (aligned by target index).
            context.InputParameters["Targets"] = targets;
            context.PreEntityImagesCollection = items.Select(item => BuildImageCollection(registration.Step.PreImages, item.PreState)).ToArray();
            context.PostEntityImagesCollection = items.Select(item => BuildImageCollection(registration.Step.PostImages, item.PostState)).ToArray();

            // A CreateMultiple Post step sees the created ids as OutputParameters["Ids"], exactly as Dataverse
            // surfaces them, so ctx.Response.Ids resolves in a post-create-multiple handler.
            if (string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase)
                && stage == PluginStages.PostOperation)
            {
                context.OutputParameters["Ids"] = targets.Entities.Select(entity => entity.Id).ToArray();
            }

            _log.Add(new PipelineExecutionEntry(registration.Step.PluginTypeName, message, stage, registration.Step.Mode, entityName, _depth));
            IPlugin plugin = registration.CreateInstance(SecureConfigFor(registration.Step));
            ExecuteWithContext(context, plugin, registration.Step);
        }

        private static EntityImageCollection BuildImageCollection(ImageDefinition[] definitions, Entity? state)
        {
            EntityImageCollection collection = new EntityImageCollection();
            BuildImages(collection, definitions, state);
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
                PrimaryEntityId = globalMessage ? Guid.Empty : invocation.PrimaryId,
                CorrelationId = _correlationId,
                IsInTransaction = IsInTransactionFor(invocation.Stage, registration.Step.Mode),
                SharedVariables = _sharedVariables
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

            BuildImages(context.PreEntityImages, registration.Step.PreImages, invocation.PreState, invocation.SubordinatePreState);
            BuildImages(context.PostEntityImages, registration.Step.PostImages, invocation.PostState, invocation.SubordinatePostState);

            _log.Add(new PipelineExecutionEntry(registration.Step.PluginTypeName, invocation.Message, invocation.Stage, registration.Step.Mode, invocation.EntityLogicalName, _depth));
            IPlugin plugin = registration.CreateInstance(SecureConfigFor(registration.Step));
            ExecuteWithContext(context, plugin, registration.Step);
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
