using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Context;

/// <summary>
/// Decorates <see cref="IOrganizationService"/> with structured tracing for Dataverse operations.
/// </summary>
public sealed class TracingOrganizationService : IOrganizationService
{
    private readonly IOrganizationService _innerService;
    private readonly ILocalPluginContext _context;
    private readonly string _scopeName;
    private readonly bool _traceEachCall;

    /// <summary>
    /// Initializes a new instance of the <see cref="TracingOrganizationService"/> class that traces every call.
    /// </summary>
    /// <param name="innerService">The organization service to decorate.</param>
    /// <param name="context">The plugin context the traces are written through.</param>
    /// <param name="scopeName">The scope named in each trace (User, System, AsUser...).</param>
    public TracingOrganizationService(IOrganizationService innerService, ILocalPluginContext context, string scopeName)
        : this(innerService, context, scopeName, traceEachCall: true)
    {
    }

    // traceEachCall false (PluginBase.AutoTraceServiceCalls) traces only a failed call, on a single line.
    internal TracingOrganizationService(IOrganizationService innerService, ILocalPluginContext context, string scopeName, bool traceEachCall)
    {
        _innerService = innerService ?? throw new ArgumentNullException(nameof(innerService));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _scopeName = string.IsNullOrWhiteSpace(scopeName) ? "Service" : scopeName;
        _traceEachCall = traceEachCall;
    }

    /// <inheritdoc />
    public Guid Create(Entity entity)
    {
        return Trace("Create", $"entity={entity?.LogicalName}", () => _innerService.Create(ToBaseEntity(entity)));
    }

    /// <inheritdoc />
    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
    {
        return Trace("Retrieve", $"entity={entityName}; id={id}", () => _innerService.Retrieve(entityName, id, columnSet));
    }

    /// <inheritdoc />
    public void Update(Entity entity)
    {
        Trace("Update", $"entity={entity?.LogicalName}; id={entity?.Id}", () => _innerService.Update(ToBaseEntity(entity)));
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
        OrganizationRequest? sanitized = request == null ? null : SanitizeEarlyBoundParameters(request);
        return Trace("Execute", $"request={requestName}", () => _innerService.Execute(sanitized));
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
            FetchExpression fetch => $"query=FetchExpression; fetchxml={RedactFetchXmlValues(fetch.Query)}",
            QueryByAttribute byAttribute => $"query=QueryByAttribute; entity={byAttribute.EntityName}",
            _ => $"query={query?.GetType().Name ?? "null"}"
        };
        return Trace("RetrieveMultiple", details, () => _innerService.RetrieveMultiple(query));
    }

    // FetchXML condition values can be PII (emails, names, ids); the plug-in trace log is broadly readable and
    // often exported, so redact the literal values while keeping the query shape (entities, attributes, operators)
    // for diagnostics. Covers the attribute form (value="..."), the multi-value element form (<value>...</value>) and
    // a CDATA section inside the element (<value><![CDATA[...]]></value>), which can itself contain '<'.
    internal static string RedactFetchXmlValues(string? fetchXml)
    {
        if (string.IsNullOrEmpty(fetchXml))
        {
            return fetchXml ?? string.Empty;
        }

        string redacted = Regex.Replace(fetchXml, "value\\s*=\\s*\"[^\"]*\"", "value=\"***\"");
        redacted = Regex.Replace(redacted, "value\\s*=\\s*'[^']*'", "value='***'");
        redacted = Regex.Replace(redacted, "(<value(?:\\s[^>]*)?>)(?:<!\\[CDATA\\[.*?\\]\\]>|[^<])*(</value>)", "$1***$2", RegexOptions.Singleline);
        return redacted;
    }

    // An early-bound entity (a subclass of Entity, e.g. a generated Account) fails to serialize into the
    // Dataverse sandbox unless the plugin assembly is a proxy-types assembly, throwing a SerializationException
    // ("The deserializer has no knowledge of any type that maps to the name '<namespace>:Account'"). Downcasting
    // to the base Entity keeps the same LogicalName, Id and attributes, so the write succeeds regardless. This
    // makes ctx.Service accept early-bound entities out of the box, including in projects that predate the
    // scaffold's [assembly: ProxyTypesAssembly]. The downcast is deep: an early-bound entity nested in
    // RelatedEntities (deep insert) or in an EntityCollection attribute (activity parties) fails the same way.
    // Nothing the caller owns is mutated: any entity or collection holding an early-bound value is copied.
    internal static Entity? ToBaseEntity(Entity? entity)
    {
        if (entity == null || !ContainsEarlyBound(entity))
        {
            return entity;
        }

        // Built by hand rather than with ToEntity<Entity>(): that shares the source's collections, and the SDK's
        // RelatedEntities setter clears the collection it replaces, which would empty the caller's.
        Entity copy = new Entity(entity.LogicalName, entity.Id)
        {
            EntityState = entity.EntityState,
            RowVersion = entity.RowVersion,
            KeyAttributes = entity.KeyAttributes,
            ExtensionData = entity.ExtensionData,
        };
        foreach (var attribute in entity.Attributes)
        {
            copy.Attributes.Add(attribute.Key, ToBaseValue(attribute.Value));
        }

        foreach (var formatted in entity.FormattedValues)
        {
            copy.FormattedValues.Add(formatted.Key, formatted.Value);
        }

        foreach (var pair in entity.RelatedEntities)
        {
            copy.RelatedEntities.Add(pair.Key, ToBaseCollection(pair.Value));
        }

        return copy;
    }

    private static EntityCollection? ToBaseCollection(EntityCollection? collection)
    {
        if (collection == null || !collection.Entities.Any(ContainsEarlyBound))
        {
            return collection;
        }

        EntityCollection sanitized = new EntityCollection
        {
            EntityName = collection.EntityName,
            MoreRecords = collection.MoreRecords,
            PagingCookie = collection.PagingCookie,
            MinActiveRowVersion = collection.MinActiveRowVersion,
            TotalRecordCount = collection.TotalRecordCount,
            TotalRecordCountLimitExceeded = collection.TotalRecordCountLimitExceeded,
        };
        foreach (Entity item in collection.Entities)
        {
            sanitized.Entities.Add(ToBaseEntity(item)!);
        }

        return sanitized;
    }

    private static object? ToBaseValue(object? value) => value switch
    {
        Entity entity => ToBaseEntity(entity),
        EntityCollection collection => ToBaseCollection(collection),
        _ => value,
    };

    private static bool ContainsEarlyBound(Entity? entity) =>
        entity != null
        && (entity.GetType() != typeof(Entity)
            || entity.Attributes.Any(a => ValueContainsEarlyBound(a.Value))
            || entity.RelatedEntities.Any(r => r.Value != null && r.Value.Entities.Any(ContainsEarlyBound)));

    private static bool ValueContainsEarlyBound(object? value) => value switch
    {
        Entity entity => ContainsEarlyBound(entity),
        EntityCollection collection => collection.Entities.Any(ContainsEarlyBound),
        _ => false,
    };

    // Downcast early-bound entities carried inside a request (Target, Targets, and any other Entity /
    // EntityCollection parameter) for the same reason - covers CreateRequest/UpdateRequest, CreateMultiple/
    // UpdateMultiple, Upsert, and custom requests. The caller's request is left untouched: a copy of the same
    // request type carries the sanitized parameters, so the plug-in can still read its own request after Execute.
    internal static OrganizationRequest SanitizeEarlyBoundParameters(OrganizationRequest request)
    {
        if (request.Parameters == null || !request.Parameters.Any(p => ValueContainsEarlyBound(p.Value)))
        {
            return request;
        }

        ParameterCollection parameters = new ParameterCollection();
        foreach (var parameter in request.Parameters)
        {
            parameters.Add(parameter.Key, ToBaseValue(parameter.Value));
        }

        // The same request type keeps the typed response and any type-based dispatch (e.g. a test double); a
        // request type without a public parameterless constructor falls back to the base OrganizationRequest.
        OrganizationRequest copy = request.GetType().GetConstructor(Type.EmptyTypes) != null
            ? (OrganizationRequest)Activator.CreateInstance(request.GetType())
            : new OrganizationRequest();
        copy.RequestName = request.RequestName;
        copy.RequestId = request.RequestId;
        copy.ExtensionData = request.ExtensionData;
        copy.Parameters = parameters;
        return copy;
    }

    // One line when the call starts (so a call that hangs or times out is still visible) and one when it ends: the
    // plug-in trace log is capped (about 10 KB), so a verbose multi-line block per call would crowd out the handler's
    // own traces. Only metadata is traced (operation, table, ids, request name), never attribute values. With
    // per-call tracing off, a successful call writes nothing and a failed one writes a single line that also carries
    // the start line's scope, depth and details, since no start line precedes it.
    private TResult Trace<TResult>(string operation, string details, Func<TResult> action)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (_traceEachCall)
        {
            _context.Trace("Dataverse.{0} start scope={1} depth={2} {3}", operation, _scopeName, _context.Depth, details);
        }

        try
        {
            TResult result = action();
            if (_traceEachCall)
            {
                _context.Trace("Dataverse.{0} ok {1} ms", operation, stopwatch.ElapsedMilliseconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            // The exception type only: a service fault's message can echo submitted values or record names, and this
            // line is metadata-only. The exception itself propagates to the plugin with its full message.
            string exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
            if (_traceEachCall)
            {
                _context.Trace("Dataverse.{0} fail {1} ms {2}", operation, stopwatch.ElapsedMilliseconds, exceptionType);
            }
            else
            {
                _context.Trace(
                    "Dataverse.{0} fail {1} ms {2} scope={3} depth={4} {5}",
                    operation,
                    stopwatch.ElapsedMilliseconds,
                    exceptionType,
                    _scopeName,
                    _context.Depth,
                    details);
            }

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
