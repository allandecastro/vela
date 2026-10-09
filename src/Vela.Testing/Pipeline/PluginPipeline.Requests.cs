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
    // Request routing: maps an OrganizationRequest to the operation that simulates it, or to the generic
    // modelled-message path, and reads the request-level shared variables and bypass directives.
    public sealed partial class PluginPipeline
    {
        internal OrganizationResponse ExecuteRequest(OrganizationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Bypass parameters apply to the request and its cascade. The effective bypass of a request is the union
            // of the bypass it inherits (from an enclosing request or batch) and its own parameters - at any depth, so
            // a plugin's nested write that sets BypassCustomPluginExecution (the standard way to avoid re-entering
            // itself) is honoured. The inherited bypass is restored once the request returns.
            BypassContext previousBypass = _bypass;
            _bypass = _bypass.Union(ExtractBypass(request));

            try
            {
                return ExecuteRequestCore(request);
            }
            finally
            {
                _bypass = previousBypass;
            }
        }

        private OrganizationResponse ExecuteRequestCore(OrganizationRequest request)
        {
            ParameterCollection shared = ExtractSharedVariables(request);

            if (_customApis.TryGetValue(request.RequestName, out Type? pluginType))
            {
                return ExecuteCustomApi(request, pluginType, shared);
            }

            // Pipeline-aware messages run their registered steps and carry any shared variables the request set
            // (e.g. the client 'tag'). Other messages (metadata/ExecuteMultiple...) hit the store.
            switch (request)
            {
                case CreateRequest create:
                {
                    CreateResponse response = new CreateResponse();
                    response.Results["id"] = ExecuteCreate(create.Target, shared);
                    return response;
                }

                case UpdateRequest update:
                    ExecuteUpdate(update.Target, shared);
                    return new UpdateResponse();

                case OrganizationRequest upsert when IsUpsert(upsert, out Entity? upsertTarget):
                    return ExecuteUpsert(upsert, upsertTarget!, shared);

                case CreateMultipleRequest createMultiple:
                {
                    Guid[] ids = ExecuteCreateMultiple(createMultiple.Targets.Entities.ToArray(), shared);
                    CreateMultipleResponse response = new CreateMultipleResponse();
                    response.Results["Ids"] = ids;
                    return response;
                }

                case UpdateMultipleRequest updateMultiple:
                    ExecuteUpdateMultiple(updateMultiple.Targets.Entities.ToArray(), shared);
                    return new UpdateMultipleResponse();

                case DeleteRequest delete:
                    // A key-addressed target (KeyAttributes, no Id) is resolved first so the Delete steps fire with the
                    // real record id; a key matching nothing throws the platform not-found fault.
                    ExecuteDelete(delete.Target.LogicalName, _store.ResolveReference(delete.Target), shared);
                    return new DeleteResponse();

                case AssociateRequest associate:
                    ExecuteAssociate(associate.Target.LogicalName, associate.Target.Id, associate.Relationship, associate.RelatedEntities, shared);
                    return new AssociateResponse();

                case DisassociateRequest disassociate:
                    ExecuteDisassociate(disassociate.Target.LogicalName, disassociate.Target.Id, disassociate.Relationship, disassociate.RelatedEntities, shared);
                    return new DisassociateResponse();

                case SetStateRequest setState:
                    ExecuteSetState(setState.EntityMoniker, setState.State, setState.Status, shared);
                    return new SetStateResponse();

                case AssignRequest assign:
                    ExecuteAssign(assign, shared);
                    return new AssignResponse();

                case RetrieveRequest retrieve:
                    return ExecuteRetrieveRequest(retrieve, shared);

                case RetrieveMultipleRequest retrieveMultiple:
                {
                    RetrieveMultipleResponse response = new RetrieveMultipleResponse();
                    response.Results["EntityCollection"] = ExecuteRetrieveMultiple(retrieveMultiple.Query, shared);
                    return response;
                }

                case ExecuteMultipleRequest executeMultiple:
                    return ExecuteMultipleThroughPipeline(executeMultiple);

                case ExecuteTransactionRequest executeTransaction:
                    return ExecuteTransactionThroughPipeline(executeTransaction);

                default:
                    return ExecuteModelledMessage(request, shared);
            }
        }

        // A message that only reads: its failure does not end the caller's transaction (verified live for Retrieve).
        internal static bool IsReadOnly(OrganizationRequest request)
        {
            string name = request?.RequestName ?? string.Empty;
            return name.StartsWith("Retrieve", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WhoAmI", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("InitializeFileBlocksDownload", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DownloadBlock", StringComparison.OrdinalIgnoreCase);
        }

        // Upsert is matched by RequestName so both the SDK UpsertRequest and Vela's typed UpsertRequest<T> (a plain
        // OrganizationRequest named "Upsert") run through the pipeline, as the store does.
        private static bool IsUpsert(OrganizationRequest request, out Entity? target)
        {
            target = null;
            if (string.Equals(request.RequestName, "Upsert", StringComparison.OrdinalIgnoreCase)
                && request.Parameters.TryGetValue("Target", out object? value)
                && value is Entity entity)
            {
                target = entity;
                return true;
            }

            return false;
        }

        // ExecuteMultiple/ExecuteTransaction must run each child through the pipeline so registered steps fire,
        // exactly as they would in production. Routing to _store.Execute (the default fall-through) would call the
        // store's own child dispatch, which bypasses the pipeline entirely and gives tests false confidence that
        // no plugin ran. Children go through ExecuteRequest so each child's effective bypass is the batch's bypass
        // united with the child's own bypass parameters.
        private ExecuteMultipleResponse ExecuteMultipleThroughPipeline(ExecuteMultipleRequest request)
        {
            bool continueOnError = request.Settings?.ContinueOnError ?? false;
            bool returnResponses = request.Settings?.ReturnResponses ?? false;
            ExecuteMultipleResponseItemCollection items = new ExecuteMultipleResponseItemCollection();
            bool faulted = false;

            for (int i = 0; i < request.Requests.Count; i++)
            {
                try
                {
                    OrganizationResponse childResponse = ExecuteRequest(request.Requests[i]);
                    if (returnResponses)
                    {
                        items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = childResponse });
                    }
                }
                catch (Exception ex)
                {
                    // Inside a plugin's transaction a failed child ends that transaction, even though ExecuteMultiple
                    // reports it as a per-item fault (the plugin cannot carry on, as with any failed service call).
                    if (_transaction != null && _transaction.AbortedBy == null && !IsReadOnly(request.Requests[i]))
                    {
                        _transaction.AbortedBy = ex;
                    }

                    faulted = true;
                    items.Add(new ExecuteMultipleResponseItem
                    {
                        RequestIndex = i,
                        Fault = ItemFault(ex),
                    });

                    if (!continueOnError)
                    {
                        break;
                    }
                }
            }

            ExecuteMultipleResponse response = new ExecuteMultipleResponse();
            response.Results["IsFaulted"] = faulted;
            response.Results["Responses"] = items;
            return response;
        }

        // Builds the per-item fault for a faulted child, mirroring the store's ExecuteMultipleItemFault: a
        // FaultException<OrganizationServiceFault> carries the platform fault (ErrorCode + Message) in its Detail, so
        // that is copied through verbatim; any other exception has no ErrorCode, so only its message is surfaced.
        private static OrganizationServiceFault ItemFault(Exception exception)
        {
            if (exception is FaultException<OrganizationServiceFault> fault && fault.Detail != null)
            {
                return new OrganizationServiceFault
                {
                    ErrorCode = fault.Detail.ErrorCode,
                    Message = fault.Detail.Message,
                };
            }

            return new OrganizationServiceFault { Message = exception.Message };
        }

        // All-or-nothing: every child runs through the pipeline inside one transaction (opened up front, so each
        // child's PreValidation runs inside it too), and the whole batch rolls back - its queued asynchronous steps
        // included - if any child throws. Mirrors the store's ExecuteTransaction but with plugin steps per child.
        private ExecuteTransactionResponse ExecuteTransactionThroughPipeline(ExecuteTransactionRequest request)
        {
            OrganizationResponseCollection responses = RunInOneTransaction(() =>
            {
                OrganizationResponseCollection collected = new OrganizationResponseCollection();
                foreach (OrganizationRequest child in request.Requests)
                {
                    OrganizationResponse childResponse = ExecuteRequest(child);
                    if (request.ReturnResponses == true)
                    {
                        collected.Add(childResponse);
                    }
                }

                return collected;
            }, eager: true);

            ExecuteTransactionResponse response = new ExecuteTransactionResponse();
            response.Results["Responses"] = responses;
            return response;
        }

        // A message with no dedicated data simulation but with registered steps (for example QualifyLead,
        // WinOpportunity, AddToQueue, SendEmail, CloseIncident, or the sharing messages) fires its Pre/Post steps
        // like any other plugin: the request parameters become InputParameters (so ctx.Request is fully typed)
        // and the response Results become OutputParameters in the Post stage. The data effect itself is not
        // simulated - a fake cannot reproduce the server logic - so the store is asked to execute only if it
        // recognises the request; otherwise the main operation is a no-op returning an empty response. A message
        // with no registered steps falls through to the store unchanged (metadata, file protocol, or its own
        // "unsupported message" error).
        private OrganizationResponse ExecuteModelledMessage(OrganizationRequest request, ParameterCollection shared)
        {
            // Steps register on the sdkmessage, which differs from the request name for some business messages
            // (SendEmailRequest runs the Send steps, WinQuoteRequest the Win steps), and plugins see that name.
            string message = SdkMessageNames.ToSdkMessage(request.RequestName);
            bool hasSteps = _registrations.Any(registration => registration.Step.Enabled
                && string.Equals(registration.Step.Message, message, StringComparison.OrdinalIgnoreCase));
            if (!hasSteps)
            {
                return _store.Execute(request);
            }

            (string entityName, Guid entityId) = DerivePrimaryEntity(request);

            // The steps see (and may change) a copy of the request, so the caller's request object is never mutated
            // and a PreValidation/PreOperation change to an input reaches the main operation.
            request = CopyRequest(request);

            return RunOperation(asyncQueue =>
            {
                // Merge images: the master (Target) and the subordinate (SubordinateId), both snapshotted before the
                // pre stages and again after the merge (the subordinate's post-image is the deactivated record).
                MergeStates? merge = request is MergeRequest mergeRequest ? MergeStates.Before(_store, mergeRequest) : null;

                FireStage(WithMergeStates(GenericInvocation(message, PluginStages.PreValidation, entityName, entityId, request.Parameters, null), merge, post: false), asyncQueue);
                EnterTransaction();
                FireStage(WithMergeStates(GenericInvocation(message, PluginStages.PreOperation, entityName, entityId, request.Parameters, null), merge, post: false), asyncQueue);

                OrganizationResponse response = TryStoreExecute(request) ?? new OrganizationResponse { ResponseName = request.RequestName };

                merge?.After(_store);
                FireStage(WithMergeStates(GenericInvocation(message, PluginStages.PostOperation, entityName, entityId, request.Parameters, response.Results), merge, post: true), asyncQueue);
                return response;
            }, shared);
        }

        private static StageInvocation WithMergeStates(StageInvocation invocation, MergeStates? merge, bool post)
        {
            if (merge != null)
            {
                invocation.PreState = merge.MasterPre;
                invocation.SubordinatePreState = merge.SubordinatePre;
                if (post)
                {
                    invocation.PostState = merge.MasterPost;
                    invocation.SubordinatePostState = merge.SubordinatePost;
                }
            }

            return invocation;
        }

        // The records a Merge step can image, read from the store before and after the merge.
        private sealed class MergeStates
        {
            private readonly MergeRequest _request;
            public Entity? MasterPre;
            public Entity? SubordinatePre;
            public Entity? MasterPost;
            public Entity? SubordinatePost;

            private MergeStates(MergeRequest request) => _request = request;

            public static MergeStates Before(InMemoryOrganizationService store, MergeRequest request)
            {
                MergeStates states = new MergeStates(request);
                (states.MasterPre, states.SubordinatePre) = states.Read(store);
                return states;
            }

            public void After(InMemoryOrganizationService store) => (MasterPost, SubordinatePost) = Read(store);

            private (Entity? Master, Entity? Subordinate) Read(InMemoryOrganizationService store)
            {
                if (_request.Target is not EntityReference target)
                {
                    return (null, null);
                }

                store.TryGet(target.LogicalName, target.Id, out Entity? master);
                store.TryGet(target.LogicalName, _request.SubordinateId, out Entity? subordinate);
                return (master, subordinate);
            }
        }

        private static readonly HashSet<string> NonTransactionalMessages =
            new HashSet<string>(new[] { "Retrieve", "RetrieveMultiple" }, StringComparer.OrdinalIgnoreCase);

        // A copy of the request (same type, so the store still recognises it) with its own parameter collection and
        // its own copies of the mutable SDK values, so a step editing a value in place cannot reach the caller's request.
        internal static OrganizationRequest CopyRequest(OrganizationRequest request)
        {
            OrganizationRequest copy;
            try
            {
                copy = (OrganizationRequest)(Activator.CreateInstance(request.GetType()) ?? new OrganizationRequest());
            }
            catch (MissingMethodException)
            {
                copy = new OrganizationRequest();
            }

            copy.RequestName = request.RequestName;
            copy.RequestId = request.RequestId;
            foreach (KeyValuePair<string, object> parameter in request.Parameters)
            {
                copy.Parameters[parameter.Key] = RequestValues.Clone(parameter.Value)!;
            }

            return copy;
        }

        // The primary entity a Custom API's main operation sees on Dataverse (verified live): "none" and an empty id
        // for a global API, the bound entity and the Target's id for an entity-bound API, and the bound entity with
        // an empty id for an entity-collection-bound API.
        private static (string, Guid) CustomApiPrimaryEntity(CustomApiDefinition? definition, OrganizationRequest request)
        {
            if (definition == null || definition.BindingType == CustomApiBindingType.Global || string.IsNullOrEmpty(definition.BoundEntityLogicalName))
            {
                return ("none", Guid.Empty);
            }

            Guid id = definition.BindingType == CustomApiBindingType.Entity
                && request.Parameters.TryGetValue("Target", out object? target) && target is EntityReference reference
                ? reference.Id
                : Guid.Empty;
            return (definition.BoundEntityLogicalName!, id);
        }

        private static StageInvocation GenericInvocation(string message, PluginStages stage, string entityName, Guid entityId, ParameterCollection inputs, ParameterCollection? outputs)
        {
            return new StageInvocation
            {
                Message = message,
                Stage = stage,
                EntityLogicalName = entityName,
                PrimaryId = entityId,
                AllInputs = inputs,
                Outputs = outputs,
            };
        }

        // Best-effort primary entity for a generic message: an explicit Target, else the first EntityReference /
        // Entity parameter (covers LeadId, Target, EntityMoniker, ...). When nothing resolves, the steps match by
        // (message, stage) alone.
        private static readonly (string Parameter, string Lookup)[] ClosingActivityPrimaries =
        {
            ("OpportunityClose", "opportunityid"),
            ("QuoteClose", "quoteid"),
            ("IncidentResolution", "incidentid"),
        };

        private static (string, Guid) DerivePrimaryEntity(OrganizationRequest request)
        {
            if (request.Parameters.TryGetValue("Target", out object? target))
            {
                if (target is Entity targetEntity)
                {
                    return (targetEntity.LogicalName, targetEntity.Id);
                }

                if (target is EntityReference targetReference)
                {
                    return (targetReference.LogicalName, targetReference.Id);
                }
            }

            // Messages whose primary record is not the first entity-typed parameter: a closing activity whose lookup
            // points at the real primary. The generic scan below would otherwise pick the activity
            // (opportunityclose, incidentresolution, quoteclose) and an entity-filtered step would silently not fire
            // (A15). Verified live: Win/Lose run with PrimaryEntityName opportunity, Close with incident.
            foreach ((string parameterName, string lookup) in ClosingActivityPrimaries)
            {
                if (request.Parameters.TryGetValue(parameterName, out object? activity)
                    && activity is Entity activityEntity
                    && activityEntity.Attributes.TryGetValue(lookup, out object? primary)
                    && primary is EntityReference primaryRef)
                {
                    return (primaryRef.LogicalName, primaryRef.Id);
                }
            }

            foreach (KeyValuePair<string, object> parameter in request.Parameters)
            {
                if (parameter.Value is EntityReference reference)
                {
                    return (reference.LogicalName, reference.Id);
                }

                if (parameter.Value is Entity entity)
                {
                    return (entity.LogicalName, entity.Id);
                }
            }

            return (string.Empty, Guid.Empty);
        }

        // The store simulates a subset of messages (grant/modify/revoke access, sharing reads, ...). For a message
        // it does not model it throws MessageNotModelledException; the generic path still fires the steps around a
        // no-op. Any other failure (a Merge on a table that cannot merge, a fault, ...) is the operation failing.
        private OrganizationResponse? TryStoreExecute(OrganizationRequest request)
        {
            try
            {
                return _store.Execute(request);
            }
            catch (MessageNotModelledException)
            {
                // Only a message the store does not model is a no-op; a modelled message that fails (an invalid
                // Merge, a missing record, a denied privilege) fails the operation as it would on the platform.
                return null;
            }
        }

        // Dataverse promotes the 'tag' request parameter to a shared variable readable by plugins via
        // IExecutionContext.SharedVariables. (The shortcut Create/Update/... methods can't carry it — only Execute.)
        private static ParameterCollection ExtractSharedVariables(OrganizationRequest request)
        {
            ParameterCollection shared = new ParameterCollection();
            if (request.Parameters.TryGetValue("tag", out object? tag) && tag != null)
            {
                shared["tag"] = tag;
            }

            return shared;
        }

        // Reads the platform's "skip business logic" request parameters. BypassCustomPluginExecution skips every
        // custom step; BypassBusinessLogicExecution skips by mode (CustomSync/CustomAsync); and
        // BypassBusinessLogicExecutionStepIds skips specific steps — matched here against the step key (the
        // harness step identity), accepting either a comma-separated string or a collection of keys/ids.
        private static BypassContext ExtractBypass(OrganizationRequest request)
        {
            BypassContext bypass = new BypassContext();

            if (request.Parameters.TryGetValue("BypassCustomPluginExecution", out object? all) && all is bool allBool)
            {
                bypass.AllCustom = allBool;
            }

            if (request.Parameters.TryGetValue("BypassBusinessLogicExecution", out object? mode) && mode != null)
            {
                string text = mode.ToString() ?? string.Empty;
                bypass.BypassSync = text.IndexOf("CustomSync", StringComparison.OrdinalIgnoreCase) >= 0;
                bypass.BypassAsync = text.IndexOf("CustomAsync", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (request.Parameters.TryGetValue("BypassBusinessLogicExecutionStepIds", out object? ids) && ids != null)
            {
                foreach (string key in EnumerateStepKeys(ids))
                {
                    bypass.StepKeys.Add(key);
                }
            }

            return bypass;
        }

        private static IEnumerable<string> EnumerateStepKeys(object value)
        {
            if (value is string text)
            {
                foreach (string part in text.Split(','))
                {
                    string trimmed = part.Trim();
                    if (trimmed.Length > 0)
                    {
                        yield return trimmed;
                    }
                }

                yield break;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (object? item in sequence)
                {
                    string? key = item?.ToString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        yield return key!;
                    }
                }
            }
        }
    }
}
