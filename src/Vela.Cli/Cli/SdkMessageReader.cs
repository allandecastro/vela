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
internal static class SdkMessageReader
{
    internal static SdkMessage ReadSdkMessage(IOrganizationService service, string messageName)
    {
        QueryExpression messageQuery = new QueryExpression("sdkmessage") { ColumnSet = new ColumnSet("sdkmessageid", "name"), TopCount = 1 };
        messageQuery.Criteria.AddCondition("name", ConditionOperator.Equal, messageName);
        Entity message = service.RetrieveMultiple(messageQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"SDK message '{messageName}' was not found.");

        // A message can have more than one pair (e.g. an internal endpoint variant). Prefer the one whose
        // namespace looks like the Organization endpoint, otherwise take the first.
        QueryExpression pairQuery = new QueryExpression("sdkmessagepair") { ColumnSet = new ColumnSet("sdkmessagepairid", "namespace") };
        pairQuery.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, message.Id);
        List<Entity> pairs = service.RetrieveAll(pairQuery);
        Entity pair = pairs.FirstOrDefault(p => LooksLikeOrganizationEndpoint(p.GetAttributeValue<string>("namespace")))
            ?? pairs.FirstOrDefault()
            ?? throw new InvalidOperationException($"SDK message '{messageName}' has no message pair.");

        QueryExpression requestQuery = new QueryExpression("sdkmessagerequest") { ColumnSet = new ColumnSet("sdkmessagerequestid"), TopCount = 1 };
        requestQuery.Criteria.AddCondition("sdkmessagepairid", ConditionOperator.Equal, pair.Id);
        Entity request = service.RetrieveMultiple(requestQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"SDK message '{messageName}' has no request definition.");

        return new SdkMessage(
            messageName,
            ReadRequestFields(service, request.Id),
            ReadResponseFields(service, request.Id));
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

    // The Organization endpoint pair carries the request/response contract we generate against; other pairs
    // (e.g. discovery/internal variants) are less useful. Loose match — the schema needs live validation.
    private static bool LooksLikeOrganizationEndpoint(string? ns) =>
        !string.IsNullOrWhiteSpace(ns) && ns.IndexOf("Organization", StringComparison.OrdinalIgnoreCase) >= 0;
}
