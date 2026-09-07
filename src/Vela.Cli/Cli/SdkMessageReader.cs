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

        // A message has several pairs, one per SOAP contract version, and the field CLR types differ by pair:
        // the legacy 2007/WebServices pair carries null or pre-Xrm types (e.g. Microsoft.Crm.Sdk.SecurityPrincipal)
        // while 2011/Contracts (and xrm/*/Contracts) carry the modern Microsoft.Xrm.Sdk.* types. The pair order is
        // not stable across environments, so we cannot take the first - prefer 2011/Contracts, then any other
        // Contracts (7.1/...), and only fall back to the legacy pair when nothing else exists. (Validated live
        // against two environments.)
        QueryExpression pairQuery = new QueryExpression("sdkmessagepair") { ColumnSet = new ColumnSet("sdkmessagepairid", "namespace") };
        pairQuery.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, message.Id);
        List<Entity> pairs = service.RetrieveAll(pairQuery);
        Entity pair = SelectBestPair(pairs)
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
