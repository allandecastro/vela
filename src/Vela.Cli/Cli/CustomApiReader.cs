using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Reads Custom API definitions (with their request parameters and response properties) from the live org for the
/// <c>vela models --custom-apis</c> scope, and applies the package-based exclusion of the Custom APIs a developer
/// authored in code. Split out of <see cref="ModelsCommand"/> so the Custom-API reading responsibility is unit-testable
/// on its own.
/// </summary>
internal static class CustomApiReader
{
    // Reads every Custom API (with its request parameters and response properties) in a fixed number of paged
    // queries - one for `customapi`, one for `customapirequestparameter`, one for `customapiresponseproperty` -
    // joined in memory by customapiid. Replaces the one-plus-two round-trips per API that the ALL-mode loop used to
    // do (which times out on large environments, #211).
    internal static IReadOnlyList<CustomApiMessage> ReadAllCustomApis(IOrganizationService service)
    {
        Dictionary<Guid, List<CustomApiParameter>> requestParameters = GroupCustomApiParameters(service, "customapirequestparameter", isRequest: true);
        Dictionary<Guid, List<CustomApiParameter>> responseProperties = GroupCustomApiParameters(service, "customapiresponseproperty", isRequest: false);

        QueryExpression query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet("customapiid", "uniquename", "bindingtype", "boundentitylogicalname", "allowedcustomprocessingsteptype", "isprivate"),
        };

        List<CustomApiMessage> result = new List<CustomApiMessage>();
        foreach (Entity api in service.RetrieveAll(query))
        {
            string? uniqueName = api.GetAttributeValue<string>("uniquename");
            if (string.IsNullOrWhiteSpace(uniqueName))
            {
                continue;
            }

            int bindingType = api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0;
            string? boundEntity = api.GetAttributeValue<string>("boundentitylogicalname");
            result.Add(new CustomApiMessage(
                uniqueName!,
                IsBoundToEntity: bindingType == 1,
                requestParameters.TryGetValue(api.Id, out List<CustomApiParameter>? rp) ? rp : new List<CustomApiParameter>(),
                responseProperties.TryGetValue(api.Id, out List<CustomApiParameter>? sp) ? sp : new List<CustomApiParameter>(),
                AllowedStepType: api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 2,
                IsPrivate: api.GetAttributeValue<bool>("isprivate"),
                BoundEntityLogicalName: string.IsNullOrWhiteSpace(boundEntity) ? null : boundEntity));
        }

        return result.OrderBy(m => m.UniqueName, StringComparer.Ordinal).ToList();
    }

    // Reads all rows of a Custom API parameter table in one paged query and groups them by their parent Custom API.
    // Mirrors the per-API reader's column/shape choices (see ReadCustomApiParameters), including the by-name order.
    private static Dictionary<Guid, List<CustomApiParameter>> GroupCustomApiParameters(IOrganizationService service, string entity, bool isRequest)
    {
        ColumnSet columns = isRequest
            ? new ColumnSet("name", "type", "isoptional", "logicalentityname", "customapiid")
            : new ColumnSet("name", "type", "logicalentityname", "customapiid");
        QueryExpression query = new QueryExpression(entity) { ColumnSet = columns };

        Dictionary<Guid, List<CustomApiParameter>> byApi = new Dictionary<Guid, List<CustomApiParameter>>();
        foreach (Entity e in service.RetrieveAll(query))
        {
            Guid apiId = e.GetAttributeValue<EntityReference>("customapiid")?.Id ?? Guid.Empty;
            string name = e.GetAttributeValue<string>("name") ?? string.Empty;
            if (apiId == Guid.Empty || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            CustomApiParameter parameter = new CustomApiParameter(
                name,
                e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10,
                isRequest && (e.GetAttributeValue<bool?>("isoptional") ?? false),
                NullIfBlank(e.GetAttributeValue<string>("logicalentityname")));

            if (!byApi.TryGetValue(apiId, out List<CustomApiParameter>? list))
            {
                list = new List<CustomApiParameter>();
                byApi[apiId] = list;
            }

            list.Add(parameter);
        }

        foreach (List<CustomApiParameter> list in byApi.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        return byApi;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static CustomApiMessage ReadCustomApi(IOrganizationService service, string uniqueName)
    {
        QueryExpression query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet("customapiid", "bindingtype", "boundentitylogicalname", "allowedcustomprocessingsteptype", "isprivate"),
            TopCount = 1,
        };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity api = service.RetrieveMultiple(query).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Custom API '{uniqueName}' was not found.");

        // bindingtype: 0 Global, 1 Entity, 2 EntityCollection. Only type 1 auto-adds a single-record Target input,
        // so IsBoundToEntity (which drives that Target property) stays == 1; the extension constraint keys off
        // boundEntity being set instead, so a collection-bound (type 2) API is still entity-constrained (Phase 3c).
        int bindingType = api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0;
        string? boundEntity = api.GetAttributeValue<string>("boundentitylogicalname");
        return new CustomApiMessage(
            uniqueName,
            IsBoundToEntity: bindingType == 1,
            ReadCustomApiParameters(service, "customapirequestparameter", api.Id),
            ReadCustomApiParameters(service, "customapiresponseproperty", api.Id),
            AllowedStepType: api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 2,
            IsPrivate: api.GetAttributeValue<bool>("isprivate"),
            BoundEntityLogicalName: string.IsNullOrWhiteSpace(boundEntity) ? null : boundEntity);
    }

    internal static List<CustomApiParameter> ReadCustomApiParameters(IOrganizationService service, string entity, Guid customApiId)
    {
        // Use the parameter's `name` (e.g. "ContactId"), NOT its `uniquename` (e.g. "zava_ContactValidate.
        // ContactId"): the name is both the C# property identifier and the runtime Parameters/Results key.
        // The dotted uniquename would emit `public Guid zava_ContactValidate.ContactId` (parsed as an explicit
        // interface member, CS0538) and read the wrong Parameters key at runtime.
        // isoptional exists on customapirequestparameter only; customapiresponseproperty has no such column
        // (response properties are always present), so it is requested and read for request parameters alone.
        bool isRequest = entity == "customapirequestparameter";
        ColumnSet columns = isRequest ? new ColumnSet("name", "type", "isoptional", "logicalentityname") : new ColumnSet("name", "type", "logicalentityname");
        QueryExpression query = new QueryExpression(entity) { ColumnSet = columns };
        query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, customApiId);
        return service.RetrieveAll(query)
            .Select(e => new CustomApiParameter(
                e.GetAttributeValue<string>("name") ?? string.Empty,
                e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10,
                isRequest && (e.GetAttributeValue<bool?>("isoptional") ?? false),
                NullIfBlank(e.GetAttributeValue<string>("logicalentityname"))))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    // The unique names of the TYPED Custom APIs authored in the scanned package(s) (CustomApiBase<TRequest, TResponse>):
    // the developer already has their request/response classes, so ALL-mode --custom-apis skips them. A lightweight
    // Custom API (RegisterCustomApi(name, handler)) has no request class in code and is not returned, so it is still
    // generated. Case-insensitive so it matches the environment's uniquename regardless of casing.
    internal static HashSet<string> AuthoredCustomApiUniqueNames(RegistrationState state)
    {
        HashSet<string> typed = new HashSet<string>(state.TypedCustomApiUniqueNames ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(AllAuthoredCustomApiUniqueNames(state).Where(typed.Contains));
        return names;
    }

    // The unique names of the lightweight Custom APIs authored in the scanned package(s): authored, but with no request
    // class in code, so ALL-mode --custom-apis keeps generating them (reported in a note).
    internal static HashSet<string> LightweightAuthoredCustomApiUniqueNames(RegistrationState state)
    {
        HashSet<string> names = new HashSet<string>(AllAuthoredCustomApiUniqueNames(state), StringComparer.OrdinalIgnoreCase);
        names.ExceptWith(state.TypedCustomApiUniqueNames ?? []);
        return names;
    }

    // Every authored Custom API unique name, from the normalized "custom-api" items.
    private static IEnumerable<string> AllAuthoredCustomApiUniqueNames(RegistrationState state)
    {
        foreach (RegistrationItem item in state.Items)
        {
            if (!string.Equals(item.Kind, "custom-api", StringComparison.Ordinal))
            {
                continue;
            }

            if (item.Definition.TryGetProperty("uniqueName", out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? uniqueName = el.GetString();
                if (!string.IsNullOrWhiteSpace(uniqueName))
                {
                    yield return uniqueName!;
                }
            }
        }
    }

    // Drops the developer's own typed Custom APIs from the ALL-mode name list (they already have those classes).
    // Returns the kept names in the original order; excludedCount reports how many were removed. Factored out so the
    // exclusion is unit-testable without a live connection.
    internal static IReadOnlyList<string> ExcludeAuthoredCustomApis(IEnumerable<string> allNames, ISet<string> authoredUniqueNames, out int excludedCount)
    {
        List<string> all = allNames.ToList();
        List<string> kept = all.Where(n => !authoredUniqueNames.Contains(n)).ToList();
        excludedCount = all.Count - kept.Count;
        return kept;
    }
}
