using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.ServiceModel;
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

    // A step scoped to a specific entity MUST carry that entity's message filter. If the entity was requested but
    // no sdkmessagefilter resolves (a typo'd/unsupported entity, or a message the entity does not support), silently
    // omitting the filter would register a GLOBAL step that fires for every entity supporting the message - a
    // production hazard. Fail fast instead. (An intentionally global step leaves EntityLogicalName empty.)
    if (!string.IsNullOrWhiteSpace(definition.EntityLogicalName) && !filterId.HasValue)
    {
        throw new InvalidOperationException(MissingMessageFilterMessage(definition));
    }

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
    // Delete the system job once it succeeds: an asynchronous-step setting, always cleared on a synchronous step.
    step["asyncautodelete"] = normalizedMode == 1 && definition.AsyncAutoDelete;
    step["rank"] = NormalizeRegistrationRank(definition.ExecutionOrder);
    step["filteringattributes"] = filteringAttributes;
    step["supporteddeployment"] = new OptionSetValue(definition.SupportedDeployment);
    step["name"] = BuildStepName(definition);
    step["description"] = BuildStepDescription(definition);
    step["configuration"] = definition.UnsecureConfig ?? string.Empty;
    // Impersonation: run the step as a specific systemuser (Dataverse impersonatinguserid), or clear it when absent.
    // The whole entity is rebuilt and re-sent on every create/update, so assigning null explicitly clears the lookup
    // on update when a previously-set impersonation is removed.
    step["impersonatinguserid"] = definition.ImpersonatingUserId == Guid.Empty
        ? null
        : new EntityReference("systemuser", definition.ImpersonatingUserId);
    // Assigned (or cleared) explicitly like impersonatinguserid: a step whose entity is dropped (a global step now)
    // keeps its old filter otherwise, and the next plan shows the same change forever. Clearing it on Update was
    // verified live.
    step["sdkmessagefilterid"] = filterId.HasValue ? new EntityReference("sdkmessagefilter", filterId.Value) : null;

    // Steps default to enabled; a step declared .Disabled() is inactive (statecode 1 / statuscode 2), registered but
    // never fired. The state is written with Update (SetStateRequest is deprecated). Dataverse ignores statecode on
    // Create (verified live: the step reads back enabled, and statuscode 2 is rejected with 0x80048408), so an existing
    // step gets its state in the same Update as its other columns, and a created step that must be disabled gets a
    // second Update right behind its create (batched in the same tier, see ApplyUpsertsBatched).
    Guid stepId = existingId ?? Guid.Empty;
    if (existingId.HasValue)
    {
        step.Id = existingId.Value;
        SetStepState(step, definition.Enabled);
        service.Update(step);
        stepId = existingId.Value;
        LogOperation("step", "update", BuildStepName(definition), verbose: verbose);
    }
    else
    {
        stepId = service.Create(step);
        LogOperation("step", "create", BuildStepName(definition), verbose: verbose);
        if (!definition.Enabled)
        {
            Entity disable = new Entity("sdkmessageprocessingstep") { Id = stepId };
            SetStepState(disable, enabled: false);
            service.Update(disable);
        }
    }

    // definition.SecureConfig is only a digest. The raw value exists only for a secure configuration Vela was given a
    // value for (SecureConfigResolver), keyed by the full step key; every other one belongs to the environment and is
    // left untouched (never written, unlinked or deleted).
    if (secureConfigByStepKey != null
        && secureConfigByStepKey.TryGetValue(DiffEngine.GetSecureConfigKey(new RegistrationItem("step", definition.PluginTypeName, definitionElement)), out string? rawSecureConfig))
    {
        SyncStepSecureConfig(service, stepId, rawSecureConfig);
    }

    SyncStepImages(service, stepId, definition.Message, definition.PreImages, definition.PostImages);
}

    // Enabled: statecode 0 / statuscode 1; disabled: statecode 1 / statuscode 2. Written on every update so the live
    // state converges whatever it was (re-sending the current state is accepted, verified live).
    internal static void SetStepState(Entity step, bool enabled)
{
    step["statecode"] = new OptionSetValue(enabled ? 0 : 1);
    step["statuscode"] = new OptionSetValue(enabled ? 1 : 2);
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

    // Deletes a removed step by the row id the live read gave it (RegistrationItem.LiveId), never by a lookup on its
    // key, trigger or name, which can match another row (several steps can share a trigger, and a name is not unique).
    // The row is re-read first: it is deleted only while its handler is still a plugin type of the assemblies being
    // applied (ourPluginTypeIds), so a step moved to another assembly since the read is left alone. A removed step
    // always comes from the live read, so a missing id is a programming error, not a reason to search.
    internal static void DeleteRemovedStep(IOrganizationService service, RegistrationItem item, IReadOnlyCollection<Guid> ourPluginTypeIds)
{
    Guid stepId = item.LiveId
        ?? throw new InvalidOperationException($"Cannot delete step '{TryGetString(item.Definition, "name") ?? item.Type}': the live read gave it no id.");
    DeleteStep(service, stepId, ourPluginTypeIds);
}

    // Deletes a step and then its secure configuration row: Dataverse does not cascade the delete to
    // sdkmessageprocessingstepsecureconfig, so without this the secret would outlive the step as an orphan.
    private static void DeleteStep(IOrganizationService service, Guid stepId, IReadOnlyCollection<Guid> ourPluginTypeIds)
{
    Entity step;
    try
    {
        step = service.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet("eventhandler", "sdkmessageprocessingstepsecureconfigid"));
    }
    catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == ObjectDoesNotExistErrorCode)
    {
        return; // already gone
    }

    if (step.GetAttributeValue<EntityReference>("eventhandler") is not { } handler || !ourPluginTypeIds.Contains(handler.Id))
    {
        return; // no longer one of ours
    }

    EntityReference? secureConfig = step.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid");
    DeleteIfExists(service, "sdkmessageprocessingstep", stepId);
    if (secureConfig != null)
    {
        DeleteIfExists(service, "sdkmessageprocessingstepsecureconfig", secureConfig.Id);
    }
}

    internal static Guid? FindStepId(IOrganizationService service, Guid pluginTypeId, StepApplyDefinition definition)
{
    QueryExpression query = new QueryExpression("sdkmessageprocessingstep")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "name", "description", "stage", "mode")
    };
    query.Criteria.AddCondition("eventhandler", ConditionOperator.Equal, pluginTypeId);

    LinkEntity messageLink = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.LeftOuter);
    messageLink.EntityAlias = "msg";
    messageLink.Columns = new ColumnSet("name");

    LinkEntity filterLink = query.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
    filterLink.EntityAlias = "filter";
    filterLink.Columns = new ColumnSet("primaryobjecttypecode");

    Guid? triggerMatch = null;
    int triggerMatchCount = 0;
    // Set when the diff paired this step with a live step under another key (DiffEngine.LiveStepKeyField).
    string? liveStepKey = definition.LiveStepKey;

    foreach (Entity row in service.RetrieveAll(query))
    {
        string message = GetAliasedString(row, "msg.name") ?? string.Empty;
        string entity = GetAliasedString(row, "filter.primaryobjecttypecode") ?? string.Empty;
        int stage = row.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0;
        int mode = row.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0;
        string keylessName = DefinitionNormalizer.BuildKeylessStepName(definition.PluginTypeName, stage, mode, message, entity);
        string parsedStepKey = DefinitionParser.ResolveStepKey(
            row.GetAttributeValue<string>("description"), row.GetAttributeValue<string>("name"), definition.PluginTypeName, keylessName);

        // The live step the diff paired this definition with, under its previous key.
        if (!string.IsNullOrEmpty(liveStepKey) && string.Equals(parsedStepKey, liveStepKey, StringComparison.OrdinalIgnoreCase))
        {
            return row.Id;
        }

        // An exact step-key match is authoritative.
        if (!string.IsNullOrWhiteSpace(definition.StepKey) &&
            string.Equals(parsedStepKey, definition.StepKey, StringComparison.OrdinalIgnoreCase))
        {
            return row.Id;
        }

        bool triggerIdentityMatches =
            string.Equals(message, definition.Message, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entity, definition.EntityLogicalName, StringComparison.OrdinalIgnoreCase) &&
            stage == definition.Stage &&
            mode == definition.Mode;

        if (!triggerIdentityMatches)
        {
            continue;
        }

        // A keyless definition matches purely by trigger identity (first match wins, unchanged).
        if (string.IsNullOrWhiteSpace(definition.StepKey))
        {
            return row.Id;
        }

        // A keyed definition paired with a keyless live step (a deployment that predates step keys): adopt that step
        // by its trigger, when it is the only keyless one there.
        if (liveStepKey is { Length: 0 } && parsedStepKey.Length == 0)
        {
            triggerMatch = row.Id;
            triggerMatchCount++;
        }
    }

    // No key match. A keyed definition adopts a live step by trigger only when the diff paired it with a keyless one
    // and that one is unambiguous. Several steps may share a trigger, so a keyed step with no pairing (an added step)
    // never takes over another step's row: it is created (#305).
    return triggerMatchCount == 1 ? triggerMatch : null;
}

    internal static Guid ResolveMessageId(IOrganizationService service, string messageName)
        => FindMessageId(service, messageName) ?? throw new InvalidOperationException(MessageNotFoundMessage(messageName));

    // The sdkmessage id of a message name, or null when the environment has no such message.
    internal static Guid? FindMessageId(IOrganizationService service, string messageName)
{
    QueryExpression query = new QueryExpression("sdkmessage")
    {
        ColumnSet = new ColumnSet("sdkmessageid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("name", ConditionOperator.Equal, messageName);
    return service.RetrieveMultiple(query).Entities.FirstOrDefault()?.Id;
}

    private static string MessageNotFoundMessage(string messageName) => $"Dataverse message not found: {messageName}";

    private static string MissingMessageFilterMessage(StepApplyDefinition definition) =>
        $"Cannot register step '{BuildStepName(definition)}': no message filter exists for entity '{definition.EntityLogicalName}' on message '{definition.Message}'. " +
        "Check the entity logical name is correct and that the entity supports this message. Registering it anyway would create an unfiltered step that fires for every entity.";

    /// <summary>
    /// The read-only check behind the preview of plan and apply: resolves the message, and the message filter of a
    /// step scoped to a table, of every step the diff adds or changes, and returns one problem per step apply could
    /// not register. It runs before any write, so such a step stops the run before a delete or the package publish
    /// rather than after them. The message of a Custom API this run creates does not exist yet (Dataverse creates it
    /// with the Custom API, which apply writes before the steps), so a step on that message is not checked here.
    /// </summary>
    internal static IReadOnlyList<string> FindUnregistrableSteps(IOrganizationService service, RegistrationDiff diff)
    {
        HashSet<string> createdApiMessages = new(
            diff.Added.Where(item => !IsStep(item) && !IsWebResource(item))
                .Select(item => TryGetString(item.Definition, "uniqueName") ?? item.Type),
            StringComparer.OrdinalIgnoreCase);
        IEnumerable<JsonElement> written = diff.Added.Where(IsStep).Select(item => item.Definition)
            .Concat(diff.Changed.Where(change => string.Equals(change.Kind, "step", StringComparison.OrdinalIgnoreCase)).Select(change => change.After));

        Dictionary<string, (int BindingType, string? BoundEntity)> apiBindings = CustomApiBindings(diff);
        Dictionary<string, Guid?> messageIds = new(StringComparer.OrdinalIgnoreCase);
        StepMetadataCache filters = new StepMetadataCache();
        List<string> problems = [];
        foreach (JsonElement element in written)
        {
            StepApplyDefinition definition = ParseStepDefinition(element);
            if (apiBindings.TryGetValue(definition.Message, out (int BindingType, string? BoundEntity) binding)
                && CustomApiStepTableProblem(definition, binding.BindingType, binding.BoundEntity) is string tableProblem)
            {
                problems.Add(tableProblem);
                continue;
            }

            if (createdApiMessages.Contains(definition.Message))
            {
                continue;
            }

            if (!messageIds.TryGetValue(definition.Message, out Guid? messageId))
            {
                messageId = FindMessageId(service, definition.Message);
                messageIds[definition.Message] = messageId;
            }

            if (messageId is not Guid id)
            {
                problems.Add($"Cannot register step '{BuildStepName(definition)}': {MessageNotFoundMessage(definition.Message)}.");
            }
            else if (!string.IsNullOrWhiteSpace(definition.EntityLogicalName)
                && filters.ResolveMessageFilterId(service, id, definition.EntityLogicalName) is null)
            {
                problems.Add(MissingMessageFilterMessage(definition));
            }
        }

        return problems;
    }

    // The binding of each Custom API Vela deploys or manages, by unique name: what this run deploys, else what is live.
    private static Dictionary<string, (int BindingType, string? BoundEntity)> CustomApiBindings(RegistrationDiff diff)
    {
        Dictionary<string, (int, string?)> bindings = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegistrationItem item in diff.LocalItems.Concat(diff.CurrentItems).Where(item => !IsStep(item) && !IsWebResource(item)))
        {
            if (TryGetString(item.Definition, "uniqueName") is string uniqueName && !bindings.ContainsKey(uniqueName))
            {
                CustomApiApplyDefinition api = ParseCustomApiDefinition(item.Definition);
                bindings[uniqueName] = (api.BindingType, api.BoundEntityLogicalName);
            }
        }

        return bindings;
    }

    /// <summary>
    /// Why a step cannot be registered on a Custom API's message with its table, or null when it can. Dataverse creates
    /// exactly one message filter per Custom API (verified live): with no table (primary entity "none") for a global API,
    /// the bound table for an entity- or collection-bound one. A step with another table has no filter to register on
    /// (one created with another message's filter is accepted but never fires). A step with no table registers with no
    /// filter, and fires (verified live).
    /// </summary>
    internal static string? CustomApiStepTableProblem(StepApplyDefinition definition, int bindingType, string? boundEntity)
    {
        string table = definition.EntityLogicalName;
        if (string.IsNullOrWhiteSpace(table))
        {
            return null;
        }

        if (bindingType == 0 || string.IsNullOrWhiteSpace(boundEntity))
        {
            return $"Cannot register step '{BuildStepName(definition)}': Custom API '{definition.Message}' is global, so its only message filter " +
                $"has no table and a step on it cannot be scoped to '{table}'. Declare the step from a request marked " +
                "[Vela.Messages.GlobalCustomApi] (vela models --custom-apis emits it); the step then registers with no table.";
        }

        return string.Equals(table, boundEntity, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Cannot register step '{BuildStepName(definition)}': Custom API '{definition.Message}' is bound to '{boundEntity}', its only " +
              $"message filter, so a step on it cannot be scoped to '{table}'.";
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
    desired.AddRange(preImages.Select(x => new StepImageApplyDefinition(x.Alias, x.Attributes, 0, x.MessagePropertyName)));
    desired.AddRange(postImages.Select(x => new StepImageApplyDefinition(x.Alias, x.Attributes, 1, x.MessagePropertyName)));

    // The image's messagepropertyname depends on the message (Create → Id, CreateMultiple → Ids, UpdateMultiple →
    // Targets, Send → EmailId, ...) unless the step chose one (SubordinateId on Merge); StepImageRules holds the map
    // shared with the runtime and the scanner. A message that supports no image (RouteTo) never gets here with images:
    // the scanner rejects it before any write.
    if (desired.Count > 0 && Vela.Steps.StepImageRules.GetMessagePropertyName(message) is null)
    {
        throw new InvalidOperationException($"Dataverse supports no image on the '{message}' message.");
    }

    QueryExpression query = new QueryExpression("sdkmessageprocessingstepimage")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid", "imagetype", "entityalias", "attributes", "messagepropertyname", "name")
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

        string messagePropertyName = Vela.Steps.StepImageRules.ResolveMessagePropertyName(message, desiredImage.MessagePropertyName)!;

        if (existingByKey.TryGetValue(key, out Entity? existing))
        {
            // The match key is (alias, imagetype), so those already agree; `attributes` and `messagepropertyname`
            // (which record the image is taken from, e.g. Target or SubordinateId on Merge) can differ. Skip entirely
            // when nothing changed.
            string existingAttributes = string.Join(",", (existing.GetAttributeValue<string>("attributes") ?? string.Empty)
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.Ordinal));
            if (string.Equals(existingAttributes, attributesCsv, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.GetAttributeValue<string>("messagepropertyname"), messagePropertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The platform's sdkmessageprocessingstepimage Update path throws a NullReferenceException
            // (0x80040216) even for an attributes-only change (it dereferences fields the update payload does
            // not carry), so we cannot Update the image in place. Delete the stale image and recreate it from
            // scratch (the create path below is the same one that produced it initially, and is reliable).
            service.Delete("sdkmessageprocessingstepimage", existing.Id);
        }

        Entity create = new Entity("sdkmessageprocessingstepimage");
        create["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId);
        create["entityalias"] = desiredImage.Alias;
        create["name"] = desiredImage.Alias;
        create["attributes"] = attributesCsv;
        create["imagetype"] = new OptionSetValue(desiredImage.ImageType);
        create["messagepropertyname"] = messagePropertyName;
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
    if (!string.IsNullOrWhiteSpace(definition.StepKey))
    {
        string lifecycle = BuildStepLifecycleLabel(definition.Stage, definition.Mode);
        return $"{definition.PluginTypeName} - {lifecycle}: {definition.StepKey}";
    }

    // Keyless: share the exact format with the read side so ResolveStepKey recognises it as keyless (#305).
    return DefinitionNormalizer.BuildKeylessStepName(definition.PluginTypeName, definition.Stage, definition.Mode, definition.Message, definition.EntityLogicalName);
}

    // Dataverse's limit on sdkmessageprocessingstep.description (shared with the VELA028 analyzer).
    internal const int StepDescriptionMaxLength = Vela.Constants.DataverseLabelLimits.StepDescription;

    // The step description: the developer's text (WithDescription), or the step name when none is declared, followed
    // by a stable [vela:stepkey=...] marker on its own line (B4). The marker lets a later read recover the step key
    // even if the display name is changed, so the step's identity survives a rename; ExtractStepKeyFromMarker reads
    // only a marker on the final line, so a marker quoted in the developer's text is never taken for a key.
    internal static string BuildStepDescription(StepApplyDefinition definition)
{
    string text = string.IsNullOrWhiteSpace(definition.Description) ? BuildStepName(definition) : definition.Description.Trim();
    return string.IsNullOrWhiteSpace(definition.StepKey)
        ? text
        : $"{text}\n{DefinitionParser.StepKeyMarker(definition.StepKey)}";
}

    /// <summary>
    /// The error for a step whose description (with its step-key marker) exceeds what Dataverse stores, or null.
    /// Checked at scan time so plan fails before any write instead of apply failing mid-run.
    /// </summary>
    internal static string? GetStepDescriptionLengthError(StepApplyDefinition definition)
{
    int length = BuildStepDescription(definition).Length;
    if (length <= StepDescriptionMaxLength)
    {
        return null;
    }

    int markerLength = string.IsNullOrWhiteSpace(definition.StepKey) ? 0 : Vela.Constants.DataverseLabelLimits.StepKeyMarkerLength(definition.StepKey.Length);
    return $"step '{BuildStepName(definition)}' has a description of {length - markerLength} characters, but Dataverse stores at most " +
        $"{StepDescriptionMaxLength - markerLength} once Vela appends its {markerLength}-character step-key marker. Shorten WithDescription(...).";
}

}
