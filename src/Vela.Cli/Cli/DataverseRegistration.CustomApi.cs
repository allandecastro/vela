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
        LogOperation("custom-api", "update", definition.UniqueName, verbose: verbose);
    }
    else
    {
        apiId = service.Create(api);
        LogOperation("custom-api", "create", definition.UniqueName, verbose: verbose);
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
                // A parameter's type is create-only: Dataverse silently ignores an update to it (the value simply
                // stays), so applying a type change would report success but never converge - every later plan
                // would show the same phantom change. Fail fast with guidance, like the parent Custom API
                // immutables (EnsureCustomApiImmutablesUnchanged).
                int existingType = existing.GetAttributeValue<OptionSetValue>("type")?.Value ?? -1;
                if (existingType != typeCode)
                {
                    throw new InvalidOperationException(
                        $"Custom API '{apiUniqueName}': parameter '{parameter.Name}' type ({existingType} -> {typeCode}) " +
                        "cannot be changed on an existing parameter. A parameter's type is set at creation and Dataverse " +
                        "ignores such updates, so applying would report success but never converge. Remove the parameter " +
                        "(or the Custom API) and re-apply to recreate it with the new type.");
                }

                Entity update = new Entity(spec.EntityName)
                {
                    Id = existing.Id
                };
                if (spec.IncludeIsOptional)
                {
                    update["isoptional"] = parameter.IsOptional;
                }
                // type is immutable (guarded unchanged above), so it is not written on update.
                // Always write logicalentityname (null clears it). Only setting it when non-empty left a stale
                // value behind when a parameter's entity binding was removed, so the live read kept surfacing the
                // old table and every subsequent plan/apply reported the Custom API as "changed" forever.
                update["logicalentityname"] = string.IsNullOrWhiteSpace(parameter.LogicalEntityName) ? null : parameter.LogicalEntityName;
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

    // Exact simple-type-name → Custom API scalar type code. Replaces an order-dependent string.Contains ladder
    // where, for example, "Int" matched ANY type name containing "int" (and "Entity" had to be tested after
    // "EntityReference"/"EntityCollection" to avoid a wrong early match). Keyed on the simple (last-segment) name
    // so both the SDK full names the reverse resolver emits ("System.Int32") and short names callers may pass
    // ("Picklist") resolve. Symmetric with ResolveCustomApiTypeName.
    // Keys are simple type names, and cover the three spellings a caller may supply: the SDK type name the reverse
    // resolver emits ("Int32"/"Double"/"OptionSetValue"), the Custom API type label ("Integer"/"Float"/"Picklist"),
    // and the C# alias ("int"/"long"/"double"). All exact — no substring matching, so an unrelated name that merely
    // contains "int" no longer resolves to Integer.
    private static readonly Dictionary<string, int> CustomApiScalarTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boolean"] = SdkConstants.CustomApiParameterType.Boolean,
        ["bool"] = SdkConstants.CustomApiParameterType.Boolean,
        ["DateTime"] = SdkConstants.CustomApiParameterType.DateTime,
        ["Decimal"] = SdkConstants.CustomApiParameterType.Decimal,
        ["decimal"] = SdkConstants.CustomApiParameterType.Decimal,
        ["Entity"] = SdkConstants.CustomApiParameterType.Entity,
        ["EntityCollection"] = SdkConstants.CustomApiParameterType.EntityCollection,
        ["EntityReference"] = SdkConstants.CustomApiParameterType.EntityReference,
        ["Float"] = SdkConstants.CustomApiParameterType.Float,
        ["Double"] = SdkConstants.CustomApiParameterType.Float,
        ["Single"] = SdkConstants.CustomApiParameterType.Float,
        ["double"] = SdkConstants.CustomApiParameterType.Float,
        ["float"] = SdkConstants.CustomApiParameterType.Float,
        ["Integer"] = SdkConstants.CustomApiParameterType.Integer,
        ["Int16"] = SdkConstants.CustomApiParameterType.Integer,
        ["Int32"] = SdkConstants.CustomApiParameterType.Integer,
        ["Int64"] = SdkConstants.CustomApiParameterType.Integer,
        ["int"] = SdkConstants.CustomApiParameterType.Integer,
        ["long"] = SdkConstants.CustomApiParameterType.Integer,
        ["short"] = SdkConstants.CustomApiParameterType.Integer,
        ["Money"] = SdkConstants.CustomApiParameterType.Money,
        ["OptionSetValue"] = SdkConstants.CustomApiParameterType.Picklist,
        ["Picklist"] = SdkConstants.CustomApiParameterType.Picklist,
        ["Guid"] = SdkConstants.CustomApiParameterType.Guid,
        ["guid"] = SdkConstants.CustomApiParameterType.Guid,
        ["String"] = SdkConstants.CustomApiParameterType.String,
        ["string"] = SdkConstants.CustomApiParameterType.String,
    };

    internal static int ResolveCustomApiTypeCode(string? parameterTypeName, bool isCollection)
{
    string simple = SimpleTypeName((parameterTypeName ?? "System.String").Trim());

    // Only String and Entity element types have a Custom API collection code (StringArray / EntityCollection);
    // any other element type has no collection representation, so it falls through to its scalar code (unchanged).
    if (isCollection)
    {
        if (simple.IndexOf("String", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SdkConstants.CustomApiParameterType.StringArray;
        }

        if (simple.IndexOf("Entity", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SdkConstants.CustomApiParameterType.EntityCollection;
        }
    }

    return CustomApiScalarTypeCodes.TryGetValue(simple, out int code)
        ? code
        : SdkConstants.CustomApiParameterType.String;
}

    // The simple (last dotted segment) of a type name; e.g. "System.Int32" -> "Int32", "Picklist" -> "Picklist".
    private static string SimpleTypeName(string typeName)
{
    int lastDot = typeName.LastIndexOf('.');
    return lastDot >= 0 && lastDot < typeName.Length - 1 ? typeName.Substring(lastDot + 1) : typeName;
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
