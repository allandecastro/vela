using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    public static string GetIdentityKey(RegistrationItem item)
    {
        return $"{item.Kind}:{GetIdentity(item)}";
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

    public static string GetLegacyStepIdentity(RegistrationItem item)
    {
        string pluginTypeName = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
        string message = TryGetString(item.Definition, "message") ?? string.Empty;
        int stage = TryGetInt(item.Definition, "stage");
        int mode = TryGetInt(item.Definition, "mode");
        string entity = TryGetString(item.Definition, "entityLogicalName") ?? string.Empty;
        return $"{pluginTypeName}:{message}:{stage}:{mode}:{entity}";
    }

    public static RegistrationDiff BuildDiff(RegistrationState local, RegistrationState current)
    {
        List<RegistrationItem> added = [];
        List<RegistrationItem> removed = [];
        List<RegistrationChange> changed = [];

        List<RegistrationItem> localCustomApis = local.Items.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
        List<RegistrationItem> currentCustomApis = current.Items.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
        Dictionary<string, RegistrationItem> localCustomApiMap = localCustomApis.ToDictionary(GetIdentityKey, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RegistrationItem> currentCustomApiMap = currentCustomApis.ToDictionary(GetIdentityKey, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, RegistrationItem> pair in localCustomApiMap)
        {
            if (!currentCustomApiMap.TryGetValue(pair.Key, out RegistrationItem? before))
            {
                added.Add(pair.Value);
                continue;
            }

            string beforeJson = JsonSerializer.Serialize(before.Definition);
            string afterJson = JsonSerializer.Serialize(pair.Value.Definition);
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
        bool[] currentStepMatched = new bool[currentSteps.Count];

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
            RegistrationItem currentStep = currentSteps[currentIndex];
            string beforeJson = JsonSerializer.Serialize(currentStep.Definition);
            string afterJson = JsonSerializer.Serialize(localStep.Definition);
            if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                changed.Add(new RegistrationChange(localStep.Kind, localStep.Type, currentStep.Definition, localStep.Definition));
            }
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

        foreach (RegistrationItem localStep in localSteps)
        {
            string localStepKey = GetStepKey(localStep);
            if (!string.IsNullOrWhiteSpace(localStepKey) && currentByStepKey.ContainsKey(localStepKey))
            {
                continue;
            }

            string legacyIdentity = GetLegacyStepIdentity(localStep);
            if (!currentByLegacyIdentity.TryGetValue(legacyIdentity, out Queue<int>? queue) || queue.Count == 0)
            {
                added.Add(localStep);
                continue;
            }

            int currentIndex = queue.Dequeue();
            currentStepMatched[currentIndex] = true;
            RegistrationItem currentStep = currentSteps[currentIndex];
            string beforeJson = JsonSerializer.Serialize(currentStep.Definition);
            string afterJson = JsonSerializer.Serialize(localStep.Definition);
            if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                changed.Add(new RegistrationChange(localStep.Kind, localStep.Type, currentStep.Definition, localStep.Definition));
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
        Dictionary<string, RegistrationItem> localWebResourceMap = localWebResources.ToDictionary(GetIdentityKey, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RegistrationItem> currentWebResourceMap = currentWebResources.ToDictionary(GetIdentityKey, StringComparer.OrdinalIgnoreCase);

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

        foreach (KeyValuePair<string, RegistrationItem> pair in currentWebResourceMap)
        {
            if (!localWebResourceMap.ContainsKey(pair.Key))
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
