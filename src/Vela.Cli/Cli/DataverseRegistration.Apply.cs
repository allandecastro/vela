using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Text.Json;
using System.Threading;
using System.Xml;
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
    internal static void ApplyDiff(IOrganizationService service, RegistrationDiff diff, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, bool verbose, IReadOnlyDictionary<string, string>? secureConfigByStepKey = null, int batchChunkSize = DefaultBatchChunkSize)
{
    // When a solution is given, route every create/update through it so all components (package, assembly, types,
    // steps, images, secure config, Custom APIs and their parameters) are added to it — not just the package.
    IOrganizationService svc = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? service
        : new SolutionScopedOrganizationService(service, solutionUniqueName!);

    List<RegistrationItem> removedSteps = diff.Removed.Where(IsStep).ToList();
    List<RegistrationItem> removedApis = diff.Removed.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
    List<RegistrationItem> addedApis = diff.Added.Where(x => !IsStep(x) && !IsWebResource(x)).ToList();
    List<RegistrationChange> changedApis = diff.Changed.Where(x => string.Equals(x.Kind, "custom-api", StringComparison.OrdinalIgnoreCase)).ToList();
    List<RegistrationItem> addedSteps = diff.Added.Where(IsStep).ToList();
    List<RegistrationChange> changedSteps = diff.Changed.Where(x => string.Equals(x.Kind, "step", StringComparison.OrdinalIgnoreCase)).ToList();

    // Stryker disable all : web resources are a work-in-progress preview, not mutation-gated yet
    // Web resources reconcile independently of plugins/Custom APIs (folder mirror + publish).
    List<RegistrationItem> removedWebResources = diff.Removed.Where(IsWebResource).ToList();
    List<RegistrationItem> addedWebResources = diff.Added.Where(IsWebResource).ToList();
    List<RegistrationChange> changedWebResources = diff.Changed.Where(x => string.Equals(x.Kind, "web-resource", StringComparison.OrdinalIgnoreCase)).ToList();
    // Stryker restore all

    // Ordered list of every operation this apply will perform, used to produce a resume report if
    // the run fails partway through (Dataverse has no cross-entity transaction).
    const string publishOperation = "publish plugin package(s) and resolve plugin types";
    List<string> planned = [];
    planned.AddRange(removedSteps.Select(x => $"delete step {TryGetString(x.Definition, "stepKey") ?? x.Type}"));
    planned.AddRange(removedApis.Select(x => $"delete custom-api {TryGetString(x.Definition, "uniqueName") ?? x.Type}"));
    // A Custom API whose implementing class was renamed (same uniqueName, another plugin type) still references the
    // old plugin type, which the package upload removes. Dataverse refuses to delete a plugin type that a Custom API
    // references, so these APIs are detached before the upload; their update after the upload points them at the
    // new type.
    List<RegistrationChange> reimplementedApis = changedApis.Where(IsCustomApiImplementationChange).ToList();
    planned.AddRange(reimplementedApis.Select(x => $"detach custom-api {TryGetString(x.After, "uniqueName") ?? x.Type} from {TryGetString(x.Before, "pluginTypeName")}"));
    planned.Add(publishOperation);
    planned.AddRange(addedApis.Select(x => $"create custom-api {x.Type}"));
    planned.AddRange(changedApis.Select(x => $"update custom-api {x.Type}"));
    // A plugin type can own several steps, so a step is labelled with its key too: the report names the exact step.
    planned.AddRange(addedSteps.Select(x => $"create step {DescribeStepOperation(x.Type, x.Definition)}"));
    planned.AddRange(changedSteps.Select(x => $"update step {DescribeStepOperation(x.Type, x.After)}"));
    // Stryker disable all : web resources are a work-in-progress preview, not mutation-gated yet
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
    // Stryker restore all

    ApplyProgress progress = new(planned);
    try
    {
        // Delete obsolete steps and custom APIs BEFORE uploading the new package.
        // Dataverse refuses to replace plugin types that still have steps registered,
        // so we must clear them first. Deletes are batched in reverse dependency order
        // (custom-API parameters/response-properties first, then steps and custom APIs).
        ApplyDeletesBatched(service, solutionUniqueName, removedSteps, removedApis, diff.CurrentAssemblies, verbose, batchChunkSize, progress);

        foreach (RegistrationChange change in reimplementedApis)
        {
            DetachCustomApiImplementation(service, change, verbose);
            progress.CompleteNext();
        }

        EnsurePluginPackages(svc, packagePayloads, solutionUniqueName, tempDirectories, verbose);

        HashSet<string> requiredPluginTypes = GetRequiredPluginTypeNames(diff);
        Dictionary<string, Guid> pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, diff.LocalAssemblies);
        // The platform materializes plugin types from the published package, asynchronously. Every local input is a
        // plugin package (the scan reads nothing else), so there is no direct assembly upload to fall back on: wait
        // for the package's types, then fail below if any is still missing.
        if (packagePayloads.Length > 0 && requiredPluginTypes.Any(x => !pluginTypeIds.ContainsKey(x)))
        {
            WaitForRequiredPluginTypes(service, diff.LocalAssemblies, requiredPluginTypes);
            pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, diff.LocalAssemblies);
        }

        string[] missingPluginTypes = requiredPluginTypes
            .Where(x => !pluginTypeIds.ContainsKey(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (missingPluginTypes.Length > 0)
        {
            throw new InvalidOperationException($"Plugin type not found in Dataverse after the plugin package publish: {string.Join(", ", missingPluginTypes)}");
        }

        progress.CompleteNext(); // publish + resolve plugin types

        // When applying into a solution, Custom API unique names must carry the publisher prefix. Resolve it once
        // (only when there is a Custom API to write) so UpsertCustomApi can validate the prefix locally.
        string? customApiSolutionPrefix = (addedApis.Count > 0 || changedApis.Count > 0) && !string.IsNullOrWhiteSpace(solutionUniqueName)
            ? ResolveSolutionCustomizationPrefix(service, solutionUniqueName!)
            : null;

        // Shared across every step in this run so message/filter ids are queried once, not per step.
        StepMetadataCache metadataCache = new StepMetadataCache();

        // Custom APIs and steps are written via chunked ExecuteMultiple, dependency-tiered so a child never precedes
        // its just-created parent: tier 1 upserts the step/custom-API rows (and disables a just-created step, see
        // ApplyUpsertsBatched), tier 2 writes their images, parameters, response properties and secure config.
        ApplyUpsertsBatched(service, solutionUniqueName, pluginTypeIds, addedApis, changedApis, addedSteps, changedSteps,
            customApiSolutionPrefix, secureConfigByStepKey, metadataCache, verbose, batchChunkSize, progress);

        // Stryker disable all : web resources are a work-in-progress preview, not mutation-gated yet
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

            progress.CompleteNext(); // "validate web resource publisher prefix"
        }

        // Web resources: delete removed, upsert added/changed, then publish the writes so they go live. Create/
        // update flow through the solution-scoped service (componenttype 61); publish uses the raw service.
        foreach (RegistrationItem item in removedWebResources)
        {
            DeleteWebResourceByName(service, TryGetString(item.Definition, "name") ?? item.Type, verbose);
            progress.CompleteNext();
        }

        List<Guid> webResourcesToPublish = [];
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? upsertError = null;
        try
        {
            foreach (RegistrationItem item in addedWebResources)
            {
                webResourcesToPublish.Add(UpsertWebResource(svc, item.Definition, verbose));
                progress.CompleteNext();
            }

            foreach (RegistrationChange change in changedWebResources)
            {
                webResourcesToPublish.Add(UpsertWebResource(svc, change.After, verbose));
                progress.CompleteNext();
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
            progress.CompleteNext();
        }
        // Stryker restore all
    }
    catch (Exception ex)
    {
        foreach (string line in progress.BuildReport(OutputWriter.RedactSecrets(ex.Message)))
        {
            Console.Error.WriteLine(line);
        }

        throw;
    }
}

    // True when a changed Custom API keeps its uniqueName but is now implemented by another plugin type (its class
    // was renamed or moved): its live row still points at the old type until it is detached.
    internal static bool IsCustomApiImplementationChange(RegistrationChange change)
    {
        string? before = TryGetString(change.Before, "pluginTypeName");
        return !string.IsNullOrWhiteSpace(before)
            && !string.Equals(before, TryGetString(change.After, "pluginTypeName"), StringComparison.Ordinal);
    }

    // Clears the Custom API's plugintypeid so the package upload can remove the old plugin type. If the apply stops
    // between this and the re-point, the API is left with no implementation; the next apply finds it by uniqueName,
    // sees that nothing implements it (EnsureCustomApiNotOwnedElsewhere allows that) and points it at the new type.
    internal static void DetachCustomApiImplementation(IOrganizationService service, RegistrationChange change, bool verbose)
    {
        string uniqueName = TryGetString(change.After, "uniqueName") ?? change.Type;
        Guid? apiId = FindCustomApiId(service, uniqueName);
        if (!apiId.HasValue)
        {
            return;
        }

        // The environment may have changed since the diff was computed: detach only an API still implemented by the
        // old type the plan saw, never one another solution now owns.
        string expectedType = TryGetString(change.Before, "pluginTypeName") ?? string.Empty;
        EntityReference? implementation = service.Retrieve("customapi", apiId.Value, new ColumnSet("plugintypeid"))?.GetAttributeValue<EntityReference>("plugintypeid");
        if (implementation == null)
        {
            return; // already implemented by nothing: nothing to detach
        }

        string? liveType;
        try
        {
            liveType = service.Retrieve("plugintype", implementation.Id, new ColumnSet("typename"))?.GetAttributeValue<string>("typename");
        }
        catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == ObjectDoesNotExistErrorCode)
        {
            liveType = null; // the implementing type is gone: nothing else owns the API
        }

        if (liveType != null && !string.Equals(liveType, expectedType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Custom API '{uniqueName}' is now implemented by '{liveType}', not by '{expectedType}' as when the plan was computed. " +
                "Vela will not detach it; re-run plan/apply against the current environment.");
        }

        Entity detach = new Entity("customapi", apiId.Value);
        detach["plugintypeid"] = null;
        service.Update(detach);
        LogOperation("custom-api", "detach", uniqueName, $"from {expectedType}", verbose);
    }
    // "Zava.Plugins.AccountPlugin (main)": the plugin type plus the step key, when the step has one.
    internal static string DescribeStepOperation(string pluginType, JsonElement definition) =>
        TryGetString(definition, "stepKey") is { Length: > 0 } key ? $"{pluginType} ({key})" : pluginType;

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

    // Dataverse rejects a plugin-package import whose nuget name has no solution publisher prefix (0x80040265,
    // "The nuget file name does not contain a solution prefix"). Without a --solution to source that prefix, a
    // package whose name isn't already prefixed can't import — fail fast with a clear message instead. Shared by
    // apply (before writing) and plan (so the preview never advertises a create apply would refuse); purely local,
    // so plan can call it without a connection.
    internal static void EnsurePluginPackagesImportable(PluginPackagePayload[] packagePayloads, string? solutionUniqueName)
{
    if (!string.IsNullOrWhiteSpace(solutionUniqueName))
    {
        return;
    }

    PluginPackagePayload? unprefixed = packagePayloads.FirstOrDefault(p => !LooksSolutionPrefixed(p.PackageName));
    if (unprefixed != null)
    {
        throw new InvalidOperationException(
            $"Plugin package '{unprefixed.PackageName}' has no solution publisher prefix, which Dataverse requires " +
            "to import a package. Re-run 'apply' with --solution <uniqueName> so the prefix is applied.");
    }
}

    internal static void EnsurePluginPackages(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories, bool verbose)
{
    // Nothing to publish (a web-resource-only run, or a run that only fixes solution membership): no prefix lookup.
    if (packagePayloads.Length == 0)
    {
        return;
    }

    EnsurePluginPackagesImportable(packagePayloads, solutionUniqueName);

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
    byte[] packageBytes = ReadPackageBytesForImport(package.PackagePath, package.PackageName, desiredPackageName);
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

    // Skip a no-op Update: if the stored package is already this one, updating would only bump modifiedon (Last
    // Modified) and re-import the package on every apply, making it look churned when nothing changed.
    if (PluginPackageMatches(service, existing, packageBytes))
    {
        LogOperation("package", "unchanged", desiredPackageName, $"v{package.Version}", verbose);
        return existing.Id;
    }

    LogOperation("package", "update", desiredPackageName, $"v{package.Version}", verbose);
    Entity update = new Entity("pluginpackage")
    {
        Id = existing.Id
    };
    // Only write content: an Update that sets content and a version other than the stored one fails with
    // "PluginPackage version cannot be updated" (verified live), which used to fail apply mid-run whenever the
    // package version was bumped. The stored version therefore keeps its first value (see PluginPackageMatches).
    update["content"] = packageContent;
    service.Update(update);
    return existing.Id;
}

    // Read-only counterpart of EnsurePluginPackages: true if any plugin package would be created or republished.
    // The registration diff only covers steps/custom APIs, so a code-only change (same step and custom-API
    // definitions, but a rebuilt assembly) is otherwise invisible and apply would report "no changes" and deploy
    // nothing. Mirrors UpsertPluginPackage's create/skip decision exactly (same prefix, same import bytes, same
    // stored-file compare) without writing.
    internal static bool PluginPackagesNeedPublish(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories)
        => ComputePackagePublishActions(service, packagePayloads, solutionUniqueName, tempDirectories).Count > 0;

    // Per-package detail behind PluginPackagesNeedPublish: one action for every package that would be created
    // (absent from Dataverse) or republished (stored file differs); packages whose stored file already matches are
    // omitted. Lets plan list the package operation and flag HasChanges for a
    // code-only rebuild. Mirrors UpsertPluginPackage exactly so plan and apply never disagree: same create/skip
    // decision, same missing-file FileNotFoundException. The importable-prefix guard (EnsurePluginPackagesImportable)
    // runs separately in both paths, so an unprefixed package without --solution never reaches here as a "create".
    internal static IReadOnlyList<PackagePublishAction> ComputePackagePublishActions(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, List<string> tempDirectories)
{
    if (packagePayloads.Length == 0)
    {
        return [];
    }

    string? solutionPrefix = string.IsNullOrWhiteSpace(solutionUniqueName)
        ? null
        : ResolveSolutionCustomizationPrefix(service, solutionUniqueName);

    List<PackagePublishAction> actions = [];
    foreach (PluginPackagePayload package in packagePayloads)
    {
        string desiredPackageName = NormalizePackageNameWithPrefix(package.PackageName, solutionPrefix);

        if (!File.Exists(package.PackagePath))
        {
            // Match UpsertPluginPackage: a missing package file is an error, not a silent "no change". Throwing
            // here keeps plan and apply in step (plan fails with the same message instead of advertising an
            // operation apply cannot perform).
            throw new FileNotFoundException($"Plugin package file not found: {package.PackagePath}", package.PackagePath);
        }

        byte[] packageBytes = ReadPackageBytesForImport(package.PackagePath, package.PackageName, desiredPackageName);
        Entity? existing = GetPluginPackage(service, desiredPackageName);
        if (existing == null)
        {
            actions.Add(new PackagePublishAction(desiredPackageName, IsCreate: true));
            continue;
        }

        // Same decision as UpsertPluginPackage: update unless the stored package is already this one.
        if (!PluginPackageMatches(service, existing, packageBytes))
        {
            actions.Add(new PackagePublishAction(desiredPackageName, IsCreate: false));
        }
    }

    return actions;
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

    internal static byte[] ReadPackageBytesForImport(string packagePath, string originalPackageName, string desiredPackageName)
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

    return RepackWithPackageId(File.ReadAllBytes(packagePath), packagePath, desiredPackageName);
}

    // Rewrites the package id deterministically, in memory: every entry is copied in its original order with its
    // original timestamp, and only the .nuspec is replaced (renamed to the new id, keeping its timestamp). The same
    // input therefore always yields the same bytes, so PluginPackagesNeedPublish sees an unchanged package as
    // unchanged; extracting and re-zipping stamped every entry with the current time, so the package was re-imported
    // on every apply.
    private static readonly XmlWriterSettings NuspecWriterSettings = new()
    {
        Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
    };

    internal static byte[] RepackWithPackageId(byte[] packageBytes, string packagePath, string desiredPackageName)
{
    using MemoryStream input = new(packageBytes, writable: false);
    using ZipArchive source = new(input, ZipArchiveMode.Read);
    ZipArchiveEntry nuspecEntry = source.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No .nuspec found in package '{packagePath}'.");

    XDocument nuspec;
    using (Stream nuspecStream = nuspecEntry.Open())
    {
        nuspec = XDocument.Load(nuspecStream);
    }

    XNamespace ns = nuspec.Root?.Name.Namespace ?? XNamespace.None;
    XElement idElement = nuspec.Root?.Element(ns + "metadata")?.Element(ns + "id")
        ?? throw new InvalidOperationException($"Package '{packagePath}' is missing metadata id in nuspec.");
    idElement.Value = desiredPackageName;

    using MemoryStream output = new();
    using (ZipArchive target = new(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (ZipArchiveEntry entry in source.Entries)
        {
            bool isNuspec = ReferenceEquals(entry, nuspecEntry);
            ZipArchiveEntry copy = target.CreateEntry(isNuspec ? $"{desiredPackageName}.nuspec" : entry.FullName, CompressionLevel.Optimal);
            copy.LastWriteTime = entry.LastWriteTime;
            using Stream destination = copy.Open();
            if (isNuspec)
            {
                // Explicit encoding and newlines: XDocument.Save's defaults follow the platform (CRLF on Windows), so
                // the same package repacked on Linux and Windows runners would differ and be re-imported.
                using XmlWriter writer = XmlWriter.Create(destination, NuspecWriterSettings);
                nuspec.Save(writer);
            }
            else
            {
                using Stream content = entry.Open();
                content.CopyTo(destination);
            }
        }
    }

    return output.ToArray();
}

    // A NuGet-signed package carries a top-level '.signature.p7s' entry (the PKCS#7 signature). Its presence means
    // any modification to the archive (e.g. the prefix-rename repack) invalidates the signature.
    internal static bool IsPackageSigned(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    return archive.GetEntry(".signature.p7s") != null;
}

    // Whether the stored package is already these bytes. Dataverse keeps the uploaded .nupkg in the `package` file
    // column and empties `content` once it has processed it (verified live: content reads back empty right after the
    // create, and the package file is byte-identical to the upload), so the file is what is compared. Its size is
    // checked first, so a changed package is usually told apart without a download. The stored `version` is not
    // compared: a content update leaves it at its first value (verified live: 1.0.0 after 1.0.1 was uploaded), so
    // every plan after a version bump would propose the update again. The bytes include the .nuspec, so a version
    // bump still shows as a change.
    internal static bool PluginPackageMatches(IOrganizationService service, Entity existing, byte[] packageBytes)
{
    InitializeFileBlocksDownloadResponse download;
    try
    {
        download = (InitializeFileBlocksDownloadResponse)service.Execute(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference("pluginpackage", existing.Id),
            FileAttributeName = "package",
        });
    }
    catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == ObjectDoesNotExistErrorCode)
    {
        return false; // no stored file ("No FileAttachment records found"): publishing stores one
    }

    if (download.FileSizeInBytes != packageBytes.LongLength)
    {
        return false;
    }

    long offset = 0;
    while (offset < packageBytes.LongLength)
    {
        long length = Math.Min(PackageDownloadBlockSize, packageBytes.LongLength - offset);
        DownloadBlockResponse block = (DownloadBlockResponse)service.Execute(new DownloadBlockRequest
        {
            FileContinuationToken = download.FileContinuationToken,
            Offset = offset,
            BlockLength = length,
        });
        byte[] data = block.Data ?? [];
        if (data.Length == 0 || data.Length > length || !data.AsSpan().SequenceEqual(packageBytes.AsSpan((int)offset, data.Length)))
        {
            return false;
        }

        offset += data.Length;
    }

    return true;
}

    // The largest block DownloadBlock serves (4 MB).
    private const long PackageDownloadBlockSize = 4L * 1024 * 1024;

    internal static Entity? GetPluginPackage(IOrganizationService service, string packageName)
{
    QueryExpression query = new QueryExpression("pluginpackage")
    {
        // Neither content nor version is read: content reads back empty and version keeps its first value
        // (PluginPackageMatches compares the stored file instead).
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
