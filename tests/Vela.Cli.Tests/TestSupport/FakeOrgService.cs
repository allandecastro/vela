using System;
using System.Collections.Generic;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli.Tests.TestSupport;

/// <summary>
/// A hand-rolled, recording <see cref="IOrganizationService"/> test double. Unlike a Moq strict mock it keeps
/// the apply/reconcile tests readable when a single method issues several different queries and writes: route
/// reads with <see cref="OnRetrieveMultiple"/>/<see cref="OnRetrieve"/> and assert against the recorded
/// <see cref="Queries"/>/<see cref="Created"/>/<see cref="Updated"/>/<see cref="Deleted"/> collections.
/// </summary>
internal sealed class FakeOrgService : IOrganizationService
{
    public Func<QueryExpression, EntityCollection> OnRetrieveMultiple { get; set; } = _ => new EntityCollection();
    public Func<string, Guid, ColumnSet, Entity> OnRetrieve { get; set; } = (e, id, _) => new Entity(e, id);
    public Func<Entity, Guid> OnCreate { get; set; } = _ => Guid.NewGuid();

    public List<QueryExpression> Queries { get; } = new();
    public List<Entity> Created { get; } = new();
    public List<Entity> Updated { get; } = new();
    public List<(string Entity, Guid Id)> Deleted { get; } = new();
    public List<OrganizationRequest> Executed { get; } = new();

    /// <summary>Every write in the order it happened, as "create:entity", "update:entity" or "delete:entity" -
    /// assert ordering across entity types here.</summary>
    public List<string> Writes { get; } = new();

    /// <summary>Every <see cref="ExecuteMultipleRequest"/> issued, in order — assert chunking and tier ordering here.</summary>
    public List<ExecuteMultipleRequest> Batches { get; } = new();

    /// <summary>When true, <see cref="Execute"/> throws on an <see cref="ExecuteMultipleRequest"/> to simulate an
    /// environment that cannot run the batch (not supported / timeout), forcing the one-by-one fallback.</summary>
    public bool FailWholeBatch { get; set; }

    /// <summary>When true, an <see cref="ExecuteMultipleRequest"/> RUNS and then throws (a timeout or dropped connection
    /// after the server executed it), so the one-by-one fallback replays writes that already landed.</summary>
    public bool ThrowAfterBatchRuns { get; set; }

    /// <summary>Routes <see cref="RetrieveMultiple"/> by entity name; the default lambda returns an empty page.</summary>
    public void RouteRetrieveMultiple(Func<string, EntityCollection> byEntity)
        => OnRetrieveMultiple = q => byEntity(q.EntityName);

    public static EntityCollection Collection(params Entity[] entities)
    {
        var ec = new EntityCollection();
        ec.Entities.AddRange(entities);
        return ec;
    }

    public Guid Create(Entity entity)
    {
        // Like the platform, a create whose (caller-supplied) id already exists fails with "duplicate key".
        if (entity.Id != Guid.Empty && Created.Exists(e => e.Id == entity.Id))
        {
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040237), Message = "Cannot insert duplicate key." },
                "Cannot insert duplicate key.");
        }

        Created.Add(entity);
        Writes.Add("create:" + entity.LogicalName);
        return OnCreate(entity);
    }

    public void Update(Entity entity)
    {
        Updated.Add(entity);
        Writes.Add("update:" + entity.LogicalName);
    }

    /// <summary>Called before a delete is recorded; throw from it to fault that delete.</summary>
    public Action<string, Guid>? OnDelete { get; set; }

    public void Delete(string entityName, Guid id)
    {
        OnDelete?.Invoke(entityName, id);
        Deleted.Add((entityName, id));
        Writes.Add("delete:" + entityName);
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => OnRetrieve(entityName, id, columnSet);

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        var qe = (QueryExpression)query;
        Queries.Add(qe);
        return OnRetrieveMultiple(qe);
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        Executed.Add(request);
        return request switch
        {
            ExecuteMultipleRequest batch => RunExecuteMultiple(batch),
            // Fallback path (one-by-one): route CUD requests through the recording methods so Created/Updated/Deleted
            // stay populated exactly as they would for a direct Create/Update/Delete call.
            // Typed like the SDK, so callers that cast (SolutionScopedOrganizationService) work against the fake.
            CreateRequest create => new CreateResponse { Results = { ["id"] = Create(create.Target) } },
            UpdateRequest update => Apply(() => Update(update.Target), new UpdateResponse()),
            DeleteRequest delete => Apply(() => Delete(delete.Target.LogicalName, delete.Target.Id), new DeleteResponse()),
            _ => new OrganizationResponse(),
        };
    }

    // Mirrors the platform's ExecuteMultiple: run each child in order; ContinueOnError keeps going past a per-item
    // fault; ReturnResponses collects per-item Response/Fault (carrying the child fault's ErrorCode + Message).
    private ExecuteMultipleResponse RunExecuteMultiple(ExecuteMultipleRequest request)
    {
        Batches.Add(request);
        if (FailWholeBatch)
        {
            throw new NotSupportedException("ExecuteMultiple is not supported (simulated).");
        }

        bool continueOnError = request.Settings?.ContinueOnError ?? false;
        bool returnResponses = request.Settings?.ReturnResponses ?? false;
        ExecuteMultipleResponseItemCollection items = new ExecuteMultipleResponseItemCollection();
        bool faulted = false;

        for (int i = 0; i < request.Requests.Count; i++)
        {
            try
            {
                OrganizationResponse childResponse = RunChild(request.Requests[i]);
                if (returnResponses)
                {
                    items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = childResponse });
                }
            }
            catch (Exception ex)
            {
                faulted = true;
                items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Fault = ToFault(ex) });
                if (!continueOnError)
                {
                    break;
                }
            }
        }

        if (ThrowAfterBatchRuns)
        {
            throw new TimeoutException("The request channel timed out (simulated, after the batch ran).");
        }

        ExecuteMultipleResponse response = new ExecuteMultipleResponse();
        response.Results["IsFaulted"] = faulted;
        response.Results["Responses"] = items;
        return response;
    }

    // Dispatches a batched child through the recording methods (Created/Updated/Deleted), without touching Executed
    // (which records only top-level Execute calls).
    private OrganizationResponse RunChild(OrganizationRequest request)
    {
        switch (request)
        {
            case CreateRequest create:
                return Response("CreateResponse", "id", Create(create.Target));
            case UpdateRequest update:
                Update(update.Target);
                return new UpdateResponse();
            case DeleteRequest delete:
                Delete(delete.Target.LogicalName, delete.Target.Id);
                return new DeleteResponse();
            case SetStateRequest:
                return new SetStateResponse();
            default:
                return new OrganizationResponse();
        }
    }

    private static OrganizationServiceFault ToFault(Exception ex)
    {
        if (ex is FaultException<OrganizationServiceFault> fault)
        {
            OrganizationServiceFault detail = fault.Detail ?? new OrganizationServiceFault();
            return new OrganizationServiceFault
            {
                ErrorCode = detail.ErrorCode,
                Message = string.IsNullOrEmpty(detail.Message) ? fault.Message : detail.Message,
            };
        }

        return new OrganizationServiceFault { Message = ex.Message };
    }

    private static OrganizationResponse Response(string name, string key, object value)
    {
        OrganizationResponse response = new OrganizationResponse { ResponseName = name };
        response.Results[key] = value;
        return response;
    }

    private static OrganizationResponse Apply(Action action, OrganizationResponse response)
    {
        action();
        return response;
    }

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => throw new NotSupportedException();

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => throw new NotSupportedException();
}
