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

/// <summary> Step (sdkmessageprocessingstep) upsert/delete, images, message/filter resolution. </summary>
internal static partial class DataverseRegistration
{
    internal static void UpsertStep(IOrganizationService service, Dictionary<string, Guid> pluginTypeIds, JsonElement definitionElement, bool verbose = true, StepMetadataCache? metadataCache = null, IReadOnlyDictionary<string, string>? secureConfigByStepKey = null)
{
    StepApplyDefinition definition = ParseStepDefinition(definitionElement);
    if (!pluginTypeIds.TryGetValue(definition.PluginTypeName, out Guid pluginTypeId))
    {
        throw new InvalidOperationException($"Plugin type not found in Dataverse: {definition.PluginTypeName}");
    }

    Guid? existingId = FindStepId(service, pluginTypeId, definition);
    // sdkmessage/sdkmessagefilter rows are immutable for the lifetime of an apply run, so resolve
    // them through the per-run cache when one is supplied to avoid an N+1 query per step.
    Guid messageId = metadataCache != null
        ? metadataCache.ResolveMessageId(service, definition.Message)
        : ResolveMessageId(service, definition.Message);
    Guid? filterId = metadataCache != null
        ? metadataCache.ResolveMessageFilterId(service, messageId, definition.EntityLogicalName)
        : ResolveMessageFilterId(service, messageId, definition.EntityLogicalName);
    int normalizedMode = definition.Mode;
    int normalizedStage = NormalizeRegistrationStage(definition.Stage, normalizedMode);
    string filteringAttributes = definition.FilteringAttributes.Length == 0
        ? string.Empty
        : string.Join(",", definition.FilteringAttributes.OrderBy(x => x, StringComparer.Ordinal));

    Entity step = new Entity("sdkmessageprocessingstep");
    step["eventhandler"] = new EntityReference("plugintype", pluginTypeId);
    step["sdkmessageid"] = new EntityReference("sdkmessage", messageId);
    step["stage"] = new OptionSetValue(normalizedStage);
    step["mode"] = new OptionSetValue(normalizedMode);
    step["rank"] = NormalizeRegistrationRank(definition.ExecutionOrder);
    step["filteringattributes"] = filteringAttributes;
    step["supporteddeployment"] = new OptionSetValue(0);
    step["name"] = BuildStepName(definition);
    step["description"] = BuildStepName(definition);
    step["configuration"] = definition.UnsecureConfig ?? string.Empty;
    if (filterId.HasValue)
    {
        step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);
    }

    Guid stepId = existingId ?? Guid.Empty;
    if (existingId.HasValue)
    {
        step.Id = existingId.Value;
        service.Update(step);
        stepId = existingId.Value;
        LogOperation("step", "update", BuildStepName(definition));
    }
    else
    {
        stepId = service.Create(step);
        LogOperation("step", "create", BuildStepName(definition));
    }

    // definition.SecureConfig is the redacted hash (Items never carry the plaintext); write the RAW value from the
    // scan-time side channel so the real secret reaches Dataverse. Falls back to the definition value when no map
    // is supplied (e.g. a direct unit test that passes a plaintext definition), or empty when there is no secret.
    string secureConfig = secureConfigByStepKey != null && secureConfigByStepKey.TryGetValue(definition.StepKey, out string? rawSecureConfig)
        ? rawSecureConfig
        : definition.SecureConfig ?? string.Empty;

    // A2: if the plaintext secret did not round-trip (no stable step key to carry it through the scan-time side
    // channel), the only value left is the redacted digest. Writing that to Dataverse would hand the plugin the
    // literal hash and make every later plan report the step as "changed". Fail clearly instead of corrupting it.
    if (!string.IsNullOrEmpty(secureConfig) && secureConfig.StartsWith(DefinitionNormalizer.SecretDigestPrefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Cannot register the secure configuration for step '{BuildStepName(definition)}': only its redacted digest is available at apply time. This happens when the step has no stable step key to carry the plaintext secret; give the step a step key so the secret can round-trip.");
    }

    SyncStepSecureConfig(service, stepId, secureConfig);
    SyncStepState(service, stepId, definition.Enabled);
    SyncStepImages(service, stepId, definition.Message, definition.PreImages, definition.PostImages);
}

    /// <summary>
    /// Reconciles the step's active state. Steps default to enabled; a step declared <c>.Disabled()</c>
    /// is transitioned to the inactive state (statecode 1 / statuscode 2) so it stays registered but
    /// does not fire. Idempotent — applied on every upsert to converge whatever the live state was.
    /// </summary>
    internal static void SyncStepState(IOrganizationService service, Guid stepId, bool enabled)
{
    int desiredState = enabled ? 0 : 1;
    int desiredStatus = enabled ? 1 : 2;
    service.Execute(new SetStateRequest
    {
        EntityMoniker = new EntityReference("sdkmessageprocessingstep", stepId),
        State = new OptionSetValue(desiredState),
        Status = new OptionSetValue(desiredStatus)
    });
}

    /// <summary>
    /// Reconciles the step's secure configuration, stored on a separate
    /// <c>sdkmessageprocessingstepsecureconfig</c> row linked from the step. A non-empty value is
    /// created or updated and linked; an empty value clears the link and deletes the orphaned row.
    /// </summary>
    internal static void SyncStepSecureConfig(IOrganizationService service, Guid stepId, string secureConfig)
{
    Entity step = service.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet("sdkmessageprocessingstepsecureconfigid"));
    EntityReference? existing = step.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid");
    bool hasValue = !string.IsNullOrEmpty(secureConfig);

    if (hasValue)
    {
        if (existing != null)
        {
            Entity update = new Entity("sdkmessageprocessingstepsecureconfig") { Id = existing.Id };
            update["secureconfig"] = secureConfig;
            service.Update(update);
            return;
        }

        Entity config = new Entity("sdkmessageprocessingstepsecureconfig");
        config["secureconfig"] = secureConfig;
        Guid configId = service.Create(config);

        Entity link = new Entity("sdkmessageprocessingstep") { Id = stepId };
        link["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", configId);
        service.Update(link);
        return;
    }

    if (existing != null)
    {
        Entity link = new Entity("sdkmessageprocessingstep") { Id = stepId };
        link["sdkmessageprocessingstepsecureconfigid"] = null;
        service.Update(link);
        DeleteIfExists(service, "sdkmessageprocessingstepsecureconfig", existing.Id);
    }
}

    internal static void DeleteStepByDefinition(IOrganizationService service, JsonElement definitionElement)
{
    // Try to find the step by its stored name first (works even when plugin type was renamed)
    string? stepName = TryGetString(definitionElement, "name");
    if (!string.IsNullOrWhiteSpace(stepName))
    {
        Guid? byName = FindStepByName(service, stepName);
        if (byName.HasValue)
        {
            DeleteIfExists(service, "sdkmessageprocessingstep", byName.Value);
            return;
        }
    }

    // Fallback: look up by plugin type + step key via current assemblies in Dataverse
    StepApplyDefinition definition = ParseStepDefinition(definitionElement);
    Dictionary<string, Guid> allTypes = GetAllPluginTypeIdsByName(service);
    if (!allTypes.TryGetValue(definition.PluginTypeName, out Guid pluginTypeId))
    {
        return;
    }

    Guid? stepId = FindStepId(service, pluginTypeId, definition);
    if (stepId.HasValue)
    {
        DeleteIfExists(service, "sdkmessageprocessingstep", stepId.Value);
    }
}

    internal static Guid? FindStepByName(IOrganizationService service, string stepName)
{
    QueryExpression query = new QueryExpression("sdkmessageprocessingstep")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("name", ConditionOperator.Equal, stepName);
    return service.RetrieveMultiple(query).Entities.FirstOrDefault()?.Id;
}

    internal static Dictionary<string, Guid> GetAllPluginTypeIdsByName(IOrganizationService service)
{
    QueryExpression query = new QueryExpression("plugintype")
    {
        ColumnSet = new ColumnSet("plugintypeid", "typename")
    };

    Dictionary<string, Guid> result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    foreach (Entity type in service.RetrieveAll(query))
    {
        string typeName = type.GetAttributeValue<string>("typename") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typeName) && !result.ContainsKey(typeName))
        {
            result[typeName] = type.Id;
        }
    }

    return result;
}

    internal static Guid? FindStepId(IOrganizationService service, Guid pluginTypeId, StepApplyDefinition definition)
{
    QueryExpression query = new QueryExpression("sdkmessageprocessingstep")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "name", "stage", "mode")
    };
    query.Criteria.AddCondition("eventhandler", ConditionOperator.Equal, pluginTypeId);

    LinkEntity messageLink = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.LeftOuter);
    messageLink.EntityAlias = "msg";
    messageLink.Columns = new ColumnSet("name");

    LinkEntity filterLink = query.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
    filterLink.EntityAlias = "filter";
    filterLink.Columns = new ColumnSet("primaryobjecttypecode");

    foreach (Entity row in service.RetrieveAll(query))
    {
        string message = GetAliasedString(row, "msg.name") ?? string.Empty;
        string entity = GetAliasedString(row, "filter.primaryobjecttypecode") ?? string.Empty;
        int stage = row.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0;
        int mode = row.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0;
        string parsedStepKey = ParseStepKey(row.GetAttributeValue<string>("name"), definition.PluginTypeName);

        if (!string.IsNullOrWhiteSpace(definition.StepKey) &&
            string.Equals(parsedStepKey, definition.StepKey, StringComparison.OrdinalIgnoreCase))
        {
            return row.Id;
        }

        if (string.IsNullOrWhiteSpace(definition.StepKey) &&
            string.Equals(message, definition.Message, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entity, definition.EntityLogicalName, StringComparison.OrdinalIgnoreCase) &&
            stage == definition.Stage &&
            mode == definition.Mode)
        {
            return row.Id;
        }
    }

    return null;
}

    internal static Guid ResolveMessageId(IOrganizationService service, string messageName)
{
    QueryExpression query = new QueryExpression("sdkmessage")
    {
        ColumnSet = new ColumnSet("sdkmessageid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("name", ConditionOperator.Equal, messageName);
    Entity? row = service.RetrieveMultiple(query).Entities.FirstOrDefault();
    if (row == null)
    {
        throw new InvalidOperationException($"Dataverse message not found: {messageName}");
    }

    return row.Id;
}

    internal static Guid? ResolveMessageFilterId(IOrganizationService service, Guid messageId, string entityLogicalName)
{
    if (string.IsNullOrWhiteSpace(entityLogicalName))
    {
        return null;
    }

    QueryExpression query = new QueryExpression("sdkmessagefilter")
    {
        ColumnSet = new ColumnSet("sdkmessagefilterid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, messageId);
    query.Criteria.AddCondition("primaryobjecttypecode", ConditionOperator.Equal, entityLogicalName);
    Entity? row = service.RetrieveMultiple(query).Entities.FirstOrDefault();
    return row?.Id;
}

    /// <summary>
    /// Per-apply-run cache for stable SDK metadata lookups (message ids and message-filter ids).
    /// These rows do not change during a run, so caching them collapses an O(steps) sequence of
    /// queries into O(distinct messages/filters). Not thread-safe: apply runs single-threaded.
    /// </summary>
    internal sealed class StepMetadataCache
{
    private readonly Dictionary<string, Guid> _messageIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid?> _filterIds = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

    public Guid ResolveMessageId(IOrganizationService service, string messageName)
    {
        if (_messageIds.TryGetValue(messageName, out Guid cached))
        {
            return cached;
        }

        Guid resolved = DataverseRegistration.ResolveMessageId(service, messageName);
        _messageIds[messageName] = resolved;
        return resolved;
    }

    public Guid? ResolveMessageFilterId(IOrganizationService service, Guid messageId, string entityLogicalName)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
        {
            return null;
        }

        string key = messageId.ToString("N") + "|" + entityLogicalName;
        if (_filterIds.TryGetValue(key, out Guid? cached))
        {
            return cached;
        }

        Guid? resolved = DataverseRegistration.ResolveMessageFilterId(service, messageId, entityLogicalName);
        _filterIds[key] = resolved;
        return resolved;
    }
}

    internal static void SyncStepImages(IOrganizationService service, Guid stepId, string message, ImageApplyDefinition[] preImages, ImageApplyDefinition[] postImages)
{
    List<StepImageApplyDefinition> desired = [];
    desired.AddRange(preImages.Select(x => new StepImageApplyDefinition(x.Alias, x.Attributes, 0)));
    desired.AddRange(postImages.Select(x => new StepImageApplyDefinition(x.Alias, x.Attributes, 1)));

    // Dataverse requires different messagepropertyname depending on the message:
    // Create PostImage → "id" (captures the new record ID); all others → "Target".
    // Bulk messages (CreateMultiple/UpdateMultiple) never reach here: the platform supports no images on them,
    // so the scanner rejects a bulk step carrying images before apply (see PackageScanner.IsBulkStepWithImages).
    bool isCreateMessage = string.Equals(message, "Create", StringComparison.OrdinalIgnoreCase);

    QueryExpression query = new QueryExpression("sdkmessageprocessingstepimage")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid", "imagetype", "entityalias", "attributes", "name")
    };
    query.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId);
    List<Entity> existingRows = service.RetrieveAll(query);
    Dictionary<string, Entity> existingByKey = existingRows.ToDictionary(
        x => BuildImageIdentityKey(x.GetAttributeValue<string>("entityalias") ?? string.Empty, x.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? -1),
        StringComparer.OrdinalIgnoreCase);

    HashSet<string> desiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (StepImageApplyDefinition desiredImage in desired)
    {
        string key = BuildImageIdentityKey(desiredImage.Alias, desiredImage.ImageType);
        desiredKeys.Add(key);
        string attributesCsv = desiredImage.Attributes.Length == 0
            ? string.Empty
            : string.Join(",", desiredImage.Attributes.OrderBy(x => x, StringComparer.Ordinal));
        string msgPropertyName = isCreateMessage && desiredImage.ImageType == 1 ? "id" : "Target";

        if (existingByKey.TryGetValue(key, out Entity? existing))
        {
            // The match key is (alias, imagetype), so those already agree, and messagepropertyname is derived from
            // the (unchanged) message + imagetype. Only `attributes` can differ, so update just that. Crucially, do
            // NOT re-set imagetype/messagepropertyname on an existing image: the platform throws a
            // NullReferenceException (0x80040216) on such an image update, which broke every step update that had an
            // image. Skip the update entirely when nothing changed.
            string existingAttributes = string.Join(",", (existing.GetAttributeValue<string>("attributes") ?? string.Empty)
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.Ordinal));
            if (string.Equals(existingAttributes, attributesCsv, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Entity update = new Entity("sdkmessageprocessingstepimage") { Id = existing.Id };
            update["attributes"] = attributesCsv;
            service.Update(update);
            continue;
        }

        Entity create = new Entity("sdkmessageprocessingstepimage");
        create["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId);
        create["entityalias"] = desiredImage.Alias;
        create["name"] = desiredImage.Alias;
        create["attributes"] = attributesCsv;
        create["imagetype"] = new OptionSetValue(desiredImage.ImageType);
        create["messagepropertyname"] = msgPropertyName;
        service.Create(create);
    }

    foreach (Entity existing in existingRows)
    {
        string key = BuildImageIdentityKey(existing.GetAttributeValue<string>("entityalias") ?? string.Empty, existing.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? -1);
        if (!desiredKeys.Contains(key))
        {
            service.Delete("sdkmessageprocessingstepimage", existing.Id);
        }
    }
}

    internal static string BuildImageIdentityKey(string alias, int imageType)
{
    return $"{imageType}:{alias}";
}

    internal static int NormalizeRegistrationStage(int stage)
{
    return stage is SdkConstants.PipelineStage.PreValidation
        or SdkConstants.PipelineStage.PreOperation
        or SdkConstants.PipelineStage.MainOperation
        or SdkConstants.PipelineStage.PostOperation
        ? stage
        // An unrecognized stage defaults to PostOperation (runs after commit, can't break the transaction).
        : SdkConstants.PipelineStage.PostOperation;
}

    /// <summary>
    /// Stage Dataverse will actually persist for a (stage, mode) pair: unknown stages collapse to the
    /// registration default and asynchronous steps (mode 1) are forced to PostOperation. Applied on BOTH
    /// the write path (<see cref="UpsertStep"/>) and the local diff shape
    /// (<see cref="DefinitionNormalizer.NormalizeStepDefinition"/>) so plan/apply stay idempotent — the
    /// local and live step shapes must match field-for-field.
    /// </summary>
    internal static int NormalizeRegistrationStage(int stage, int mode)
{
    int normalized = NormalizeRegistrationStage(stage);
    // Async steps must run post-commit: Dataverse persists them at PostOperation.
    if (mode == 1 && normalized != SdkConstants.PipelineStage.PostOperation)
    {
        normalized = SdkConstants.PipelineStage.PostOperation;
    }

    return normalized;
}

    /// <summary>
    /// Rank Dataverse will persist for a declared execution order: clamped to a positive value (default 1).
    /// Shared by the write path and the local diff shape to preserve plan/apply idempotence.
    /// </summary>
    internal static int NormalizeRegistrationRank(int executionOrder)
{
    return executionOrder <= 0 ? 1 : executionOrder;
}


    internal static string BuildStepName(StepApplyDefinition definition)
{
    string lifecycle = BuildStepLifecycleLabel(definition.Stage, definition.Mode);
    if (!string.IsNullOrWhiteSpace(definition.StepKey))
    {
        return $"{definition.PluginTypeName} - {lifecycle}: {definition.StepKey}";
    }

    string entity = string.IsNullOrWhiteSpace(definition.EntityLogicalName) ? string.Empty : $" of {definition.EntityLogicalName}";
    return $"{definition.PluginTypeName} - {lifecycle}: {definition.Message}{entity}";
}

}
