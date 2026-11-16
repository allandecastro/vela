using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Plugin types that a package update removes. Dataverse refuses, atomically, to remove a plugin type that still has
/// steps, even disabled ones (verified live: 0x80040265 "Unable to delete '&lt;type&gt;' plugintype due to N step(s)"),
/// so the steps must be gone before the publish. Vela deletes its own steps first (the delete phase runs before the
/// publish); a step Vela does not delete stops the run in the preview, before any write. A renamed assembly is the
/// same case for every type of the old assembly: its live steps are read through the package (retired assemblies).
/// </summary>
internal static partial class DataverseRegistration
{
    /// <summary>
    /// Adds the retired assemblies to the live state: the assemblies of each deployed plugin package of the run that the
    /// local package no longer contains (the project's assembly was renamed). Their Vela steps and their Custom APIs go
    /// to <see cref="RegistrationState.RetiredItems"/>, which the diff deletes (steps) or moves to the local type
    /// (Custom APIs); a step Vela did not write is left out, and blocks the run (FindPluginTypeRemovalBlockers).
    /// </summary>
    internal static RegistrationState WithRetiredAssemblies(IOrganizationService service, RegistrationState current, RegistrationState local, string? solutionUniqueName, JsonSerializerOptions jsonOptions)
    {
        string[] retired = FindRetiredAssemblies(service, local, solutionUniqueName, out Dictionary<string, string> packageByAssembly);
        if (retired.Length == 0)
        {
            return current;
        }

        List<string> found = [];
        List<RegistrationItem> items = [];
        List<string> info = [.. current.Info ?? []];
        foreach (string assembly in retired)
        {
            AssembliesReadResult read = ReadAssembliesState(service, [assembly], jsonOptions);
            if (read.FoundAssemblies.Count == 0)
            {
                continue;
            }

            found.Add(assembly);
            RegistrationItem[] velaSteps = read.Items.Where(i => DiffEngine.IsStep(i) && IsVelaWrittenStep(i)).ToArray();
            RegistrationItem[] apis = read.Items.Where(i => !DiffEngine.IsStep(i)).ToArray();
            items.AddRange(velaSteps);
            items.AddRange(apis);
            info.Add($"Plugin assembly '{assembly}' is no longer in plugin package '{packageByAssembly[assembly]}': apply deletes its " +
                $"{velaSteps.Length} Vela step(s) before the package update and moves its {apis.Length} Custom API(s) to the local plugin types.");
        }

        return current with
        {
            RetiredAssemblies = [.. found],
            RetiredItems = [.. items],
            Info = [.. info],
        };
    }

    // The deployed assemblies of the run's plugin packages (pluginassembly.packageid) whose name the local packages no
    // longer build. Read-only.
    private static string[] FindRetiredAssemblies(IOrganizationService service, RegistrationState local, string? solutionUniqueName, out Dictionary<string, string> packageByAssembly)
    {
        packageByAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (local.PackagePayloads.Length == 0)
        {
            return [];
        }

        HashSet<string> localAssemblies = new(local.SourceAssemblies.Concat(local.SourceAssemblyPayloads.Select(p => p.AssemblyName)), StringComparer.OrdinalIgnoreCase);
        string? prefix = string.IsNullOrWhiteSpace(solutionUniqueName) ? null : ResolveSolutionCustomizationPrefix(service, solutionUniqueName!);
        foreach (PluginPackagePayload payload in local.PackagePayloads)
        {
            string packageName = NormalizePackageNameWithPrefix(payload.PackageName, prefix);
            if (GetPluginPackage(service, packageName) is not { } package)
            {
                continue;
            }

            QueryExpression query = new QueryExpression("pluginassembly") { ColumnSet = new ColumnSet("pluginassemblyid", "name") };
            query.Criteria.AddCondition("packageid", ConditionOperator.Equal, package.Id);
            foreach (Entity assembly in service.RetrieveAll(query))
            {
                string? name = assembly.GetAttributeValue<string>("name");
                if (!string.IsNullOrWhiteSpace(name) && !localAssemblies.Contains(name))
                {
                    packageByAssembly[name!] = packageName;
                }
            }
        }

        return packageByAssembly.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // A step Vela wrote: its name or description carries a Vela step key, or its name is Vela's keyless name. Other
    // tools name steps differently (the Plugin Registration Tool: "<type>: <message> of <table>").
    internal static bool IsVelaWrittenStep(RegistrationItem step)
    {
        JsonElement definition = step.Definition;
        if (!string.IsNullOrWhiteSpace(TryGetString(definition, "stepKey")))
        {
            return true;
        }

        string keyless = DefinitionNormalizer.BuildKeylessStepName(
            TryGetString(definition, "pluginTypeName") ?? step.Type, TryGetInt(definition, "stage"), TryGetInt(definition, "mode"),
            TryGetString(definition, "message") ?? string.Empty, TryGetString(definition, "entityLogicalName") ?? string.Empty);
        return string.Equals((TryGetString(definition, "name") ?? string.Empty).Trim(), keyless, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-only pre-flight for the package update: the plugin types it removes (a live type of the run's
    /// assemblies that the local scan no longer has, and every type of a retired assembly), and, for each, the live
    /// steps the run does not delete before the publish. Each such type is one blocking problem naming its steps.
    /// A Custom API's own main-operation step goes with the Custom API, so it does not block when the run deletes or
    /// detaches that Custom API. Runs only when a plugin package is updated.
    /// </summary>
    internal static IReadOnlyList<string> FindPluginTypeRemovalBlockers(IOrganizationService service, RegistrationState local, RegistrationState current, RegistrationDiff diff, bool packageUpdated)
    {
        if (!packageUpdated)
        {
            return [];
        }

        Dictionary<Guid, string> removedTypes = [];
        if (local.PluginTypeNames != null)
        {
            HashSet<string> localTypes = new(local.PluginTypeNames, StringComparer.Ordinal);
            foreach (string assembly in current.SourceAssemblies)
            {
                if (GetPluginAssembly(service, assembly) is not { } row)
                {
                    continue;
                }

                foreach ((Guid id, string typeName) in GetPluginTypes(service, row.Id).Where(t => !localTypes.Contains(t.Value)))
                {
                    removedTypes[id] = typeName;
                }
            }
        }

        foreach (string assembly in current.RetiredAssemblies ?? [])
        {
            if (GetPluginAssembly(service, assembly) is { } row)
            {
                foreach ((Guid id, string typeName) in GetPluginTypes(service, row.Id))
                {
                    removedTypes[id] = typeName;
                }
            }
        }

        if (removedTypes.Count == 0)
        {
            return [];
        }

        HashSet<Guid> deletedSteps = new(diff.Removed.Where(DiffEngine.IsStep).Select(i => i.LiveId).OfType<Guid>());
        HashSet<string> releasedApis = new(
            diff.Removed.Where(i => i.Kind == "custom-api").Select(i => TryGetString(i.Definition, "uniqueName") ?? i.Type)
                .Concat(diff.Changed.Where(c => c.Kind == "custom-api" && IsCustomApiImplementationChange(c)).Select(c => TryGetString(c.Before, "uniqueName") ?? c.Type)),
            StringComparer.OrdinalIgnoreCase);

        List<Entity> steps = service.RetrieveAllInChunks(removedTypes.Keys.ToArray(), chunk =>
        {
            QueryExpression query = new QueryExpression("sdkmessageprocessingstep") { ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "eventhandler", "name", "stage") };
            query.Criteria.AddCondition("eventhandler", ConditionOperator.In, chunk);
            LinkEntity message = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.LeftOuter);
            message.EntityAlias = "msg";
            message.Columns = new ColumnSet("name");
            return query;
        });

        return steps
            .Where(step => !deletedSteps.Contains(step.Id))
            .Where(step => !((step.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0) == SdkConstants.PipelineStage.MainOperation
                && releasedApis.Contains(GetAliasedString(step, "msg.name") ?? string.Empty)))
            .GroupBy(step => step.GetAttributeValue<EntityReference>("eventhandler")?.Id ?? Guid.Empty)
            .Where(group => removedTypes.ContainsKey(group.Key))
            .OrderBy(group => removedTypes[group.Key], StringComparer.Ordinal)
            .Select(group =>
            {
                string[] names = group.Select(s => s.GetAttributeValue<string>("name") is { Length: > 0 } n ? $"'{n}'" : s.Id.ToString())
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray();
                return $"The plugin package update removes plugin type '{removedTypes[group.Key]}', but {names.Length} step(s) that Vela " +
                    $"does not manage are registered on it: {string.Join(", ", names)}. Dataverse refuses to delete a plugin type that " +
                    "has steps, even disabled ones (0x80040265). Delete those steps or move them to another plugin type, then run again.";
            })
            .ToArray();
    }

    /// <summary>
    /// Warnings for the Vela steps of a retired assembly that carry a secure configuration: the step is deleted and
    /// re-created on the new assembly's plugin type, and the secure configuration is deleted with it. Vela cannot read
    /// a value back to carry it over, so a re-created step gets one only when the run supplies it.
    /// </summary>
    internal static IEnumerable<string> RetiredSecureConfigWarnings(RegistrationState local, RegistrationState current)
    {
        Dictionary<string, RegistrationItem> localByStep = local.Items.Where(DiffEngine.IsStep)
            .GroupBy(DiffEngine.GetSecureConfigKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (RegistrationItem retired in (current.RetiredItems ?? []).Where(DiffEngine.IsStep))
        {
            string key = DiffEngine.GetSecureConfigKey(retired);
            if (string.IsNullOrEmpty(TryGetString(retired.Definition, "secureConfig"))
                || !localByStep.ContainsKey(key)
                || local.SecureConfigByStepKey?.ContainsKey(key) == true)
            {
                continue;
            }

            yield return $"Step '{TryGetString(retired.Definition, "name") ?? retired.Type}' moves to the renamed assembly: it is deleted and " +
                "re-created, and its secure configuration is deleted with it. Supply the value (--secure-config or " +
                "VELA_SECURE_CONFIG_<NAME>) so the re-created step gets it.";
        }
    }
}
