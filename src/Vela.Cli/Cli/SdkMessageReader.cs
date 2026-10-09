using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Reads an SDK message's request/response contract from Dataverse's <c>sdkmessage*</c> metadata tables and maps
/// each field's CLR type (via <see cref="ClrTypeNameMapper"/>) to a C# type, producing an <see cref="SdkMessage"/>
/// the pure <see cref="SdkMessageGenerator"/> can turn into typed Request/Response classes. Mirrors the
/// <c>ReadCustomApi</c> path in <see cref="ModelsCommand"/> (live connection, <c>RetrieveMultiple</c> /
/// <c>RetrieveAll</c>).
/// </summary>
/// <summary>A message eligible for generation and the entities it applies to (<c>primaryobjecttypecode</c>). The
/// entity list is captured for future per-entity validation (Phase 3); generation is entity-agnostic today.</summary>
internal sealed record GeneratableMessage(string Name, IReadOnlyList<string> Entities);

internal static class SdkMessageReader
{
    /// <summary>
    /// Lists every message eligible for generation in ALL mode (<c>vela models --messages</c> with no value),
    /// following Microsoft's Event Framework "available events" definition: a non-private message, not one of the
    /// deprecated/generic exclusions, that has at least one <c>sdkmessagefilter</c> which both allows a custom
    /// processing step and is visible. Custom-API-backed names are dropped so <c>--custom-apis</c> owns them (with
    /// richer metadata) and the developer's own authored Custom APIs stay out of the generated set. The caller
    /// still filters names that already have an SDK request class (see <see cref="SdkMessageClassResolver"/>). Each
    /// result carries the entities it applies to (<c>primaryobjecttypecode</c>), captured for future per-entity
    /// validation; generation itself is entity-agnostic. Validated live: ~416 messages, versus ~1707 with
    /// <c>isprivate == false</c> alone.
    /// </summary>
    internal static IReadOnlyList<GeneratableMessage> ListGeneratableMessages(IOrganizationService service)
    {
        // The CLI talks SOAP (IOrganizationService), not the Web API, so the docs' OData query is expressed as a
        // distinct sdkmessage query inner-joined to sdkmessagefilter with the two step-eligibility conditions.
        QueryExpression query = new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("name"),
            Distinct = true,
        };
        query.Criteria.AddCondition("isprivate", ConditionOperator.Equal, false);
        query.Criteria.AddCondition("name", ConditionOperator.NotIn,
            "SetStateDynamicEntity", "RemoveRelated", "SetRelated", "Execute");

        LinkEntity filter = query.AddLink("sdkmessagefilter", "sdkmessageid", "sdkmessageid", JoinOperator.Inner);
        filter.EntityAlias = "filter";
        filter.Columns = new ColumnSet("primaryobjecttypecode");
        filter.LinkCriteria.AddCondition("iscustomprocessingstepallowed", ConditionOperator.Equal, true);
        filter.LinkCriteria.AddCondition("isvisible", ConditionOperator.Equal, true);

        // A Custom API's sdkmessage.name equals the customapi uniquename; drop those so --custom-apis owns them.
        QueryExpression customApiQuery = new QueryExpression("customapi") { ColumnSet = new ColumnSet("uniquename") };
        HashSet<string> customApiNames = service.RetrieveAll(customApiQuery)
            .Select(e => e.GetAttributeValue<string>("uniquename"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return service.RetrieveAll(query)
            .Select(e => (
                Name: e.GetAttributeValue<string>("name") ?? string.Empty,
                Entity: e.GetAttributeValue<AliasedValue>("filter.primaryobjecttypecode")?.Value as string))
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !customApiNames.Contains(r.Name))
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GeneratableMessage(
                g.Key,
                g.Select(r => r.Entity)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// Reads the request/response contract of the SDK request <paramref name="requestName"/>, the name Dataverse
    /// executes (<c>SendEmail</c>, <c>WinQuote</c>). It is not always the name of the <c>sdkmessage</c> the request
    /// runs as (<c>Send</c>, <c>Win</c>), and executing a request under that name fails with "Request not supported"
    /// (verified live), so an sdkmessage name is rejected with the request names to use instead.
    /// </summary>
    internal static SdkMessage ReadSdkMessage(IOrganizationService service, string requestName)
    {
        // Each request has one sdkmessagepair per SOAP contract version, and the field CLR types differ by version:
        // the legacy 2007/WebServices pair carries null or pre-Xrm types (e.g. Microsoft.Crm.Sdk.SecurityPrincipal)
        // while 2011/Contracts (and xrm/*/Contracts) carry the modern Microsoft.Xrm.Sdk.* types. The pair order is
        // not stable across environments, so take the request of the preferred version: 2011/Contracts, then any
        // other Contracts (7.1/...), the legacy pair last. (Validated live against two environments.)
        QueryExpression requestQuery = new QueryExpression("sdkmessagerequest") { ColumnSet = new ColumnSet("sdkmessagerequestid", "name", "primaryobjecttypecode") };
        requestQuery.Criteria.AddCondition("name", ConditionOperator.Equal, requestName);
        LinkEntity pairLink = requestQuery.AddLink("sdkmessagepair", "sdkmessagepairid", "sdkmessagepairid", JoinOperator.Inner);
        pairLink.EntityAlias = "pair";
        pairLink.Columns = new ColumnSet("namespace", "sdkmessageid");
        Entity? request = service.RetrieveAll(requestQuery)
            .OrderBy(r => PairPreference(Aliased<string>(r, "pair.namespace")))
            .FirstOrDefault();
        if (request == null)
        {
            IReadOnlyList<string> requestNames = ListRequestNames(service, requestName);
            throw new InvalidOperationException(requestNames.Count == 0
                ? $"SDK request '{requestName}' was not found."
                : $"'{requestName}' is the sdkmessage a step registers on, not a request Dataverse executes. Generate {string.Join(", ", requestNames)} instead.");
        }

        // A request bound to one table (SendEmail on email, WinQuote on quote) only runs on that table.
        string? table = request.GetAttributeValue<string>("primaryobjecttypecode");
        bool bound = !string.IsNullOrWhiteSpace(table) && !string.Equals(table, "none", StringComparison.OrdinalIgnoreCase);
        Guid? messageId = Aliased<EntityReference>(request, "pair.sdkmessageid")?.Id;
        return new SdkMessage(
            request.GetAttributeValue<string>("name") ?? requestName,
            ReadRequestFields(service, request.Id),
            ReadResponseFields(service, request.Id))
        {
            Entities = bound ? new[] { table! } : messageId is Guid id ? ReadApplicableEntities(service, id) : Array.Empty<string>(),
        };
    }

    /// <summary>
    /// The names of the requests Dataverse executes as the sdkmessage <paramref name="messageName"/>: usually the
    /// message name itself, but <c>Send</c> is executed as <c>SendEmail</c>, <c>SendFax</c> and <c>SendTemplate</c>.
    /// A legacy placeholder that is not a valid identifier (<c>SetState{Entity.PrimaryEntityName}</c>) is skipped.
    /// </summary>
    internal static IReadOnlyList<string> ListRequestNames(IOrganizationService service, string messageName)
    {
        QueryExpression query = new QueryExpression("sdkmessagerequest") { ColumnSet = new ColumnSet("name") };
        LinkEntity pair = query.AddLink("sdkmessagepair", "sdkmessagepairid", "sdkmessagepairid", JoinOperator.Inner);
        LinkEntity message = pair.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.Inner);
        message.LinkCriteria.AddCondition("name", ConditionOperator.Equal, messageName);

        return service.RetrieveAll(query)
            .Select(r => r.GetAttributeValue<string>("name"))
            .Where(n => !string.IsNullOrWhiteSpace(n) && n!.All(c => char.IsLetterOrDigit(c) || c == '_'))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static T? Aliased<T>(Entity row, string alias) where T : class
        => row.GetAttributeValue<AliasedValue>(alias)?.Value as T;

    // The entities a message applies to (its step-eligible, visible sdkmessagefilter rows'
    // primaryobjecttypecode), captured so the generator can emit an entity-constrained On<Message>() step
    // extension per entity (Phase 3b). Mirrors the filter conditions in ListGeneratableMessages; skips
    // null/empty codes, dedupes and orders.
    private static IReadOnlyList<string> ReadApplicableEntities(IOrganizationService service, Guid messageId)
    {
        QueryExpression query = new QueryExpression("sdkmessagefilter") { ColumnSet = new ColumnSet("primaryobjecttypecode") };
        query.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, messageId);
        query.Criteria.AddCondition("iscustomprocessingstepallowed", ConditionOperator.Equal, true);
        query.Criteria.AddCondition("isvisible", ConditionOperator.Equal, true);

        // A global (entity-agnostic) message uses the sentinel primaryobjecttypecode "none" (verified live); it is
        // NOT a concrete entity, so drop it - an empty result makes the generator emit the generic On<TEntity>()
        // extension instead of referencing a non-existent "None" early-bound type.
        return service.RetrieveAll(query)
            .Select(e => e.GetAttributeValue<string>("primaryobjecttypecode"))
            .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "none", StringComparison.OrdinalIgnoreCase))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<SdkMessageField> ReadRequestFields(IOrganizationService service, Guid requestId)
    {
        QueryExpression query = new QueryExpression("sdkmessagerequestfield") { ColumnSet = new ColumnSet("name", "clrparser", "position", "optional") };
        query.Criteria.AddCondition("sdkmessagerequestid", ConditionOperator.Equal, requestId);

        return service.RetrieveAll(query)
            .Select(e => (
                Name: e.GetAttributeValue<string>("name") ?? string.Empty,
                Clr: e.GetAttributeValue<string>("clrparser"),
                Position: e.GetAttributeValue<int>("position")))
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .OrderBy(f => f.Position)
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(f => new SdkMessageField(f.Name, ClrTypeNameMapper.Map(f.Clr)))
            .ToList();
    }

    private static IReadOnlyList<SdkMessageField> ReadResponseFields(IOrganizationService service, Guid requestId)
    {
        QueryExpression responseQuery = new QueryExpression("sdkmessageresponse") { ColumnSet = new ColumnSet("sdkmessageresponseid"), TopCount = 1 };
        responseQuery.Criteria.AddCondition("sdkmessagerequestid", ConditionOperator.Equal, requestId);
        Entity? response = service.RetrieveMultiple(responseQuery).Entities.FirstOrDefault();
        if (response == null)
        {
            return Array.Empty<SdkMessageField>();
        }

        QueryExpression fieldQuery = new QueryExpression("sdkmessageresponsefield") { ColumnSet = new ColumnSet("name", "clrformatter", "position") };
        fieldQuery.Criteria.AddCondition("sdkmessageresponseid", ConditionOperator.Equal, response.Id);

        return service.RetrieveAll(fieldQuery)
            .Select(e => (
                Name: e.GetAttributeValue<string>("name") ?? string.Empty,
                Clr: e.GetAttributeValue<string>("clrformatter"),
                Position: e.GetAttributeValue<int>("position")))
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .OrderBy(f => f.Position)
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(f => new SdkMessageField(f.Name, ClrTypeNameMapper.Map(f.Clr)))
            .ToList();
    }

    // Rank of a pair's contract version, lowest first: 2011/Contracts carries the modern Microsoft.Xrm.Sdk.* field
    // types, then any other *Contracts (e.g. xrm/7.1/Contracts), then anything but the legacy 2007/WebServices.
    private static int PairPreference(string? pairNamespace)
    {
        string ns = pairNamespace ?? string.Empty;
        bool Has(string token) => ns.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        return Has("2011/Contracts") ? 0 : Has("Contracts") ? 1 : !Has("2007/WebServices") ? 2 : 3;
    }
}
