using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
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
        Created.Add(entity);
        return OnCreate(entity);
    }

    public void Update(Entity entity) => Updated.Add(entity);

    public void Delete(string entityName, Guid id) => Deleted.Add((entityName, id));

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
        return new OrganizationResponse();
    }

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => throw new NotSupportedException();

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        => throw new NotSupportedException();
}
