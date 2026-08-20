using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DefinitionNormalizer;
using static Vela.Cli.DefinitionParser;
using static Vela.Cli.DiffEngine;

namespace Vela.Cli;

/// <summary> Custom API upsert/delete, parameters, type mapping, and shared definition parsing. </summary>
internal static partial class DataverseRegistration
{
    internal static void UpsertCustomApi(IOrganizationService service, Dictionary<string, Guid> pluginTypeIds, JsonElement definitionElement, bool verbose = true)
{
    CustomApiApplyDefinition definition = ParseCustomApiDefinition(definitionElement);
    if (!pluginTypeIds.TryGetValue(definition.PluginTypeName, out Guid pluginTypeId))
    {
        throw new InvalidOperationException($"Plugin type not found in Dataverse: {definition.PluginTypeName}");
    }

    Guid? existingApiId = FindCustomApiId(service, definition.UniqueName);
    Entity api = new Entity("customapi");
    api["uniquename"] = definition.UniqueName;
    api["name"] = definition.UniqueName;
    api["displayname"] = definition.UniqueName;
    api["description"] = definition.UniqueName;
    api["isfunction"] = definition.IsFunction;
    api["boundentitylogicalname"] = string.IsNullOrWhiteSpace(definition.BoundEntityLogicalName) ? null : definition.BoundEntityLogicalName;
    api["executeprivilegename"] = string.IsNullOrWhiteSpace(definition.ExecutePrivilegeName) ? null : definition.ExecutePrivilegeName;
    api["isprivate"] = definition.IsPrivate;
    api["allowedcustomprocessingsteptype"] = new OptionSetValue(definition.AllowedCustomProcessingStepType);
    api["bindingtype"] = new OptionSetValue(definition.BindingType);
    api["workflowsdkstepenabled"] = definition.WorkflowSdkStepEnabled;
    api["plugintypeid"] = new EntityReference("plugintype", pluginTypeId);

    Guid apiId;
    if (existingApiId.HasValue)
    {
        EnsureCustomApiImmutablesUnchanged(service, existingApiId.Value, definition);
        api.Id = existingApiId.Value;
        service.Update(api);
        apiId = existingApiId.Value;
        LogOperation("custom-api", "update", definition.UniqueName);
    }
    else
    {
        apiId = service.Create(api);
        LogOperation("custom-api", "create", definition.UniqueName);
    }

    SyncCustomApiRequestParameters(service, apiId, definition.UniqueName, definition.InputParameters, verbose);
    SyncCustomApiResponseProperties(service, apiId, definition.UniqueName, definition.OutputParameters, verbose);
}

    internal static void DeleteCustomApi(IOrganizationService service, JsonElement definitionElement)
{
    CustomApiApplyDefinition definition = ParseCustomApiDefinition(definitionElement);
    Guid? apiId = FindCustomApiId(service, definition.UniqueName);
    if (apiId.HasValue)
    {
        DeleteCustomApiRequestParameters(service, apiId.Value);
        DeleteCustomApiResponseProperties(service, apiId.Value);
        DeleteIfExists(service, "customapi", apiId.Value);
    }
}

    // isFunction, bindingType and the bound entity are fixed when a Custom API is created; Dataverse silently
    // ignores attempts to change them on Update (no error, the value simply stays). Left unguarded, `apply`
    // would report a successful update that never takes effect, so every subsequent plan/apply would show the
    // same phantom change forever (idempotence never converges). Fail fast with guidance instead.
    private static void EnsureCustomApiImmutablesUnchanged(IOrganizationService service, Guid existingApiId, CustomApiApplyDefinition definition)
    {
        Entity existing = service.Retrieve("customapi", existingApiId, new ColumnSet("isfunction", "bindingtype", "boundentitylogicalname"));
        bool existingIsFunction = existing.GetAttributeValue<bool?>("isfunction") ?? false;
        int existingBindingType = existing.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0;
        string existingBoundEntity = existing.GetAttributeValue<string>("boundentitylogicalname") ?? string.Empty;
        string desiredBoundEntity = definition.BoundEntityLogicalName ?? string.Empty;

        List<string> changed = new();
        if (existingIsFunction != definition.IsFunction)
        {
            changed.Add($"isFunction ({existingIsFunction} -> {definition.IsFunction})");
        }
        if (existingBindingType != definition.BindingType)
        {
            changed.Add($"bindingType ({existingBindingType} -> {definition.BindingType})");
        }
        if (!string.Equals(existingBoundEntity, desiredBoundEntity, StringComparison.OrdinalIgnoreCase))
        {
            changed.Add($"boundEntity ('{existingBoundEntity}' -> '{desiredBoundEntity}')");
        }

        if (changed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Custom API '{definition.UniqueName}': {string.Join(", ", changed)} cannot be changed on an " +
                "existing Custom API. These are set at creation and Dataverse ignores such updates, so applying " +
                "would report success but never converge. Delete the Custom API and re-apply to recreate it with " +
                "the new shape, or revert the change.");
        }
    }

    internal static Guid? FindCustomApiId(IOrganizationService service, string uniqueName)
{
    QueryExpression query = new QueryExpression("customapi")
    {
        ColumnSet = new ColumnSet("customapiid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
    Entity? row = service.RetrieveMultiple(query).Entities.FirstOrDefault();
    return row?.Id;
}

    /// <summary>
    /// Declarative description of one Custom API parameter collection (request input vs response output),
    /// capturing the few differences between the two otherwise-identical sync flows.
    /// </summary>
    private sealed record ParameterSyncSpec(
        string EntityName,
        string IdColumn,
        string LogTag,
        string UniqueNameSuffix,
        bool IncludeIsOptional);

    private static readonly ParameterSyncSpec RequestParameterSpec = new(
        EntityName: "customapirequestparameter",
        IdColumn: "customapirequestparameterid",
        LogTag: "input-param",
        UniqueNameSuffix: string.Empty,
        IncludeIsOptional: true);

    private static readonly ParameterSyncSpec ResponsePropertySpec = new(
        EntityName: "customapiresponseproperty",
        IdColumn: "customapiresponsepropertyid",
        LogTag: "output-param",
        UniqueNameSuffix: ".response",
        IncludeIsOptional: false);

    internal static void SyncCustomApiRequestParameters(IOrganizationService service, Guid apiId, string apiUniqueName, CustomApiParameterApplyDefinition[] desired, bool verbose = true)
        => SyncParameterCollection(service, apiId, apiUniqueName, desired, RequestParameterSpec, verbose);

    internal static void SyncCustomApiResponseProperties(IOrganizationService service, Guid apiId, string apiUniqueName, CustomApiParameterApplyDefinition[] desired, bool verbose = true)
        => SyncParameterCollection(service, apiId, apiUniqueName, desired, ResponsePropertySpec, verbose);

    private static void SyncParameterCollection(
        IOrganizationService service,
        Guid apiId,
        string apiUniqueName,
        CustomApiParameterApplyDefinition[] desired,
        ParameterSyncSpec spec,
        bool verbose)
    {
        List<string> columns = new() { spec.IdColumn, "name", "type", "logicalentityname" };
        if (spec.IncludeIsOptional)
        {
            columns.Add("isoptional");
        }

        QueryExpression query = new QueryExpression(spec.EntityName)
        {
            ColumnSet = new ColumnSet(columns.ToArray())
        };
        query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, apiId);
        List<Entity> existingRows = service.RetrieveAll(query);
        Dictionary<string, Entity> existingByName = existingRows
            .Where(x => !string.IsNullOrWhiteSpace(x.GetAttributeValue<string>("name")))
            .ToDictionary(x => x.GetAttributeValue<string>("name")!, StringComparer.OrdinalIgnoreCase);

        HashSet<string> desiredNames = new HashSet<string>(desired.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        foreach (CustomApiParameterApplyDefinition parameter in desired)
        {
            int typeCode = ResolveCustomApiTypeCode(parameter.ParameterTypeName, parameter.IsCollection);
            string details = spec.IncludeIsOptional
                ? $"[{parameter.ParameterTypeName}{(parameter.IsOptional ? ", optional" : "")}]"
                : $"[{parameter.ParameterTypeName}]";

            if (existingByName.TryGetValue(parameter.Name, out Entity? existing))
            {
                Entity update = new Entity(spec.EntityName)
                {
                    Id = existing.Id
                };
                if (spec.IncludeIsOptional)
                {
                    update["isoptional"] = parameter.IsOptional;
                }
                update["type"] = new OptionSetValue(typeCode);
                if (!string.IsNullOrWhiteSpace(parameter.LogicalEntityName))
                {
                    update["logicalentityname"] = parameter.LogicalEntityName;
                }
                service.Update(update);
                LogOperation(spec.LogTag, "update", parameter.Name, details, verbose, indent: true);
                continue;
            }

            Entity create = new Entity(spec.EntityName);
            create["customapiid"] = new EntityReference("customapi", apiId);
            create["name"] = parameter.Name;
            create["displayname"] = parameter.Name;
            create["description"] = parameter.Name;
            create["uniquename"] = $"{apiUniqueName}.{parameter.Name}{spec.UniqueNameSuffix}";
            if (spec.IncludeIsOptional)
            {
                create["isoptional"] = parameter.IsOptional;
            }
            create["type"] = new OptionSetValue(typeCode);
            if (!string.IsNullOrWhiteSpace(parameter.LogicalEntityName))
            {
                create["logicalentityname"] = parameter.LogicalEntityName;
            }
            service.Create(create);
            LogOperation(spec.LogTag, "create", parameter.Name, details, verbose, indent: true);
        }

        foreach (Entity existing in existingRows)
        {
            string name = existing.GetAttributeValue<string>("name") ?? string.Empty;
            if (!desiredNames.Contains(name))
            {
                service.Delete(spec.EntityName, existing.Id);
                LogOperation(spec.LogTag, "delete", name, verbose: verbose, indent: true);
            }
        }
    }

    internal static void DeleteCustomApiRequestParameters(IOrganizationService service, Guid apiId)
{
    QueryExpression query = new QueryExpression("customapirequestparameter")
    {
        ColumnSet = new ColumnSet("customapirequestparameterid")
    };
    query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, apiId);
    foreach (Entity row in service.RetrieveAll(query))
    {
        service.Delete("customapirequestparameter", row.Id);
    }
}

    internal static void DeleteCustomApiResponseProperties(IOrganizationService service, Guid apiId)
{
    QueryExpression query = new QueryExpression("customapiresponseproperty")
    {
        ColumnSet = new ColumnSet("customapiresponsepropertyid")
    };
    query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, apiId);
    foreach (Entity row in service.RetrieveAll(query))
    {
        service.Delete("customapiresponseproperty", row.Id);
    }
}

    internal static int ResolveCustomApiTypeCode(string? parameterTypeName, bool isCollection)
{
    string normalized = (parameterTypeName ?? "System.String").Trim();
    if (isCollection)
    {
        if (normalized.Contains("String", StringComparison.OrdinalIgnoreCase))
        {
            return SdkConstants.CustomApiParameterType.StringArray;
        }

        if (normalized.Contains("Entity", StringComparison.OrdinalIgnoreCase))
        {
            return SdkConstants.CustomApiParameterType.EntityCollection;
        }
    }

    if (normalized.Contains("Boolean", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Boolean;
    }

    if (normalized.Contains("DateTime", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.DateTime;
    }

    if (normalized.Contains("Decimal", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Decimal;
    }

    if (normalized.Contains("EntityReference", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.EntityReference;
    }

    if (normalized.Contains("EntityCollection", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.EntityCollection;
    }

    if (normalized.Contains("Entity", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Entity;
    }

    if (normalized.Contains("Double", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Single", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Float;
    }

    if (normalized.Contains("Int", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Long", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Short", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Integer;
    }

    if (normalized.Contains("Money", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Money;
    }

    if (normalized.Contains("OptionSet", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Picklist", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Picklist;
    }

    if (normalized.Contains("Guid", StringComparison.OrdinalIgnoreCase))
    {
        return SdkConstants.CustomApiParameterType.Guid;
    }

    return SdkConstants.CustomApiParameterType.String;
}

    internal static (string typeName, bool isCollection) ResolveCustomApiTypeName(int typeCode, string? logicalEntityName)
{
    return typeCode switch
    {
        SdkConstants.CustomApiParameterType.Boolean => ("System.Boolean", false),
        SdkConstants.CustomApiParameterType.DateTime => ("System.DateTime", false),
        SdkConstants.CustomApiParameterType.Decimal => ("System.Decimal", false),
        SdkConstants.CustomApiParameterType.Entity => ("Microsoft.Xrm.Sdk.Entity", false),
        SdkConstants.CustomApiParameterType.EntityCollection => ("Microsoft.Xrm.Sdk.Entity", true),
        SdkConstants.CustomApiParameterType.EntityReference => ("Microsoft.Xrm.Sdk.EntityReference", false),
        SdkConstants.CustomApiParameterType.Float => ("System.Double", false),
        SdkConstants.CustomApiParameterType.Integer => ("System.Int32", false),
        SdkConstants.CustomApiParameterType.Money => ("Microsoft.Xrm.Sdk.Money", false),
        SdkConstants.CustomApiParameterType.Picklist => ("Microsoft.Xrm.Sdk.OptionSetValue", false),
        SdkConstants.CustomApiParameterType.StringArray => ("System.String", true),
        SdkConstants.CustomApiParameterType.Guid => ("System.Guid", false),
        _ => ("System.String", false)
    };
}

    internal static string? GetAliasedString(Entity row, string aliasName)
{
    return row.GetAttributeValue<AliasedValue>(aliasName)?.Value as string;
}
}
