using System;
using System.Diagnostics;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Context;

/// <summary>
/// Decorates <see cref="IOrganizationService"/> with structured tracing for Dataverse operations.
/// </summary>
public sealed class TracingOrganizationService(IOrganizationService innerService, ILocalPluginContext context, string scopeName) : IOrganizationService
{
    private readonly IOrganizationService _innerService = innerService ?? throw new ArgumentNullException(nameof(innerService));
    private readonly ILocalPluginContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly string _scopeName = string.IsNullOrWhiteSpace(scopeName) ? "Service" : scopeName;

    /// <inheritdoc />
    public Guid Create(Entity entity)
    {
        return Trace("Create", $"entity={entity?.LogicalName}", () => _innerService.Create(entity));
    }

    /// <inheritdoc />
    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
    {
        return Trace("Retrieve", $"entity={entityName}; id={id}", () => _innerService.Retrieve(entityName, id, columnSet));
    }

    /// <inheritdoc />
    public void Update(Entity entity)
    {
        Trace("Update", $"entity={entity?.LogicalName}; id={entity?.Id}", () => _innerService.Update(entity));
    }

    /// <inheritdoc />
    public void Delete(string entityName, Guid id)
    {
        Trace("Delete", $"entity={entityName}; id={id}", () => _innerService.Delete(entityName, id));
    }

    /// <inheritdoc />
    public OrganizationResponse Execute(OrganizationRequest request)
    {
        string requestName = request?.RequestName ?? "UnknownRequest";
        return Trace("Execute", $"request={requestName}", () => _innerService.Execute(request));
    }

    /// <inheritdoc />
    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        Trace("Associate", $"entity={entityName}; id={entityId}; relationship={relationship?.SchemaName}", () => _innerService.Associate(entityName, entityId, relationship, relatedEntities));
    }

    /// <inheritdoc />
    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        Trace("Disassociate", $"entity={entityName}; id={entityId}; relationship={relationship?.SchemaName}", () => _innerService.Disassociate(entityName, entityId, relationship, relatedEntities));
    }

    /// <inheritdoc />
    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        string details = query switch
        {
            QueryExpression expression => $"query=QueryExpression; entity={expression.EntityName}; top={expression.TopCount?.ToString() ?? "null"}",
            FetchExpression fetch => $"query=FetchExpression; fetchxml={fetch.Query}",
            QueryByAttribute byAttribute => $"query=QueryByAttribute; entity={byAttribute.EntityName}",
            _ => $"query={query?.GetType().Name ?? "null"}"
        };
        return Trace("RetrieveMultiple", details, () => _innerService.RetrieveMultiple(query));
    }

    private TResult Trace<TResult>(string operation, string details, Func<TResult> action)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string nl = Environment.NewLine;
        _context.Trace(
            "[{0:o}] Dataverse.{1} start{2}" +
            "  scope       : {3}{2}" +
            "  message     : {4}{2}" +
            "  stage/mode  : {5}/{6}{2}" +
            "  depth       : {7}{2}" +
            "  correlation : {8}{2}" +
            "  details     : {9}",
            DateTime.UtcNow,
            operation,
            nl,
            _scopeName,
            _context.MessageName,
            _context.Stage,
            _context.Mode,
            _context.Depth,
            _context.CorrelationId,
            details);

        try
        {
            TResult result = action();
            _context.Trace(
                "[{0:o}] Dataverse.{1} ok{2}" +
                "  scope       : {3}{2}" +
                "  durationMs  : {4}{2}" +
                "  details     : {5}",
                DateTime.UtcNow,
                operation,
                nl,
                _scopeName,
                stopwatch.ElapsedMilliseconds,
                details);
            return result;
        }
        catch (Exception ex)
        {
            _context.Trace(
                "[{0:o}] Dataverse.{1} fail{2}" +
                "  scope       : {3}{2}" +
                "  durationMs  : {4}{2}" +
                "  details     : {5}{2}" +
                "  error       : {6}{2}" +
                "  message     : {7}",
                DateTime.UtcNow,
                operation,
                nl,
                _scopeName,
                stopwatch.ElapsedMilliseconds,
                details,
                ex.GetType().FullName ?? ex.GetType().Name,
                ex.Message ?? string.Empty);
            throw;
        }
    }

    private void Trace(string operation, string details, Action action)
    {
        _ = Trace(operation, details, () =>
        {
            action();
            return true;
        });
    }
}
