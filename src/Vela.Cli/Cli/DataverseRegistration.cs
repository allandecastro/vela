using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
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

/// <summary> Reads live Dataverse registration state (assemblies, types, steps, custom APIs). </summary>
internal static partial class DataverseRegistration
{
    // Stryker disable all : these three methods construct/consume a live Dataverse ServiceClient (network I/O)
    // and cannot be exercised by an offline unit test — mutating them only yields NoCoverage noise. The reader
    // helpers they orchestrate (GetPluginAssembly/GetPluginTypes/GetStepItems/GetCustomApiItems) are covered
    // directly through the IOrganizationService seam.
    internal static RegistrationState BuildDataverseState(string[] sourceAssemblies, SharedOptions options, JsonSerializerOptions jsonOptions)
{
    using ServiceClient client = CreateDataverseClient(options);

    IOrganizationService service = client;
    List<RegistrationItem> items = [];
    List<string> diagnostics = [];

    foreach (string assemblyName in sourceAssemblies)
    {
        Entity? pluginAssembly = GetPluginAssembly(service, assemblyName);
        if (pluginAssembly == null)
        {
            diagnostics.Add($"Plugin assembly '{assemblyName}' not found in Dataverse.");
            continue;
        }

        Dictionary<Guid, string> pluginTypes = GetPluginTypes(service, pluginAssembly.Id);
        if (pluginTypes.Count == 0)
        {
            diagnostics.Add($"No plugin types found for assembly '{assemblyName}'.");
            continue;
        }

        items.AddRange(GetStepItems(service, pluginTypes, jsonOptions));

        // Do NOT demote a custom-API read failure to a diagnostic: treating it as "no custom APIs" would
        // make the diff see every local custom API as added and recreate APIs that already exist. Let it
        // propagate to the top-level handler so the run fails with a proper connection/parse exit code,
        // matching how the step read above surfaces errors.
        items.AddRange(GetCustomApiItems(service, pluginTypes, jsonOptions));
    }

    return new RegistrationState(
        SourceAssemblies: sourceAssemblies,
        SourceAssemblyPayloads: [],
        PackagePayloads: [],
        GeneratedAtUtc: DateTime.UtcNow,
        Items: items.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
        Diagnostics: diagnostics.ToArray());
}

    internal static ServiceClient CreateDataverseClient(SharedOptions options)
{
    if (!string.IsNullOrEmpty(options.OAuthUrl) && options.OAuthToken != null)
    {
        string token = options.OAuthToken;
        string oauthUrl = options.OAuthUrl;
        var client = new ServiceClient(
            new Uri(oauthUrl),
            _ => Task.FromResult(token),
            useUniqueInstance: true);
        if (!client.IsReady)
            throw new DataverseConnectionException($"Dataverse connection failed: {client.LastError}");
        return client;
    }
    return CreateDataverseClientFromConnectionString(options.ConnectionString!);
}

    internal static ServiceClient CreateDataverseClientFromConnectionString(string connectionString)
{
    ServiceClient client = new ServiceClient(connectionString);
    if (!client.IsReady)
    {
        throw new DataverseConnectionException($"Dataverse connection failed: {client.LastError}");
    }

    return client;
}
    // Stryker restore all

    internal static Entity? GetPluginAssembly(IOrganizationService service, string assemblyName)
{
    QueryExpression query = new QueryExpression("pluginassembly")
    {
        ColumnSet = new ColumnSet("pluginassemblyid", "name", "createdon", "modifiedon")
    };
    query.Criteria.AddCondition("name", ConditionOperator.Equal, assemblyName);
    Entity[] candidates = service.RetrieveAll(query).ToArray();
    if (candidates.Length == 0)
    {
        return null;
    }

    if (candidates.Length == 1)
    {
        return candidates[0];
    }

    Entity selected = candidates[0];
    int selectedTypeCount = GetPluginTypes(service, selected.Id).Count;
    DateTime selectedModifiedOn = selected.GetAttributeValue<DateTime?>("modifiedon")
        ?? selected.GetAttributeValue<DateTime?>("createdon")
        ?? DateTime.MinValue;

    foreach (Entity candidate in candidates.Skip(1))
    {
        int candidateTypeCount = GetPluginTypes(service, candidate.Id).Count;
        DateTime candidateModifiedOn = candidate.GetAttributeValue<DateTime?>("modifiedon")
            ?? candidate.GetAttributeValue<DateTime?>("createdon")
            ?? DateTime.MinValue;

        if (candidateTypeCount > selectedTypeCount ||
            (candidateTypeCount == selectedTypeCount && candidateModifiedOn > selectedModifiedOn))
        {
            selected = candidate;
            selectedTypeCount = candidateTypeCount;
            selectedModifiedOn = candidateModifiedOn;
        }
    }

    return selected;
}

    internal static Dictionary<Guid, string> GetPluginTypes(IOrganizationService service, Guid pluginAssemblyId)
{
    QueryExpression query = new QueryExpression("plugintype")
    {
        ColumnSet = new ColumnSet("plugintypeid", "typename")
    };
    query.Criteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, pluginAssemblyId);

    Dictionary<Guid, string> result = new Dictionary<Guid, string>();
    foreach (Entity type in service.RetrieveAll(query))
    {
        string typeName = type.GetAttributeValue<string>("typename") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            result[type.Id] = typeName;
        }
    }

    return result;
}

    internal static List<RegistrationItem> GetStepItems(IOrganizationService service, Dictionary<Guid, string> pluginTypes, JsonSerializerOptions jsonOptions)
{
    List<RegistrationItem> items = [];
    Guid[] pluginTypeIds = pluginTypes.Keys.ToArray();
    if (pluginTypeIds.Length == 0)
    {
        return items;
    }

    QueryExpression stepsQuery = new QueryExpression("sdkmessageprocessingstep")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "eventhandler", "name", "stage", "mode", "rank", "filteringattributes", "statecode", "configuration", "sdkmessageprocessingstepsecureconfigid")
    };
    stepsQuery.Criteria.AddCondition("eventhandler", ConditionOperator.In, pluginTypeIds.Cast<object>().ToArray());

    LinkEntity messageLink = stepsQuery.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.LeftOuter);
    messageLink.EntityAlias = "msg";
    messageLink.Columns = new ColumnSet("name");

    LinkEntity filterLink = stepsQuery.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
    filterLink.EntityAlias = "filter";
    filterLink.Columns = new ColumnSet("primaryobjecttypecode");

    List<Entity> stepRows = service.RetrieveAll(stepsQuery);
    Dictionary<Guid, Entity[]> imagesByStep = GetStepImages(service, stepRows.Select(x => x.Id).ToArray());
    Dictionary<Guid, string> secureConfigsById = GetSecureConfigs(
        service,
        stepRows.Select(x => x.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")?.Id)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray());

    foreach (Entity step in stepRows)
    {
        Guid handlerId = step.GetAttributeValue<EntityReference>("eventhandler")?.Id ?? Guid.Empty;
        if (!pluginTypes.TryGetValue(handlerId, out string? pluginTypeName))
        {
            continue;
        }

        string message = GetAliasedString(step, "msg.name") ?? string.Empty;
        string entityLogicalName = GetAliasedString(step, "filter.primaryobjecttypecode") ?? string.Empty;
        int stage = step.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0;
        int mode = step.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0;

        // Skip internal Custom API handler steps (MainOperation) — managed by the customapi entity
        if (stage == SdkConstants.PipelineStage.MainOperation)
        {
            continue;
        }

        int executionOrder = step.GetAttributeValue<int?>("rank") ?? 1;
        string? stepName = step.GetAttributeValue<string>("name");
        string stepKey = ParseStepKey(stepName, pluginTypeName);
        string[] filteringAttributes = SplitCsv(step.GetAttributeValue<string>("filteringattributes"));
        bool enabled = (step.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0) == 0;
        string unsecureConfig = step.GetAttributeValue<string>("configuration") ?? string.Empty;
        Guid? secureConfigId = step.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")?.Id;
        string secureConfig = secureConfigId.HasValue && secureConfigsById.TryGetValue(secureConfigId.Value, out string? secureValue)
            ? secureValue
            : string.Empty;

        Entity[] images = imagesByStep.TryGetValue(step.Id, out Entity[]? found) ? found : [];
        StepImage[] preImages = images
            .Where(x => (x.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? -1) is 0 or 2)
            .Select(x => new StepImage(x.GetAttributeValue<string>("entityalias") ?? string.Empty, SplitCsv(x.GetAttributeValue<string>("attributes"))))
            .ToArray();
        StepImage[] postImages = images
            .Where(x => (x.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? -1) is 1 or 2)
            .Select(x => new StepImage(x.GetAttributeValue<string>("entityalias") ?? string.Empty, SplitCsv(x.GetAttributeValue<string>("attributes"))))
            .ToArray();

        // Build the diff shape through the shared builder so the live read and the local definition
        // (DefinitionNormalizer.NormalizeStepDefinition) can't diverge. The name is the raw Dataverse
        // value here (the local side computes its expected name); everything else is identical by construction.
        JsonElement shape = DefinitionNormalizer.BuildStepShape(
            pluginTypeName,
            message,
            entityLogicalName,
            stepKey,
            stepName ?? string.Empty,
            stage,
            mode,
            executionOrder,
            filteringAttributes,
            preImages,
            postImages,
            enabled,
            unsecureConfig,
            secureConfig,
            jsonOptions);

        items.Add(new RegistrationItem("step", pluginTypeName, shape));
    }

    return items;
}

    internal static Dictionary<Guid, Entity[]> GetStepImages(IOrganizationService service, Guid[] stepIds)
{
    Dictionary<Guid, List<Entity>> map = new Dictionary<Guid, List<Entity>>();
    if (stepIds.Length == 0)
    {
        return new Dictionary<Guid, Entity[]>();
    }

    QueryExpression imageQuery = new QueryExpression("sdkmessageprocessingstepimage")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "imagetype", "entityalias", "attributes")
    };
    imageQuery.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.In, stepIds.Cast<object>().ToArray());

    foreach (Entity image in service.RetrieveAll(imageQuery))
    {
        Guid stepId = image.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty;
        if (stepId == Guid.Empty)
        {
            continue;
        }

        if (!map.TryGetValue(stepId, out List<Entity>? list))
        {
            list = [];
            map[stepId] = list;
        }

        list.Add(image);
    }

    return map.ToDictionary(x => x.Key, x => x.Value.ToArray());
}

    /// <summary>
    /// Batch-reads the secure configuration values for the supplied secure-config record ids. The
    /// value lives on a separate <c>sdkmessageprocessingstepsecureconfig</c> row linked from the step,
    /// so reading it back is what lets <c>plan</c> detect drift in the declared secure configuration.
    /// </summary>
    internal static Dictionary<Guid, string> GetSecureConfigs(IOrganizationService service, Guid[] secureConfigIds)
{
    Dictionary<Guid, string> map = new Dictionary<Guid, string>();
    if (secureConfigIds.Length == 0)
    {
        return map;
    }

    QueryExpression query = new QueryExpression("sdkmessageprocessingstepsecureconfig")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepsecureconfigid", "secureconfig")
    };
    query.Criteria.AddCondition("sdkmessageprocessingstepsecureconfigid", ConditionOperator.In, secureConfigIds.Cast<object>().ToArray());

    foreach (Entity row in service.RetrieveAll(query))
    {
        map[row.Id] = row.GetAttributeValue<string>("secureconfig") ?? string.Empty;
    }

    return map;
}

    internal static List<RegistrationItem> GetCustomApiItems(IOrganizationService service, Dictionary<Guid, string> pluginTypes, JsonSerializerOptions jsonOptions)
{
    List<RegistrationItem> items = [];
    Guid[] pluginTypeIds = pluginTypes.Keys.ToArray();
    if (pluginTypeIds.Length == 0)
    {
        return items;
    }

    QueryExpression apiQuery = new QueryExpression("customapi")
    {
        ColumnSet = new ColumnSet("customapiid", "uniquename", "isfunction", "boundentitylogicalname", "executeprivilegename", "isprivate", "allowedcustomprocessingsteptype", "bindingtype", "workflowsdkstepenabled", "plugintypeid")
    };
    apiQuery.Criteria.AddCondition("plugintypeid", ConditionOperator.In, pluginTypeIds.Cast<object>().ToArray());
    List<Entity> apiRows = service.RetrieveAll(apiQuery);

    Guid[] apiIds = apiRows.Select(x => x.Id).ToArray();
    Dictionary<Guid, CustomApiParameterApplyDefinition[]> requestParams = GetCustomApiRequestParameters(service, apiIds);
    Dictionary<Guid, CustomApiParameterApplyDefinition[]> responseParams = GetCustomApiResponseParameters(service, apiIds);

    foreach (Entity api in apiRows)
    {
        Guid pluginTypeId = api.GetAttributeValue<EntityReference>("plugintypeid")?.Id ?? Guid.Empty;
        string pluginTypeName = pluginTypes.TryGetValue(pluginTypeId, out string? resolved) ? resolved : string.Empty;

        string uniqueName = api.GetAttributeValue<string>("uniquename") ?? string.Empty;
        bool isFunction = api.GetAttributeValue<bool?>("isfunction") ?? false;
        string? boundEntityLogicalName = api.GetAttributeValue<string>("boundentitylogicalname");
        string? executePrivilegeName = api.GetAttributeValue<string>("executeprivilegename");
        bool isPrivate = api.GetAttributeValue<bool?>("isprivate") ?? false;
        int allowedCustomProcessingStepType = api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 0;
        int bindingType = api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0;
        bool isBound = bindingType != 0;
        bool workflowSdkStepEnabled = api.GetAttributeValue<bool?>("workflowsdkstepenabled") ?? false;

        var inputParameters = requestParams.TryGetValue(api.Id, out var input)
            ? input.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new
            {
                name = x.Name,
                isOptional = x.IsOptional,
                parameterTypeName = x.ParameterTypeName,
                isCollection = x.IsCollection,
                logicalEntityName = x.LogicalEntityName
            }).ToArray()
            : [];
        var outputParameters = responseParams.TryGetValue(api.Id, out var output)
            ? output.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new
            {
                name = x.Name,
                parameterTypeName = x.ParameterTypeName,
                isCollection = x.IsCollection,
                logicalEntityName = x.LogicalEntityName
            }).ToArray()
            : [];

        var shape = new
        {
            pluginTypeName,
            uniqueName,
            isFunction,
            isBound,
            boundEntityLogicalName,
            executePrivilegeName,
            isPrivate,
            allowedCustomProcessingStepType,
            bindingType,
            workflowSdkStepEnabled,
            inputParameters,
            outputParameters
        };

        items.Add(new RegistrationItem("custom-api", uniqueName, JsonSerializer.SerializeToElement(shape, jsonOptions)));
    }

    return items;
}

    internal static Dictionary<Guid, CustomApiParameterApplyDefinition[]> GetCustomApiRequestParameters(IOrganizationService service, Guid[] apiIds)
{
    Dictionary<Guid, List<CustomApiParameterApplyDefinition>> map = new Dictionary<Guid, List<CustomApiParameterApplyDefinition>>();
    if (apiIds.Length == 0)
    {
        return new Dictionary<Guid, CustomApiParameterApplyDefinition[]>();
    }

    QueryExpression query = new QueryExpression("customapirequestparameter")
    {
        ColumnSet = new ColumnSet("customapiid", "name", "isoptional", "type", "logicalentityname")
    };
    query.Criteria.AddCondition("customapiid", ConditionOperator.In, apiIds.Cast<object>().ToArray());

    foreach (Entity row in service.RetrieveAll(query))
    {
        Guid apiId = row.GetAttributeValue<EntityReference>("customapiid")?.Id ?? Guid.Empty;
        if (apiId == Guid.Empty)
        {
            continue;
        }

        string name = row.GetAttributeValue<string>("name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        bool isOptional = row.GetAttributeValue<bool?>("isoptional") ?? false;
        int typeCode = row.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10;
        string? logicalEntityName = row.GetAttributeValue<string>("logicalentityname");
        (string typeName, bool isCollection) = ResolveCustomApiTypeName(typeCode, logicalEntityName);
        if (!map.TryGetValue(apiId, out List<CustomApiParameterApplyDefinition>? list))
        {
            list = [];
            map[apiId] = list;
        }

        list.Add(new CustomApiParameterApplyDefinition(name, isOptional, typeName, isCollection, logicalEntityName));
    }

    return map.ToDictionary(x => x.Key, x => x.Value.ToArray());
}

    internal static Dictionary<Guid, CustomApiParameterApplyDefinition[]> GetCustomApiResponseParameters(IOrganizationService service, Guid[] apiIds)
{
    Dictionary<Guid, List<CustomApiParameterApplyDefinition>> map = new Dictionary<Guid, List<CustomApiParameterApplyDefinition>>();
    if (apiIds.Length == 0)
    {
        return new Dictionary<Guid, CustomApiParameterApplyDefinition[]>();
    }

    QueryExpression query = new QueryExpression("customapiresponseproperty")
    {
        ColumnSet = new ColumnSet("customapiid", "name", "type", "logicalentityname")
    };
    query.Criteria.AddCondition("customapiid", ConditionOperator.In, apiIds.Cast<object>().ToArray());

    foreach (Entity row in service.RetrieveAll(query))
    {
        Guid apiId = row.GetAttributeValue<EntityReference>("customapiid")?.Id ?? Guid.Empty;
        if (apiId == Guid.Empty)
        {
            continue;
        }

        string name = row.GetAttributeValue<string>("name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        int typeCode = row.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10;
        string? logicalEntityName = row.GetAttributeValue<string>("logicalentityname");
        (string typeName, bool isCollection) = ResolveCustomApiTypeName(typeCode, logicalEntityName);
        if (!map.TryGetValue(apiId, out List<CustomApiParameterApplyDefinition>? list))
        {
            list = [];
            map[apiId] = list;
        }

        list.Add(new CustomApiParameterApplyDefinition(name, false, typeName, isCollection, logicalEntityName));
    }

    return map.ToDictionary(x => x.Key, x => x.Value.ToArray());
}

    // Dataverse error code returned when deleting a record that no longer exists (0x80040217).
    private const int ObjectDoesNotExistErrorCode = -2147220969;

    /// <summary>
    /// Deletes a record but treats an already-deleted record as success, so a resumed apply that
    /// re-runs a delete it already completed (or that lost a race) stays idempotent. Detection is by
    /// Dataverse fault error code, not exception message text, so it is locale/SDK-wording independent.
    /// </summary>
    internal static void DeleteIfExists(IOrganizationService service, string entityLogicalName, Guid id)
{
    try
    {
        service.Delete(entityLogicalName, id);
    }
    catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == ObjectDoesNotExistErrorCode)
    {
        // Already gone — nothing to do.
    }
}

}
