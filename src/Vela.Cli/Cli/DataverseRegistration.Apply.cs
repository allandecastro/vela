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
using static Vela.Cli.DiffEngine;

namespace Vela.Cli;

/// <summary> Applies a registration diff: ordering, package/assembly/type sync, resume report. </summary>
internal static partial class DataverseRegistration
{
    internal static void ApplyDiff(IOrganizationService service, RegistrationDiff diff, AssemblyPayload[] sourceAssemblyPayloads, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, bool verbose)
{
    // When a solution is given, route every create/update through it so all components (package, assembly, types,
    // steps, images, secure config, Custom APIs and their parameters) are added to it — not just the package.
    IOrganizationService svc = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? service
        : new SolutionScopedOrganizationService(service, solutionUniqueName!);

    List<RegistrationItem> removedSteps = diff.Removed.Where(IsStep).ToList();
    List<RegistrationItem> removedApis = diff.Removed.Where(x => !IsStep(x)).ToList();
    List<RegistrationItem> addedApis = diff.Added.Where(x => !IsStep(x)).ToList();
    List<RegistrationChange> changedApis = diff.Changed.Where(x => string.Equals(x.Kind, "custom-api", StringComparison.OrdinalIgnoreCase)).ToList();
    List<RegistrationItem> addedSteps = diff.Added.Where(IsStep).ToList();
    List<RegistrationChange> changedSteps = diff.Changed.Where(x => string.Equals(x.Kind, "step", StringComparison.OrdinalIgnoreCase)).ToList();

    // Ordered list of every operation this apply will perform, used to produce a resume report if
    // the run fails partway through (Dataverse has no cross-entity transaction).
    const string publishOperation = "publish plugin package(s) and resolve plugin types";
    List<string> planned = [];
    planned.AddRange(removedSteps.Select(x => $"delete step {TryGetString(x.Definition, "stepKey") ?? x.Type}"));
    planned.AddRange(removedApis.Select(x => $"delete custom-api {TryGetString(x.Definition, "uniqueName") ?? x.Type}"));
    planned.Add(publishOperation);
    planned.AddRange(addedApis.Select(x => $"create custom-api {x.Type}"));
    planned.AddRange(changedApis.Select(x => $"update custom-api {x.Type}"));
    planned.AddRange(addedSteps.Select(x => $"create step {x.Type}"));
    planned.AddRange(changedSteps.Select(x => $"update step {x.Type}"));

    int done = 0;
    try
    {
        // Delete obsolete steps and custom APIs BEFORE uploading the new package.
        // Dataverse refuses to replace plugin types that still have steps registered,
        // so we must clear them first.
        foreach (RegistrationItem item in removedSteps)
        {
            try
            {
                string stepKey = TryGetString(item.Definition, "stepKey") ?? item.Type;
                LogOperation("step", "delete", stepKey);
                DeleteStepByDefinition(service, item.Definition);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping step delete for '{item.Type}' (managed by custom API): {ex.Message}");
            }

            done++;
        }

        foreach (RegistrationItem item in removedApis)
        {
            try
            {
                string uniqueName = TryGetString(item.Definition, "uniqueName") ?? item.Type;
                LogOperation("custom-api", "delete", uniqueName);
                DeleteCustomApi(service, item.Definition);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping custom API delete for '{item.Type}': {ex.Message}");
            }

            done++;
        }

        EnsurePluginPackages(svc, packagePayloads, solutionUniqueName, tempDirectories, verbose);

        HashSet<string> requiredPluginTypes = GetRequiredPluginTypeNames(diff);
        Dictionary<string, Guid> pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, diff.LocalAssemblies);
        if (requiredPluginTypes.Any(x => !pluginTypeIds.ContainsKey(x)))
        {
            if (packagePayloads.Length > 0)
            {
                WaitForRequiredPluginTypes(service, diff.LocalAssemblies, requiredPluginTypes);
            }
            else
            {
                EnsurePluginAssembliesForMissingTypes(svc, sourceAssemblyPayloads, requiredPluginTypes);
                pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, diff.LocalAssemblies);
                if (requiredPluginTypes.Any(x => !pluginTypeIds.ContainsKey(x)))
                {
                    EnsurePluginTypesForMissingEntries(svc, sourceAssemblyPayloads, requiredPluginTypes);
                }
            }

            pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, diff.LocalAssemblies);
        }

        string[] missingPluginTypes = requiredPluginTypes
            .Where(x => !pluginTypeIds.ContainsKey(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (missingPluginTypes.Length > 0)
        {
            throw new InvalidOperationException($"Plugin type not found in Dataverse after assembly sync: {string.Join(", ", missingPluginTypes)}");
        }

        done++; // publish + resolve plugin types

        foreach (RegistrationItem item in addedApis)
        {
            try
            {
                UpsertCustomApi(svc, pluginTypeIds, item.Definition, verbose);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping custom API upsert for '{item.Type}': {ex.Message}");
            }

            done++;
        }

        foreach (RegistrationChange change in changedApis)
        {
            try
            {
                UpsertCustomApi(svc, pluginTypeIds, change.After, verbose);
            }
            catch (Exception ex) when (IsIgnorableCustomApiStageException(ex))
            {
                Console.Error.WriteLine($"Warning: skipping custom API update for '{change.Type}': {ex.Message}");
            }

            done++;
        }

        // Shared across every step in this run so message/filter ids are queried once, not per step.
        StepMetadataCache metadataCache = new StepMetadataCache();
        foreach (RegistrationItem item in addedSteps)
        {
            UpsertStep(svc, pluginTypeIds, item.Definition, verbose, metadataCache);
            done++;
        }

        foreach (RegistrationChange change in changedSteps)
        {
            UpsertStep(svc, pluginTypeIds, change.After, verbose, metadataCache);
            done++;
        }
    }
    catch (Exception ex)
    {
        string failed = done < planned.Count ? planned[done] : "(unknown operation)";
        List<string> completed = planned.Take(done).ToList();
        List<string> remaining = planned.Skip(done + 1).ToList();
        foreach (string line in ApplyResumeReport.Build(failed, ex.Message, completed, remaining))
        {
            Console.Error.WriteLine(line);
        }

        throw;
    }
}

    internal static bool IsIgnorableCustomApiStageException(Exception ex)
{
    // Only a genuine Dataverse platform fault for the "invalid plug-in registration stage"
    // validation is ignorable (the custom-API main-operation step is managed by the platform).
    // Gating on FaultException<OrganizationServiceFault> ensures a generic programming error whose
    // message merely contains the phrase is never silently swallowed. We walk the InnerException
    // chain because ServiceClient often wraps the fault in an outer operation exception.
    Exception? current = ex;
    while (current != null)
    {
        if (current is FaultException<OrganizationServiceFault> fault &&
            (fault.Message ?? string.Empty).Contains("Invalid plug-in registration stage", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        current = current.InnerException;
    }

    return false;
}

    internal static void EnsurePluginPackages(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, bool verbose)
{
    // Dataverse rejects a plugin-package import whose nuget name has no solution publisher prefix (0x80040265,
    // "The nuget file name does not contain a solution prefix"). Without a --solution to source that prefix, a
    // package whose name isn't already prefixed can't import — fail fast with a clear message instead.
    if (string.IsNullOrWhiteSpace(solutionUniqueName))
    {
        PluginPackagePayload? unprefixed = packagePayloads.FirstOrDefault(p => !LooksSolutionPrefixed(p.PackageName));
        if (unprefixed != null)
        {
            throw new InvalidOperationException(
                $"Plugin package '{unprefixed.PackageName}' has no solution publisher prefix, which Dataverse requires " +
                "to import a package. Re-run 'apply --execute' with --solution <uniqueName> so the prefix is applied.");
        }
    }

    string? solutionPrefix = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? null
        : ResolveSolutionCustomizationPrefix(service, solutionUniqueName);

    // Solution membership comes from the caller's solution-scoped service (the SolutionUniqueName request
    // parameter); the prefix only shapes the package name.
    foreach (PluginPackagePayload package in packagePayloads)
    {
        UpsertPluginPackage(service, package, solutionPrefix, tempDirectories, verbose);
    }
}

    internal static Guid UpsertPluginPackage(IOrganizationService service, PluginPackagePayload package, string? solutionPrefix, List<string> tempDirectories, bool verbose)
{
    if (!File.Exists(package.PackagePath))
    {
        throw new FileNotFoundException($"Plugin package file not found: {package.PackagePath}", package.PackagePath);
    }

    string desiredPackageName = NormalizePackageNameWithPrefix(package.PackageName, solutionPrefix);
    byte[] packageBytes = ReadPackageBytesForImport(package.PackagePath, package.PackageName, desiredPackageName, tempDirectories);
    string packageContent = Convert.ToBase64String(packageBytes);
    Entity? existing = GetPluginPackage(service, desiredPackageName);
    if (existing == null)
    {
        LogOperation("package", "create", desiredPackageName, $"v{package.Version}", verbose);
        Entity create = new Entity("pluginpackage");
        create["name"] = desiredPackageName;
        create["version"] = package.Version;
        create["content"] = packageContent;
        return service.Create(create);
    }

    LogOperation("package", "update", desiredPackageName, $"v{package.Version}", verbose);
    Entity update = new Entity("pluginpackage")
    {
        Id = existing.Id
    };
    update["version"] = package.Version;
    update["content"] = packageContent;
    service.Update(update);
    return existing.Id;
}

    internal static string NormalizePackageNameWithPrefix(string packageName, string? solutionPrefix)
{
    if (string.IsNullOrWhiteSpace(solutionPrefix) || packageName.StartsWith(solutionPrefix + "_", StringComparison.OrdinalIgnoreCase))
    {
        return packageName;
    }

    return $"{solutionPrefix}_{packageName}";
}

    // A package name already carries a solution publisher prefix when it starts with "<prefix>_" and <prefix> is a
    // plausible Dataverse customization prefix (2-8 letters/digits, letter-first). Used to decide whether importing
    // without a --solution can possibly succeed.
    internal static bool LooksSolutionPrefixed(string packageName)
{
    int underscore = packageName.IndexOf('_');
    if (underscore < 2)
    {
        return false;
    }

    string prefix = packageName.Substring(0, underscore);
    return prefix.Length >= 2 && prefix.Length <= 8 && char.IsLetter(prefix[0]) && prefix.All(char.IsLetterOrDigit);
}

    internal static string ResolveSolutionCustomizationPrefix(IOrganizationService service, string solutionUniqueName)
{
    QueryExpression query = new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("publisherid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);

    Entity? solution = service.RetrieveMultiple(query).Entities.FirstOrDefault();
    if (solution == null)
    {
        throw new InvalidOperationException($"Solution not found: {solutionUniqueName}");
    }

    Guid publisherId = solution.GetAttributeValue<EntityReference>("publisherid")?.Id ?? Guid.Empty;
    if (publisherId == Guid.Empty)
    {
        throw new InvalidOperationException($"Publisher not found for solution: {solutionUniqueName}");
    }

    Entity publisher = service.Retrieve("publisher", publisherId, new ColumnSet("customizationprefix"));
    string? prefix = publisher.GetAttributeValue<string>("customizationprefix");
    if (string.IsNullOrWhiteSpace(prefix))
    {
        throw new InvalidOperationException($"Customization prefix not found for solution: {solutionUniqueName}");
    }

    return prefix.Trim();
}

    internal static byte[] ReadPackageBytesForImport(string packagePath, string originalPackageName, string desiredPackageName, List<string> tempDirectories)
{
    if (string.Equals(originalPackageName, desiredPackageName, StringComparison.OrdinalIgnoreCase))
    {
        return File.ReadAllBytes(packagePath);
    }

    string tempRoot = Path.Combine(Path.GetTempPath(), "dataverse-framework-tools", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    tempDirectories.Add(tempRoot);

    ZipFile.ExtractToDirectory(packagePath, tempRoot);
    string[] nuspecFiles = Directory.GetFiles(tempRoot, "*.nuspec", SearchOption.TopDirectoryOnly);
    if (nuspecFiles.Length == 0)
    {
        throw new InvalidOperationException($"No .nuspec found in package '{packagePath}'.");
    }

    string nuspecPath = nuspecFiles[0];
    XDocument nuspec = XDocument.Load(nuspecPath);
    XNamespace ns = nuspec.Root?.Name.Namespace ?? XNamespace.None;
    XElement? idElement = nuspec.Root?.Element(ns + "metadata")?.Element(ns + "id");
    if (idElement == null)
    {
        throw new InvalidOperationException($"Package '{packagePath}' is missing metadata id in nuspec.");
    }

    idElement.Value = desiredPackageName;
    nuspec.Save(nuspecPath);

    string desiredNuspecPath = Path.Combine(tempRoot, $"{desiredPackageName}.nuspec");
    if (!string.Equals(Path.GetFileName(nuspecPath), Path.GetFileName(desiredNuspecPath), StringComparison.OrdinalIgnoreCase))
    {
        if (File.Exists(desiredNuspecPath))
        {
            File.Delete(desiredNuspecPath);
        }

        File.Move(nuspecPath, desiredNuspecPath);
    }

    string outputDirectory = Path.Combine(Path.GetTempPath(), "dataverse-framework-tools", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDirectory);
    tempDirectories.Add(outputDirectory);
    string transformedPackagePath = Path.Combine(outputDirectory, $"{desiredPackageName}.nupkg");
    ZipFile.CreateFromDirectory(tempRoot, transformedPackagePath);
    return File.ReadAllBytes(transformedPackagePath);
}

    internal static Entity? GetPluginPackage(IOrganizationService service, string packageName)
{
    QueryExpression query = new QueryExpression("pluginpackage")
    {
        ColumnSet = new ColumnSet("pluginpackageid", "name"),
        TopCount = 1
    };
    query.Criteria.AddCondition("name", ConditionOperator.Equal, packageName);
    return service.RetrieveMultiple(query).Entities.FirstOrDefault();
}

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

    internal static void EnsurePluginAssembliesForMissingTypes(IOrganizationService service, AssemblyPayload[] sourceAssemblyPayloads, HashSet<string> requiredPluginTypes)
{
    if (sourceAssemblyPayloads.Length == 0 || requiredPluginTypes.Count == 0)
    {
        return;
    }

    HashSet<string> missingPluginTypes = new HashSet<string>(
        requiredPluginTypes.Where(x => !GetPluginTypeIdByNameForAssemblies(service, sourceAssemblyPayloads.Select(p => p.AssemblyName).ToArray()).ContainsKey(x)),
        StringComparer.OrdinalIgnoreCase);
    if (missingPluginTypes.Count == 0)
    {
        return;
    }

    // Fail fast if a new plugin type is being added under an unchanged assembly version — re-uploading the
    // same version would leave Dataverse serving the cached (old) assembly, so the type would not be found.
    foreach (AssemblyPayload payload in sourceAssemblyPayloads)
    {
        string[] newForAssembly = missingPluginTypes
            .Where(t => string.Equals(t, payload.AssemblyName, StringComparison.OrdinalIgnoreCase)
                || t.StartsWith(payload.AssemblyName + ".", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
        GuardAgainstStaleAssemblyVersion(service, payload, newForAssembly);
    }

    foreach (AssemblyPayload payload in sourceAssemblyPayloads)
    {
        UpsertPluginAssembly(service, payload);
    }

    foreach (AssemblyPayload payload in sourceAssemblyPayloads)
    {
        HashSet<string> requiredForAssembly = new HashSet<string>(
            missingPluginTypes.Where(typeName =>
                string.Equals(typeName, payload.AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                typeName.StartsWith(payload.AssemblyName + ".", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        if (requiredForAssembly.Count > 0)
        {
            WaitForPluginTypes(service, payload.AssemblyName, requiredForAssembly);
        }
    }
}

    internal static void UpsertPluginAssembly(IOrganizationService service, AssemblyPayload payload)
{
    if (!File.Exists(payload.AssemblyPath))
    {
        throw new FileNotFoundException($"Assembly payload file not found: {payload.AssemblyPath}", payload.AssemblyPath);
    }

    Entity? existing = GetPluginAssembly(service, payload.AssemblyName);
    string content = Convert.ToBase64String(File.ReadAllBytes(payload.AssemblyPath));
    AssemblyName assemblyInfo = AssemblyName.GetAssemblyName(payload.AssemblyPath);
    string version = assemblyInfo.Version?.ToString() ?? "1.0.0.0";
    string culture = string.IsNullOrWhiteSpace(assemblyInfo.CultureName) ? "neutral" : assemblyInfo.CultureName;
    byte[]? tokenBytes = assemblyInfo.GetPublicKeyToken();
    string publicKeyToken = tokenBytes == null || tokenBytes.Length == 0
        ? "null"
        : Convert.ToHexString(tokenBytes).ToLowerInvariant();
    if (existing == null)
    {
        Entity create = new Entity("pluginassembly");
        create["name"] = payload.AssemblyName;
        create["content"] = content;
        create["version"] = version;
        create["culture"] = culture;
        create["publickeytoken"] = publicKeyToken;
        create["sourcetype"] = new OptionSetValue(0);
        create["isolationmode"] = new OptionSetValue(2);
        service.Create(create);
        return;
    }

    Entity update = new Entity("pluginassembly")
    {
        Id = existing.Id
    };
    update["content"] = content;
    update["version"] = version;
    update["culture"] = culture;
    update["publickeytoken"] = publicKeyToken;
    service.Update(update);
}

    // Adding a new plugin type but re-uploading the assembly under the SAME version is the classic Dataverse
    // trap: the platform caches plugin assemblies by version and keeps serving the old one, so the new type
    // fails at runtime with "The plug-in type could not be found in the plug-in assembly". Catch it at apply.
    internal static void GuardAgainstStaleAssemblyVersion(IOrganizationService service, AssemblyPayload payload, string[] newPluginTypes)
{
    if (newPluginTypes.Length == 0 || !File.Exists(payload.AssemblyPath))
    {
        return;
    }

    Entity? existing = GetPluginAssembly(service, payload.AssemblyName);
    if (existing == null)
    {
        return; // first registration of this assembly — nothing cached to go stale
    }

    string liveVersion = service.Retrieve("pluginassembly", existing.Id, new ColumnSet("version"))
        .GetAttributeValue<string>("version") ?? string.Empty;
    string newVersion = AssemblyName.GetAssemblyName(payload.AssemblyPath).Version?.ToString() ?? string.Empty;
    if (!string.Equals(liveVersion, newVersion, StringComparison.OrdinalIgnoreCase))
    {
        return; // version bumped — Dataverse will load the new assembly
    }

    throw new InvalidOperationException(
        $"Assembly '{payload.AssemblyName}' registers new plugin type(s) [{string.Join(", ", newPluginTypes)}] but its " +
        $"version is unchanged ({newVersion}). Dataverse caches plugin assemblies by version and would keep serving the " +
        $"previously-registered assembly, so the new type(s) would fail at runtime with \"The plug-in type could not be " +
        $"found in the plug-in assembly\". Bump the assembly version (AssemblyVersion) and rebuild, then re-run apply. " +
        $"('vela init' scaffolds an auto-incrementing AssemblyVersion to prevent this.)");
}

    internal static void WaitForPluginTypes(IOrganizationService service, string assemblyName, HashSet<string> requiredPluginTypes)
{
    // Plugin types are materialized asynchronously by the platform after an assembly upload;
    // poll until they appear or the timeout elapses.
    const int pluginTypeSyncTimeoutSeconds = 30;
    const int pollIntervalMs = 2000;

    DateTime deadline = DateTime.UtcNow.AddSeconds(pluginTypeSyncTimeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        Entity? pluginAssembly = GetPluginAssembly(service, assemblyName);
        if (pluginAssembly != null)
        {
            HashSet<string> availablePluginTypes = new HashSet<string>(GetPluginTypes(service, pluginAssembly.Id).Values, StringComparer.OrdinalIgnoreCase);
            if (requiredPluginTypes.All(availablePluginTypes.Contains))
            {
                return;
            }
        }

        Thread.Sleep(pollIntervalMs);
    }
}

    internal static void EnsurePluginTypesForMissingEntries(IOrganizationService service, AssemblyPayload[] sourceAssemblyPayloads, HashSet<string> requiredPluginTypes)
{
    Dictionary<string, Guid> availablePluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, sourceAssemblyPayloads.Select(x => x.AssemblyName).ToArray());
    HashSet<string> missingPluginTypes = new HashSet<string>(
        requiredPluginTypes.Where(x => !availablePluginTypeIds.ContainsKey(x)),
        StringComparer.OrdinalIgnoreCase);
    if (missingPluginTypes.Count == 0)
    {
        return;
    }

    foreach (AssemblyPayload payload in sourceAssemblyPayloads)
    {
        string[] assemblyMissingTypes = missingPluginTypes
            .Where(typeName =>
                string.Equals(typeName, payload.AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                typeName.StartsWith(payload.AssemblyName + ".", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (assemblyMissingTypes.Length == 0)
        {
            continue;
        }

        Entity? assembly = GetPluginAssembly(service, payload.AssemblyName);
        if (assembly == null)
        {
            continue;
        }

        HashSet<string> existingTypeNames = new HashSet<string>(GetPluginTypes(service, assembly.Id).Values, StringComparer.OrdinalIgnoreCase);
        foreach (string missingTypeName in assemblyMissingTypes)
        {
            if (!existingTypeNames.Contains(missingTypeName))
            {
                CreatePluginType(service, assembly.Id, missingTypeName);
                existingTypeNames.Add(missingTypeName);
            }
        }
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

    internal static void CreatePluginType(IOrganizationService service, Guid pluginAssemblyId, string typeName)
{
    Entity pluginType = new Entity("plugintype");
    pluginType["pluginassemblyid"] = new EntityReference("pluginassembly", pluginAssemblyId);
    pluginType["typename"] = typeName;
    pluginType["name"] = typeName;
    pluginType["friendlyname"] = typeName.Split('.').LastOrDefault() ?? typeName;
    service.Create(pluginType);
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
