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
    internal static void ApplyDiff(IOrganizationService service, RegistrationDiff diff, AssemblyPayload[] sourceAssemblyPayloads, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, bool verbose, IReadOnlyDictionary<string, string>? secureConfigByStepKey = null, int batchChunkSize = DefaultBatchChunkSize)
{
    // When a solution is given, route every create/update through it so all components (package, assembly, types,
    // steps, images, secure config, Custom APIs and their parameters) are added to it — not just the package.
    IOrganizationService svc = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? service
        : new SolutionScopedOrganizationService(service, solutionUniqueName!);

    // Heads-up before overwriting an assembly owned by another solution. The registered name is the DLL's assembly
    // name (unique per org), so two projects that build the same-named assembly collide; if one already exists and
    // is not in the target solution, this apply rewrites its content under that other solution.
    if (!string.IsNullOrWhiteSpace(solutionUniqueName) && sourceAssemblyPayloads.Length > 0)
    {
        foreach (string name in FindCrossSolutionAssemblyCollisions(service, sourceAssemblyPayloads, solutionUniqueName!))
        {
            Console.Error.WriteLine(
                $"Warning: plugin assembly '{name}' already exists in this org and is not part of solution '{solutionUniqueName}'. " +
                "Applying overwrites its content, modifying an assembly owned by another solution. " +
                "Set a unique <AssemblyName> in the plugins project (for example prefix it with the solution name) to avoid the collision.");
        }
    }

    List<RegistrationItem> removedSteps = diff.Removed.Where(IsStep).ToList();
    List<RegistrationItem> removedApis = diff.Removed.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
    List<RegistrationItem> addedApis = diff.Added.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
    List<RegistrationChange> changedApis = diff.Changed.Where(x => string.Equals(x.Kind, "custom-api", StringComparison.OrdinalIgnoreCase)).ToList();
    List<RegistrationItem> addedSteps = diff.Added.Where(IsStep).ToList();
    List<RegistrationChange> changedSteps = diff.Changed.Where(x => string.Equals(x.Kind, "step", StringComparison.OrdinalIgnoreCase)).ToList();

    // Web resources reconcile independently of plugins/Custom APIs (folder mirror + publish).
    List<RegistrationItem> removedWebResources = diff.Removed.Where(IsWebResource).ToList();
    List<RegistrationItem> addedWebResources = diff.Added.Where(IsWebResource).ToList();
    List<RegistrationChange> changedWebResources = diff.Changed.Where(x => string.Equals(x.Kind, "web-resource", StringComparison.OrdinalIgnoreCase)).ToList();

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
    bool hasWebResourceWrites = addedWebResources.Count > 0 || changedWebResources.Count > 0;
    bool validatesWebResourcePrefix = hasWebResourceWrites && !string.IsNullOrWhiteSpace(solutionUniqueName);
    if (validatesWebResourcePrefix)
    {
        planned.Add("validate web resource publisher prefix");
    }

    planned.AddRange(removedWebResources.Select(x => $"delete web-resource {x.Type}"));
    planned.AddRange(addedWebResources.Select(x => $"create web-resource {x.Type}"));
    planned.AddRange(changedWebResources.Select(x => $"update web-resource {x.Type}"));
    if (hasWebResourceWrites)
    {
        planned.Add("publish web resource(s)");
    }

    int done = 0;
    try
    {
        // Delete obsolete steps and custom APIs BEFORE uploading the new package.
        // Dataverse refuses to replace plugin types that still have steps registered,
        // so we must clear them first. Deletes are batched in reverse dependency order
        // (custom-API parameters/response-properties first, then steps and custom APIs).
        ApplyDeletesBatched(service, solutionUniqueName, removedSteps, removedApis, verbose, batchChunkSize, ref done);

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

        // When applying into a solution, Custom API unique names must carry the publisher prefix. Resolve it once
        // (only when there is a Custom API to write) so UpsertCustomApi can validate the prefix locally.
        string? customApiSolutionPrefix = (addedApis.Count > 0 || changedApis.Count > 0) && !string.IsNullOrWhiteSpace(solutionUniqueName)
            ? ResolveSolutionCustomizationPrefix(service, solutionUniqueName!)
            : null;

        // Shared across every step in this run so message/filter ids are queried once, not per step.
        StepMetadataCache metadataCache = new StepMetadataCache();

        // Custom APIs and steps are written via chunked ExecuteMultiple, dependency-tiered so a child never precedes
        // its just-created parent: tier 1 upserts the step/custom-API rows, tier 2 writes their images, parameters,
        // response properties, secure config and state.
        ApplyUpsertsBatched(service, solutionUniqueName, pluginTypeIds, addedApis, changedApis, addedSteps, changedSteps,
            customApiSolutionPrefix, secureConfigByStepKey, metadataCache, verbose, batchChunkSize, ref done);

        // Safeguard: every web resource name must start with the solution's publisher prefix (its top-level folder),
        // or Dataverse rejects the create. Validate once, up front, before any web-resource mutation.
        if (validatesWebResourcePrefix)
        {
            string prefix = ResolveSolutionCustomizationPrefix(service, solutionUniqueName!);
            string[] wrongPrefix = FindWebResourcesWithWrongPrefix(
                addedWebResources.Select(x => TryGetString(x.Definition, "name") ?? x.Type)
                    .Concat(changedWebResources.Select(x => TryGetString(x.After, "name") ?? x.Type)),
                prefix);
            if (wrongPrefix.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Web resource name(s) must start with the solution publisher prefix '{prefix}_' (the top-level folder is the prefix): {string.Join(", ", wrongPrefix)}");
            }

            done++; // "validate web resource publisher prefix"
        }

        // Web resources: delete removed, upsert added/changed, then publish the writes so they go live. Create/
        // update flow through the solution-scoped service (componenttype 61); publish uses the raw service.
        foreach (RegistrationItem item in removedWebResources)
        {
            DeleteWebResourceByName(service, TryGetString(item.Definition, "name") ?? item.Type, verbose);
            done++;
        }

        List<Guid> webResourcesToPublish = [];
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? upsertError = null;
        try
        {
            foreach (RegistrationItem item in addedWebResources)
            {
                webResourcesToPublish.Add(UpsertWebResource(svc, item.Definition, verbose));
                done++;
            }

            foreach (RegistrationChange change in changedWebResources)
            {
                webResourcesToPublish.Add(UpsertWebResource(svc, change.After, verbose));
                done++;
            }
        }
        catch (Exception upsertEx)
        {
            // Capture, but still publish what was already committed below: a created/updated web resource left
            // unpublished is invisible/stale, so publishing the committed writes is the safer half-applied state.
            upsertError = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(upsertEx);
        }

        if (webResourcesToPublish.Count > 0)
        {
            PublishWebResources(service, webResourcesToPublish);
        }

        if (upsertError != null)
        {
            upsertError.Throw();
        }

        if (hasWebResourceWrites)
        {
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

    // Dataverse error code for the "Invalid plug-in registration stage" validation (captured live: the Web API
    // returns error code 0x80044184 for a step create/update at MainOperation). Matching on the code is
    // locale-independent, unlike the message text.
    private const int InvalidPluginRegistrationStageErrorCode = unchecked((int)0x80044184);

    internal static bool IsIgnorableCustomApiStageException(Exception ex)
{
    // Only a genuine Dataverse platform fault for the "invalid plug-in registration stage"
    // validation is ignorable (the custom-API main-operation step is managed by the platform).
    // Gating on FaultException<OrganizationServiceFault> ensures a generic programming error whose
    // message merely contains the phrase is never silently swallowed. We match on the locale-independent
    // error code first, falling back to the message text (older SDKs / wrapped faults without a Detail).
    // We walk the InnerException chain because ServiceClient often wraps the fault in an outer exception.
    Exception? current = ex;
    while (current != null)
    {
        if (current is FaultException<OrganizationServiceFault> fault &&
            (fault.Detail?.ErrorCode == InvalidPluginRegistrationStageErrorCode
                || (fault.Message ?? string.Empty).Contains("Invalid plug-in registration stage", StringComparison.OrdinalIgnoreCase)))
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
                "to import a package. Re-run 'apply' with --solution <uniqueName> so the prefix is applied.");
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

    // Skip a no-op Update: if the stored version and content already match, updating would only bump
    // modifiedon (Last Modified) on every apply, making the package look churned when nothing changed.
    string existingVersion = existing.GetAttributeValue<string>("version") ?? string.Empty;
    string existingContent = existing.GetAttributeValue<string>("content") ?? string.Empty;
    if (string.Equals(existingVersion, package.Version, StringComparison.Ordinal) &&
        string.Equals(existingContent, packageContent, StringComparison.Ordinal))
    {
        LogOperation("package", "unchanged", desiredPackageName, $"v{package.Version}", verbose);
        return existing.Id;
    }

    LogOperation("package", "update", desiredPackageName, $"v{package.Version}", verbose);
    Entity update = new Entity("pluginpackage")
    {
        Id = existing.Id
    };
    // Only write content: Dataverse derives the pluginpackage version from the uploaded .nupkg and rejects an
    // explicit write of the version field on an existing package ("PluginPackage version cannot be updated"),
    // which used to fail apply mid-run whenever the package version was bumped.
    update["content"] = packageContent;
    service.Update(update);
    return existing.Id;
}

    // Read-only counterpart of EnsurePluginPackages: true if any plugin package would be created or have its
    // content updated. The registration diff only covers steps/custom APIs, so a code-only change (same step and
    // custom-API definitions, but a rebuilt assembly) is otherwise invisible and apply would report "no changes"
    // and deploy nothing. This mirrors UpsertPluginPackage's create/skip decision exactly (same prefix, same
    // import bytes, same content compare) without writing.
    internal static bool PluginPackagesNeedPublish(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories)
{
    if (packagePayloads.Length == 0)
    {
        return false;
    }

    string? solutionPrefix = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? null
        : ResolveSolutionCustomizationPrefix(service, solutionUniqueName);

    foreach (PluginPackagePayload package in packagePayloads)
    {
        if (!File.Exists(package.PackagePath))
        {
            // Let the real publish surface the missing-file error rather than silently short-circuiting.
            return true;
        }

        string desiredPackageName = NormalizePackageNameWithPrefix(package.PackageName, solutionPrefix);
        byte[] packageBytes = ReadPackageBytesForImport(package.PackagePath, package.PackageName, desiredPackageName, tempDirectories);
        string packageContent = Convert.ToBase64String(packageBytes);
        Entity? existing = GetPluginPackage(service, desiredPackageName);
        if (existing == null)
        {
            return true;
        }

        string existingContent = existing.GetAttributeValue<string>("content") ?? string.Empty;
        if (!string.Equals(existingContent, packageContent, StringComparison.Ordinal))
        {
            return true;
        }
    }

    return false;
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

    // Applying with --solution injects the publisher prefix by rewriting the package id in the .nuspec and
    // repacking. That repack changes the archive content, which invalidates any NuGet author signature: Dataverse
    // then rejects the upload with 0x80040265 "the package is signed but its signature does not cover the package
    // content". Detect a signed package up front and fail with an actionable message instead of a cryptic
    // platform error. (The package must be named with the prefix BEFORE signing, or applied without --solution.)
    if (IsPackageSigned(packagePath))
    {
        throw new InvalidOperationException(
            $"Plugin package '{originalPackageName}' is signed, but applying it with a solution requires renaming it " +
            $"to '{desiredPackageName}' (to add the publisher prefix), which would invalidate the signature and be " +
            "rejected by Dataverse (0x80040265). Build/sign the package already named with the solution publisher " +
            $"prefix ('{desiredPackageName}'), or apply without --solution.");
    }

    string tempRoot = Path.Combine(Path.GetTempPath(), "vela-tools", Guid.NewGuid().ToString("N"));
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

    string outputDirectory = Path.Combine(Path.GetTempPath(), "vela-tools", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDirectory);
    tempDirectories.Add(outputDirectory);
    string transformedPackagePath = Path.Combine(outputDirectory, $"{desiredPackageName}.nupkg");
    ZipFile.CreateFromDirectory(tempRoot, transformedPackagePath);
    return File.ReadAllBytes(transformedPackagePath);
}

    // A NuGet-signed package carries a top-level '.signature.p7s' entry (the PKCS#7 signature). Its presence means
    // any modification to the archive (e.g. the prefix-rename repack) invalidates the signature.
    internal static bool IsPackageSigned(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    return archive.GetEntry(".signature.p7s") != null;
}

    internal static Entity? GetPluginPackage(IOrganizationService service, string packageName)
{
    QueryExpression query = new QueryExpression("pluginpackage")
    {
        // version + content are read so UpsertPluginPackage can skip a no-op Update (which would otherwise
        // bump modifiedon on every apply even when nothing changed).
        ColumnSet = new ColumnSet("pluginpackageid", "name", "version", "content"),
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

    // Resolve the existing plugin-type ids once, not once per required type: the lookup issues a query pair per
    // assembly, so calling it inside the Where predicate was O(requiredTypes x assemblies) round-trips.
    Dictionary<string, Guid> existingTypeIds = GetPluginTypeIdByNameForAssemblies(
        service, sourceAssemblyPayloads.Select(p => p.AssemblyName).ToArray());
    HashSet<string> missingPluginTypes = new HashSet<string>(
        requiredPluginTypes.Where(x => !existingTypeIds.ContainsKey(x)),
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

    // Returns the names of source assemblies that already exist in the org but are NOT part of the target solution.
    // The registered pluginassembly name is the DLL's assembly name (unique per org), so two projects that build the
    // same-named assembly collide: applying one rewrites the other's content. This is the signal for that collision -
    // an assembly that exists and belongs elsewhere. Pure over the service, so it is testable with a fake org.
    internal static List<string> FindCrossSolutionAssemblyCollisions(IOrganizationService service, AssemblyPayload[] payloads, string solutionUniqueName)
    {
        List<string> collisions = new List<string>();
        foreach (AssemblyPayload payload in payloads)
        {
            Entity? existing = GetPluginAssembly(service, payload.AssemblyName);
            if (existing != null && !IsAssemblyInSolution(service, existing.Id, solutionUniqueName))
            {
                collisions.Add(payload.AssemblyName);
            }
        }

        return collisions;
    }

    private static bool IsAssemblyInSolution(IOrganizationService service, Guid assemblyId, string solutionUniqueName)
    {
        QueryExpression query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("solutioncomponentid"),
            TopCount = 1,
        };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, assemblyId);
        LinkEntity solution = query.AddLink("solution", "solutionid", "solutionid");
        solution.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);
        return service.RetrieveMultiple(query).Entities.Count > 0;
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

    // Skip a pure no-op update: identical content AND version would only bump modifiedon (re-uploading the same
    // version is also a no-op for type registration, since Dataverse caches assemblies by version). Any real
    // change to either still updates. The "new type + same version" stale-assembly trap is handled separately by
    // GuardAgainstStaleAssemblyVersion.
    Entity current = service.Retrieve("pluginassembly", existing.Id, new ColumnSet("content", "version"));
    if (string.Equals(current.GetAttributeValue<string>("content") ?? string.Empty, content, StringComparison.Ordinal) &&
        string.Equals(current.GetAttributeValue<string>("version") ?? string.Empty, version, StringComparison.Ordinal))
    {
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
