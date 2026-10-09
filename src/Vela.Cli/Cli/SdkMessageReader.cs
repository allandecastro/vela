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
    /// Reads the request/response contract of every request that runs as <paramref name="name"/>: one
    /// <see cref="SdkMessage"/> per request, named after the request, which is the name Dataverse executes. Most
    /// messages have one request of the same name, but some differ (the <c>Send</c> sdkmessage is run by
    /// <c>SendEmail</c>, <c>SendFax</c> and <c>SendTemplate</c>), and executing <c>"Send"</c> fails with "Request not
    /// supported" (verified live). <paramref name="name"/> can also be a request name (<c>WinQuote</c>), which reads
    /// that request only.
    /// </summary>
    internal static IReadOnlyList<SdkMessage> ReadSdkMessages(IOrganizationService service, string name)
    {
        string? onlyRequest = null;
        Entity? message = FindMessage(service, name);
        if (message == null && !string.Equals(Vela.Constants.SdkMessageNames.ForRequest(name), name, StringComparison.OrdinalIgnoreCase))
        {
            onlyRequest = name;
            message = FindMessage(service, Vela.Constants.SdkMessageNames.ForRequest(name));
        }

        if (message == null)
        {
            throw new InvalidOperationException($"SDK message '{name}' was not found.");
        }

        string messageName = message.GetAttributeValue<string>("name") ?? name;

        // A message has one pair per (request, SOAP contract version), and the field CLR types differ by version:
        // the legacy 2007/WebServices pairs carry null or pre-Xrm types (e.g. Microsoft.Crm.Sdk.SecurityPrincipal)
        // while 2011/Contracts (and xrm/*/Contracts) carry the modern Microsoft.Xrm.Sdk.* types. The pair order is
        // not stable across environments, so walk the pairs from the preferred version down (2011/Contracts, then any
        // other Contracts, the legacy pairs last) and keep each request from the first pair that has it. (Validated
        // live: the Win message has a pair per version for WinOpportunity and another set for WinQuote.)
        QueryExpression pairQuery = new QueryExpression("sdkmessagepair") { ColumnSet = new ColumnSet("sdkmessagepairid", "namespace") };
        pairQuery.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, message.Id);
        List<Entity> pairs = service.RetrieveAll(pairQuery);
        if (pairs.Count == 0)
        {
            throw new InvalidOperationException($"SDK message '{messageName}' has no message pair.");
        }

        Dictionary<string, Entity> requestsByName = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach (Entity pair in pairs.OrderBy(PairPreference))
        {
            QueryExpression requestQuery = new QueryExpression("sdkmessagerequest") { ColumnSet = new ColumnSet("sdkmessagerequestid", "name", "primaryobjecttypecode") };
            requestQuery.Criteria.AddCondition("sdkmessagepairid", ConditionOperator.Equal, pair.Id);
            foreach (Entity row in service.RetrieveAll(requestQuery))
            {
                string requestName = RequestName(row, messageName);
                if ((onlyRequest == null || string.Equals(requestName, onlyRequest, StringComparison.OrdinalIgnoreCase))
                    && !requestsByName.ContainsKey(requestName))
                {
                    requestsByName.Add(requestName, row);
                }
            }
        }

        List<(Entity Row, string Name)> requests = requestsByName
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => (Row: r.Value, Name: r.Key))
            .ToList();
        if (requests.Count == 0)
        {
            throw new InvalidOperationException($"SDK message '{name}' has no request definition.");
        }

        IReadOnlyList<string> entities = ReadApplicableEntities(service, message.Id);
        return requests
            .Select(r =>
            {
                // A request bound to one table (SendEmail on email, WinQuote on quote) only runs on that table.
                string? table = r.Row.GetAttributeValue<string>("primaryobjecttypecode");
                bool bound = !string.IsNullOrWhiteSpace(table) && !string.Equals(table, "none", StringComparison.OrdinalIgnoreCase);
                return new SdkMessage(r.Name, ReadRequestFields(service, r.Row.Id), ReadResponseFields(service, r.Row.Id))
                {
                    MessageName = messageName,
                    Entities = bound ? new[] { table! } : entities,
                };
            })
            .ToList();
    }

    private static Entity? FindMessage(IOrganizationService service, string messageName)
    {
        QueryExpression messageQuery = new QueryExpression("sdkmessage") { ColumnSet = new ColumnSet("sdkmessageid", "name"), TopCount = 1 };
        messageQuery.Criteria.AddCondition("name", ConditionOperator.Equal, messageName);
        return service.RetrieveMultiple(messageQuery).Entities.FirstOrDefault();
    }

    // The request's own name, the one Dataverse executes. Falls back to the message name when the row has none, or a
    // name that cannot be a C# class name (the legacy "SetState{Entity.PrimaryEntityName}" placeholder).
    private static string RequestName(Entity request, string messageName)
    {
        string? name = request.GetAttributeValue<string>("name");
        return !string.IsNullOrWhiteSpace(name) && name!.All(c => char.IsLetterOrDigit(c) || c == '_') ? name : messageName;
    }

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
    private static int PairPreference(Entity pair)
    {
        string ns = pair.GetAttributeValue<string>("namespace") ?? string.Empty;
        bool Has(string token) => ns.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        return Has("2011/Contracts") ? 0 : Has("Contracts") ? 1 : !Has("2007/WebServices") ? 2 : 3;
    }
}
