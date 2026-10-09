using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Pure diff engine: compares a local registration state against a current (live Dataverse) state
/// and reports added/removed/changed steps and custom APIs. Has no Dataverse or filesystem dependency.
/// </summary>
internal static class DiffEngine
{
    public static bool IsStep(RegistrationItem item)
    {
        return string.Equals(item.Kind, "step", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWebResource(RegistrationItem item)
    {
        return string.Equals(item.Kind, "web-resource", StringComparison.OrdinalIgnoreCase);
    }

    // The top-level folder of a web resource name (the segment before the first '/'), e.g. "adc_/js/a.js" -> "adc_".
    // Deletion reconciliation is scoped to these so a subset folder never prunes unrelated top-level folders.
    private static string TopFolder(string name)
    {
        int slash = name.IndexOf('/');
        return slash < 0 ? name : name.Substring(0, slash);
    }

    /// <summary>
    /// Set on a changed step's <c>After</c> definition when it was paired with its live step by legacy identity
    /// rather than by step key (its key changed, or the live step predates keys): the live step's key, so apply
    /// updates exactly that step. Several steps may share a trigger, so the trigger alone cannot designate it.
    /// </summary>
    internal const string LiveStepKeyField = "liveStepKey";

    public static string GetIdentityKey(RegistrationItem item)
    {
        return $"{item.Kind}:{GetIdentity(item)}";
    }

    // Builds an identity -> item map, failing with a clear, actionable message on a duplicate identity instead of
    // the opaque "An item with the same key has already been added" that ToDictionary throws (A5). Two entries with
    // the same identity (e.g. two plugin classes declaring the same Custom API uniqueName, or two local files
    // mapping to the same web-resource name) cannot be told apart, so we stop rather than silently drop one.
    private static Dictionary<string, RegistrationItem> BuildIdentityMap(IEnumerable<RegistrationItem> items, string sideLabel)
    {
        Dictionary<string, RegistrationItem> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegistrationItem item in items)
        {
            string key = GetIdentityKey(item);
            if (!map.TryAdd(key, item))
            {
                throw new InvalidOperationException(
                    $"Duplicate {sideLabel} identity '{key}'. Two entries resolve to the same identity; make their unique names / names distinct.");
            }
        }

        return map;
    }

    public static string GetIdentity(RegistrationItem item)
    {
        if (string.Equals(item.Kind, "custom-api", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetString(item.Definition, "uniqueName") ?? item.Type;
        }

        if (IsWebResource(item))
        {
            return TryGetString(item.Definition, "name") ?? item.Type;
        }

        string? stepKey = TryGetString(item.Definition, "stepKey");
        if (!string.IsNullOrWhiteSpace(stepKey))
        {
            string pluginTypeFromKey = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
            return $"{pluginTypeFromKey}:stepkey:{stepKey}";
        }

        string pluginTypeName = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
        string message = TryGetString(item.Definition, "message") ?? string.Empty;
        int stage = TryGetInt(item.Definition, "stage");
        int mode = TryGetInt(item.Definition, "mode");
        string entity = TryGetString(item.Definition, "entityLogicalName") ?? string.Empty;
        return $"{pluginTypeName}:{message}:{stage}:{mode}:{entity}";
    }

    public static string GetStepKey(RegistrationItem item)
    {
        if (!IsStep(item))
        {
            return string.Empty;
        }

        string stepKey = TryGetString(item.Definition, "stepKey") ?? string.Empty;
        string pluginType = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
        return string.IsNullOrWhiteSpace(stepKey) ? string.Empty : $"{pluginType}:stepkey:{stepKey}";
    }

    // The key a step's secure configuration is tracked by between the scan, the resolver and apply: the full step key,
    // or the legacy identity for a keyless step. Always qualified by the plugin type, so two plugins never share it.
    internal static string GetSecureConfigKey(RegistrationItem item) =>
        GetStepKey(item) is { Length: > 0 } key ? key : GetLegacyStepIdentity(item);

    public static string GetLegacyStepIdentity(RegistrationItem item)
    {
        string pluginTypeName = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
        string message = TryGetString(item.Definition, "message") ?? string.Empty;
        int stage = TryGetInt(item.Definition, "stage");
        int mode = TryGetInt(item.Definition, "mode");
        string entity = TryGetString(item.Definition, "entityLogicalName") ?? string.Empty;
        return $"{pluginTypeName}:{message}:{stage}:{mode}:{entity}";
    }

    // Top-level string fields whose CASING must not, on its own, mark an item as "changed": Dataverse compares a
    // step message, a Custom API unique name and the derived display name case-insensitively, and apply still writes
    // the authored casing. Folding only these top-level fields keeps case-significant config (e.g. unsecureConfig)
    // and nested parameter unique names (the dotted-name self-heal) comparing exactly. (#305)
    private static readonly string[] CaseFoldedFields = ["message", "name", "uniqueName"];

    private static readonly (string Label, string Source)[] DerivedLabelFields =
        [("description", "name"), ("displayName", "uniqueName"), ("description", "uniqueName")];

    // A comparison-only serialization of a normalized definition: identical to the stored shape except the
    // case-folded fields are lower-cased, so a case-only drift compares equal. The stored Definition is untouched,
    // so apply still writes the authored casing.
    private static string ComparisonKey(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.Serialize(definition);
        }

        if (JsonNode.Parse(definition.GetRawText()) is not JsonObject obj)
        {
            return JsonSerializer.Serialize(definition);
        }

        // A label that defaults to a case-folded field (a step description to the step name, a Custom API display name
        // and description to the unique name) inherits that field's casing, so fold it too while it is still the
        // default. A label the developer wrote keeps comparing exactly.
        foreach ((string label, string source) in DerivedLabelFields)
        {
            if (obj[label] is JsonValue labelValue && labelValue.TryGetValue(out string? labelText)
                && obj[source] is JsonValue sourceValue && sourceValue.TryGetValue(out string? sourceText)
                && string.Equals(labelText, sourceText, StringComparison.OrdinalIgnoreCase))
            {
                obj[label] = labelText!.ToLowerInvariant();
            }
        }

        foreach (string field in CaseFoldedFields)
        {
            if (obj.TryGetPropertyValue(field, out JsonNode? value)
                && value is JsonValue jsonValue
                && jsonValue.TryGetValue(out string? text)
                && text != null)
            {
                obj[field] = text.ToLowerInvariant();
            }
        }

        return obj.ToJsonString();
    }

    /// <summary>
    /// Pairs each local step with its live counterpart, one-to-one: first by step key (case-insensitive), then, for a
    /// local step whose key has no live match, by legacy identity (plugin type, message, stage, mode, entity) in order.
    /// Returns, for each local step, the index of its live step, or -1 when it has none. Shared by the diff and by
    /// SecureConfigResolver, so both see the same pairs.
    /// </summary>
    internal static int[] PairSteps(IReadOnlyList<RegistrationItem> localSteps, IReadOnlyList<RegistrationItem> currentSteps)
    {
        int[] pairedCurrent = Enumerable.Repeat(-1, localSteps.Count).ToArray();
        bool[] currentStepMatched = new bool[currentSteps.Count];

        // A6: two local steps that share a (non-empty) step key are indistinguishable - the matching below would
        // pair the first and then silently drop the second (the legacy pass skips any key already present). Fail
        // with a clear message instead of quietly losing a step.
        HashSet<string> seenLocalStepKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegistrationItem step in localSteps)
        {
            string key = GetStepKey(step);
            if (!string.IsNullOrWhiteSpace(key) && !seenLocalStepKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Duplicate local step key '{key}'. Two registered steps resolve to the same step key; give each a distinct trigger (message + stage + mode + entity + filtering) or step key.");
            }
        }

        Dictionary<string, int> currentByStepKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < currentSteps.Count; i++)
        {
            string stepKey = GetStepKey(currentSteps[i]);
            if (string.IsNullOrWhiteSpace(stepKey))
            {
                continue;
            }

            if (!currentByStepKey.ContainsKey(stepKey))
            {
                currentByStepKey[stepKey] = i;
            }
        }

        for (int i = 0; i < localSteps.Count; i++)
        {
            RegistrationItem localStep = localSteps[i];
            string localStepKey = GetStepKey(localStep);
            if (string.IsNullOrWhiteSpace(localStepKey))
            {
                continue;
            }

            if (!currentByStepKey.TryGetValue(localStepKey, out int currentIndex) || currentStepMatched[currentIndex])
            {
                continue;
            }

            currentStepMatched[currentIndex] = true;
            pairedCurrent[i] = currentIndex;
        }

        Dictionary<string, Queue<int>> currentByLegacyIdentity = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < currentSteps.Count; i++)
        {
            if (currentStepMatched[i])
            {
                continue;
            }

            string legacyIdentity = GetLegacyStepIdentity(currentSteps[i]);
            if (!currentByLegacyIdentity.TryGetValue(legacyIdentity, out Queue<int>? queue))
            {
                queue = new Queue<int>();
                currentByLegacyIdentity[legacyIdentity] = queue;
            }

            queue.Enqueue(i);
        }

        for (int i = 0; i < localSteps.Count; i++)
        {
            RegistrationItem localStep = localSteps[i];
            string localStepKey = GetStepKey(localStep);
            if (!string.IsNullOrWhiteSpace(localStepKey) && currentByStepKey.ContainsKey(localStepKey))
            {
                continue;
            }

            string legacyIdentity = GetLegacyStepIdentity(localStep);
            if (!currentByLegacyIdentity.TryGetValue(legacyIdentity, out Queue<int>? queue) || queue.Count == 0)
            {
                continue;
            }

            int currentIndex = queue.Dequeue();
            currentStepMatched[currentIndex] = true;
            pairedCurrent[i] = currentIndex;
        }


        return pairedCurrent;
    }

    // A local step paired with a live step whose key differs carries that live key, so apply targets that step.
    private static JsonElement WithLiveStepKey(RegistrationItem localStep, RegistrationItem currentStep)
    {
        string localKey = TryGetString(localStep.Definition, "stepKey") ?? string.Empty;
        string liveKey = TryGetString(currentStep.Definition, "stepKey") ?? string.Empty;
        if (string.Equals(localKey, liveKey, StringComparison.OrdinalIgnoreCase)
            || JsonNode.Parse(localStep.Definition.GetRawText()) is not JsonObject obj)
        {
            return localStep.Definition;
        }

        obj[LiveStepKeyField] = liveKey;
        return JsonSerializer.SerializeToElement(obj);
    }

    public static RegistrationDiff BuildDiff(RegistrationState local, RegistrationState current)
    {
        List<RegistrationItem> added = [];
        List<RegistrationItem> removed = [];
        List<RegistrationChange> changed = [];

        List<RegistrationItem> localCustomApis = local.Items.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
        List<RegistrationItem> currentCustomApis = current.Items.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
        Dictionary<string, RegistrationItem> localCustomApiMap = BuildIdentityMap(localCustomApis, "local Custom API");
        Dictionary<string, RegistrationItem> currentCustomApiMap = BuildIdentityMap(currentCustomApis, "live Custom API");

        foreach (KeyValuePair<string, RegistrationItem> pair in localCustomApiMap)
        {
            if (!currentCustomApiMap.TryGetValue(pair.Key, out RegistrationItem? before))
            {
                added.Add(pair.Value);
                continue;
            }

            string beforeJson = ComparisonKey(before.Definition);
            string afterJson = ComparisonKey(pair.Value.Definition);
            if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                changed.Add(new RegistrationChange(pair.Value.Kind, pair.Value.Type, before.Definition, pair.Value.Definition));
            }
        }

        foreach (KeyValuePair<string, RegistrationItem> pair in currentCustomApiMap)
        {
            if (!localCustomApiMap.ContainsKey(pair.Key))
            {
                removed.Add(pair.Value);
            }
        }

        List<RegistrationItem> localSteps = local.Items.Where(IsStep).ToList();
        List<RegistrationItem> currentSteps = current.Items.Where(IsStep).ToList();
        // Pair each local step with its live counterpart (step key first, then legacy identity), then classify.
        int[] pairedCurrent = PairSteps(localSteps, currentSteps);
        bool[] currentStepMatched = new bool[currentSteps.Count];
        for (int i = 0; i < localSteps.Count; i++)
        {
            if (pairedCurrent[i] < 0)
            {
                added.Add(localSteps[i]);
                continue;
            }

            currentStepMatched[pairedCurrent[i]] = true;
            RegistrationItem localStep = localSteps[i];
            RegistrationItem currentStep = currentSteps[pairedCurrent[i]];
            string beforeJson = ComparisonKey(currentStep.Definition);
            string afterJson = ComparisonKey(localStep.Definition);
            if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                changed.Add(new RegistrationChange(localStep.Kind, localStep.Type, currentStep.Definition, WithLiveStepKey(localStep, currentStep)));
            }
        }

        for (int i = 0; i < currentSteps.Count; i++)
        {
            if (!currentStepMatched[i])
            {
                removed.Add(currentSteps[i]);
            }
        }

        // Web resources reconcile independently of plugins/Custom APIs: match by name (path), change by content
        // hash. Scope safety (a plugin-only run never touches web resources and vice versa) is upstream - the
        // current state only carries web resources when a web-resources scan is requested, so plugin-only runs
        // have none on either side and produce no web-resource diff here.
        List<RegistrationItem> localWebResources = local.Items.Where(IsWebResource).ToList();
        List<RegistrationItem> currentWebResources = current.Items.Where(IsWebResource).ToList();
        Dictionary<string, RegistrationItem> localWebResourceMap = BuildIdentityMap(localWebResources, "local web resource");
        Dictionary<string, RegistrationItem> currentWebResourceMap = BuildIdentityMap(currentWebResources, "live web resource");

        foreach (KeyValuePair<string, RegistrationItem> pair in localWebResourceMap)
        {
            if (!currentWebResourceMap.TryGetValue(pair.Key, out RegistrationItem? before))
            {
                added.Add(pair.Value);
                continue;
            }

            // Compare only the content hash: the local definition also carries a local file path the live state
            // can't have, so a whole-definition compare would always report a spurious change.
            string beforeHash = TryGetString(before.Definition, "contentHash") ?? string.Empty;
            string afterHash = TryGetString(pair.Value.Definition, "contentHash") ?? string.Empty;
            if (!string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
            {
                changed.Add(new RegistrationChange(pair.Value.Kind, pair.Value.Type, before.Definition, pair.Value.Definition));
            }
        }

        // Deletion is scoped to the top-level folders actually deployed locally, so mirroring a subset folder never
        // hard-deletes web resources under other top-level folders (e.g. another team's `other_/...`). An empty
        // local set therefore removes nothing.
        HashSet<string> localTopFolders = new HashSet<string>(
            localWebResources.Select(w => TopFolder(GetIdentity(w))),
            StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, RegistrationItem> pair in currentWebResourceMap)
        {
            if (!localWebResourceMap.ContainsKey(pair.Key) && localTopFolders.Contains(TopFolder(GetIdentity(pair.Value))))
            {
                removed.Add(pair.Value);
            }
        }

        return new RegistrationDiff(
            LocalAssemblies: local.SourceAssemblies,
            CurrentAssemblies: current.SourceAssemblies,
            Added: added.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
            Removed: removed.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
            Changed: changed.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
            LocalItems: local.Items.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
            CurrentItems: current.Items.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray());
    }
}
