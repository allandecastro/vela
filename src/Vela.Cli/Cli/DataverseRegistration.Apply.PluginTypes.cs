using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Xrm.Sdk;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DiffEngine;


namespace Vela.Cli;

/// <summary> Resolves the plugin types the run needs once the package is published, with a bounded poll. </summary>
internal static partial class DataverseRegistration
{
    internal static HashSet<string> GetRequiredPluginTypeNames(RegistrationDiff diff)
{
    HashSet<string> required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (RegistrationItem item in diff.Added)
    {
        AddRequiredPluginTypeName(required, item.Definition);
    }

    foreach (RegistrationChange change in diff.Changed)
    {
        AddRequiredPluginTypeName(required, change.After);
    }

    return required;
}

    internal static void AddRequiredPluginTypeName(HashSet<string> required, JsonElement definition)
{
    string? pluginTypeName = TryGetString(definition, "pluginTypeName");
    if (!string.IsNullOrWhiteSpace(pluginTypeName))
    {
        required.Add(pluginTypeName);
    }
}

    internal static void WaitForRequiredPluginTypes(IOrganizationService service, string[] localAssemblies, HashSet<string> requiredPluginTypes)
{
    // Allow longer here: required types may span several freshly-uploaded assemblies.
    const int requiredPluginTypesSyncTimeoutSeconds = 60;
    const int pollIntervalMs = 2000;

    DateTime deadline = DateTime.UtcNow.AddSeconds(requiredPluginTypesSyncTimeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        Dictionary<string, Guid> pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, localAssemblies);
        if (requiredPluginTypes.All(pluginTypeIds.ContainsKey))
        {
            return;
        }

        Thread.Sleep(pollIntervalMs);
    }
}

    internal static HashSet<Guid> GetPluginTypeIdsForAssemblies(IOrganizationService service, string[] sourceAssemblies)
{
    HashSet<Guid> ids = [];
    foreach (string assemblyName in sourceAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (GetPluginAssembly(service, assemblyName) is { } pluginAssembly)
        {
            ids.UnionWith(GetPluginTypes(service, pluginAssembly.Id).Keys);
        }
    }

    return ids;
}

    internal static Dictionary<string, Guid> GetPluginTypeIdByNameForAssemblies(IOrganizationService service, string[] sourceAssemblies)
{
    Dictionary<string, Guid> map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    foreach (string assemblyName in sourceAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        Entity? pluginAssembly = GetPluginAssembly(service, assemblyName);
        if (pluginAssembly == null)
        {
            continue;
        }

        Dictionary<Guid, string> types = GetPluginTypes(service, pluginAssembly.Id);
        foreach ((Guid id, string typeName) in types)
        {
            if (!map.ContainsKey(typeName))
            {
                map[typeName] = id;
            }
        }
    }

    return map;
}
}
