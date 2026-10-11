using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Reads an SDK message's request/response contract from Dataverse's <c>sdkmessage*</c> metadata tables and maps
/// each field's CLR type (via <see cref="ClrTypeNameMapper"/>) to a C# type, producing an <see cref="SdkMessage"/>
/// per request the message serves, which the pure <see cref="SdkMessageGenerator"/> turns into typed Request/Response
/// classes. Mirrors the
/// <c>ReadCustomApi</c> path in <see cref="ModelsCommand"/> (live connection, <c>RetrieveMultiple</c> /
/// <c>RetrieveAll</c>).
/// </summary>
/// <summary>A message eligible for generation and the entities it applies to (<c>primaryobjecttypecode</c>).</summary>
internal sealed record GeneratableMessage(string Name, IReadOnlyList<string> Entities);

internal static class SdkMessageReader
{
    /// <summary>
    /// Lists every message eligible for generation in ALL mode (<c>vela models --messages</c> with no value),
    /// following Microsoft's Event Framework "available events" definition: a non-private message, not one of the
    /// deprecated/generic exclusions, that has at least one <c>sdkmessagefilter</c> which both allows a custom
    /// processing step and is visible. Custom-API-backed names are dropped so <c>--custom-apis</c> owns them (with
    /// richer metadata) and the developer's own authored Custom APIs stay out of the generated set. The caller
    /// reads each message's requests (<see cref="ReadMessageRequests"/>) and drops those that already have an SDK request
    /// class (see <see cref="SdkMessageClassResolver"/>). Each result carries the entities it applies to
    /// (<c>primaryobjecttypecode</c>). Validated live: ~416 messages, versus ~1707 with
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
    /// Reads the typed contract of the request <paramref name="requestName"/>: the name <c>Execute</c> takes
    /// (<c>CloseQuote</c>, <c>SendEmail</c>, <c>Create</c>), never an sdkmessage alias. A name that is only an
    /// sdkmessage (<c>Close</c>, <c>Send</c>) is rejected with the request names it serves, so the generator makes
    /// exactly what was asked. The contract keeps the sdkmessage its steps register on and applies to the request's
    /// own entity when it has one (verified live 2026-10-09: a shared sdkmessage carries one request per entity, each
    /// with its own fields).
    /// </summary>
    internal static SdkMessage ReadRequest(IOrganizationService service, string requestName)
    {
        List<Entity> owners = FindMessagesOfRequest(service, requestName);
        if (owners.Count == 0)
        {
            Entity? alias = FindMessage(service, requestName);
            if (alias != null)
            {
                IReadOnlyList<string> requests = ReadContracts(service, alias, onlyRequest: null).Select(c => c.Name).ToList();
                throw new InvalidOperationException(
                    $"'{requestName}' is an sdkmessage, not a request: pass the request name you execute ({string.Join(", ", requests)}).");
            }

            throw new InvalidOperationException($"SDK request '{requestName}' was not found.");
        }

        // A request name can live under several sdkmessages (SetState sits under SetState and SetStateDynamicEntity):
        // prefer the sdkmessage of the same name.
        Entity message = owners.FirstOrDefault(m => string.Equals(m.GetAttributeValue<string>("name"), requestName, StringComparison.OrdinalIgnoreCase))
            ?? owners[0];
        return ReadContracts(service, message, requestName).Single();
    }

    /// <summary>
    /// Reads every request the sdkmessage <paramref name="messageName"/> serves (the bare <c>--messages</c>, which
    /// generates all registerable messages): <c>Close</c> yields <c>CloseIncident</c> and <c>CloseQuote</c>.
    /// </summary>
    internal static IReadOnlyList<SdkMessage> ReadMessageRequests(IOrganizationService service, string messageName)
    {
        Entity message = FindMessage(service, messageName)
            ?? throw new InvalidOperationException($"SDK message '{messageName}' was not found.");
        return ReadContracts(service, message, onlyRequest: null);
    }

    private static IReadOnlyList<SdkMessage> ReadContracts(IOrganizationService service, Entity message, string? onlyRequest)
    {
        string messageName = message.GetAttributeValue<string>("name") ?? string.Empty;

        // A message has several pairs, one per SOAP contract version and request, and the field CLR types differ by
        // pair: the legacy 2007/WebServices pair carries null or pre-Xrm types (e.g. Microsoft.Crm.Sdk.SecurityPrincipal)
        // while 2011/Contracts (and xrm/*/Contracts) carry the modern Microsoft.Xrm.Sdk.* types. The pair order is
        // not stable across environments, so we cannot take the first - for each request, prefer 2011/Contracts,
        // then any other Contracts (7.1/...), and only fall back to the legacy pair when nothing else exists.
        // (Validated live against two environments.)
        QueryExpression pairQuery = new QueryExpression("sdkmessagepair") { ColumnSet = new ColumnSet("sdkmessagepairid", "namespace") };
        pairQuery.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, message.Id);
        List<Entity> pairs = service.RetrieveAll(pairQuery);
        if (pairs.Count == 0)
        {
            throw new InvalidOperationException($"SDK message '{messageName}' has no message pair.");
        }

        List<(Entity Pair, Entity Request, string RequestName)> requests = new List<(Entity, Entity, string)>();
        foreach (Entity pair in pairs)
        {
            QueryExpression requestQuery = new QueryExpression("sdkmessagerequest") { ColumnSet = new ColumnSet("sdkmessagerequestid", "name", "primaryobjecttypecode") };
            requestQuery.Criteria.AddCondition("sdkmessagepairid", ConditionOperator.Equal, pair.Id);
            foreach (Entity request in service.RetrieveAll(requestQuery))
            {
                string requestName = request.GetAttributeValue<string>("name");
                requests.Add((pair, request, string.IsNullOrWhiteSpace(requestName) ? messageName : requestName.Trim()));
            }
        }

        if (requests.Count == 0)
        {
            throw new InvalidOperationException($"SDK message '{messageName}' has no request definition.");
        }

        IReadOnlyList<string> messageEntities = ReadApplicableEntities(service, message.Id);
        List<SdkMessage> contracts = new List<SdkMessage>();
        foreach (IGrouping<string, (Entity Pair, Entity Request, string RequestName)> group in requests
            .Where(r => onlyRequest == null || string.Equals(r.RequestName, onlyRequest, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.RequestName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Entity bestPair = SelectBestPair(group.Select(r => r.Pair).ToList())!;
            Entity request = group.First(r => r.Pair.Id == bestPair.Id).Request;

            // The request's entity narrows a shared message to it (CloseQuote applies to quote only). It is null on a
            // request that serves every entity of its message (Create), which keeps the message's whole filter list.
            string? requestEntity = group
                .Select(r => r.Request.GetAttributeValue<string>("primaryobjecttypecode"))
                .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code) && !string.Equals(code, "none", StringComparison.OrdinalIgnoreCase));

            contracts.Add(new SdkMessage(
                group.First().RequestName,
                ReadRequestFields(service, request.Id),
                ReadResponseFields(service, request.Id))
            {
                MessageName = messageName,
                Entities = requestEntity == null
                    ? messageEntities
                    : messageEntities.Where(e => string.Equals(e, requestEntity, StringComparison.OrdinalIgnoreCase)).ToList(),
            });
        }

        if (contracts.Count == 0)
        {
            throw new InvalidOperationException($"SDK message '{messageName}' has no request named '{onlyRequest}'.");
        }

        return contracts;
    }

    private static Entity? FindMessage(IOrganizationService service, string name)
    {
        QueryExpression messageQuery = new QueryExpression("sdkmessage") { ColumnSet = new ColumnSet("sdkmessageid", "name"), TopCount = 1 };
        messageQuery.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        return service.RetrieveMultiple(messageQuery).Entities.FirstOrDefault();
    }

    // The sdkmessages serving a request name, through their pairs (sdkmessagerequest -> sdkmessagepair -> sdkmessage).
    private static List<Entity> FindMessagesOfRequest(IOrganizationService service, string requestName)
    {
        QueryExpression query = new QueryExpression("sdkmessage") { ColumnSet = new ColumnSet("sdkmessageid", "name"), Distinct = true };
        LinkEntity pair = query.AddLink("sdkmessagepair", "sdkmessageid", "sdkmessageid", JoinOperator.Inner);
        LinkEntity request = pair.AddLink("sdkmessagerequest", "sdkmessagepairid", "sdkmessagepairid", JoinOperator.Inner);
        request.LinkCriteria.AddCondition("name", ConditionOperator.Equal, requestName);
        return service.RetrieveMultiple(query).Entities.ToList();
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

    // Picks the pair whose field CLR types are the modern Microsoft.Xrm.Sdk.* ones: prefer 2011/Contracts, then
    // any other *Contracts (e.g. xrm/7.1/Contracts), avoiding the legacy 2007/WebServices pair unless it is the
    // only one present.
    private static Entity? SelectBestPair(List<Entity> pairs)
    {
        if (pairs.Count == 0)
        {
            return null;
        }

        static bool Has(Entity p, string token) =>
            (p.GetAttributeValue<string>("namespace") ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        return pairs.FirstOrDefault(p => Has(p, "2011/Contracts"))
            ?? pairs.FirstOrDefault(p => Has(p, "Contracts"))
            ?? pairs.FirstOrDefault(p => !Has(p, "2007/WebServices"))
            ?? pairs[0];
    }
}
