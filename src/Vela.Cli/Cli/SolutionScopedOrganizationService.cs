using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Wraps an <see cref="IOrganizationService"/> so every Create/Update also adds the affected record to a solution,
/// via the <c>SolutionUniqueName</c> request parameter. Dataverse ignores a <c>solutionid</c> set directly on the
/// record; the request parameter is the supported way to place created components (plugin package/assembly/types,
/// steps, step images, secure config, Custom APIs and their parameters) into a solution. Reads, deletes and other
/// messages pass straight through — only creations/updates need the solution context.
/// </summary>
internal sealed class SolutionScopedOrganizationService(IOrganizationService inner, string solutionUniqueName) : IOrganizationService
{
    private readonly IOrganizationService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly string _solutionUniqueName = solutionUniqueName ?? throw new ArgumentNullException(nameof(solutionUniqueName));

    /// <summary>The service this one wraps (the connection).</summary>
    public IOrganizationService Inner => _inner;

    public Guid Create(Entity entity)
    {
        CreateRequest request = new() { Target = entity };
        request["SolutionUniqueName"] = _solutionUniqueName;
        return ((CreateResponse)_inner.Execute(request)).id;
    }

    public void Update(Entity entity)
    {
        UpdateRequest request = new() { Target = entity };
        request["SolutionUniqueName"] = _solutionUniqueName;
        _inner.Execute(request);
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => _inner.Retrieve(entityName, id, columnSet);

    public void Delete(string entityName, Guid id) => _inner.Delete(entityName, id);

    public OrganizationResponse Execute(OrganizationRequest request) => _inner.Execute(request);

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        _inner.Associate(entityName, entityId, relationship, relatedEntities);

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        _inner.Disassociate(entityName, entityId, relationship, relatedEntities);

    public EntityCollection RetrieveMultiple(QueryBase query) => _inner.RetrieveMultiple(query);
}
