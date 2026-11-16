using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DiffEngine;


namespace Vela.Cli;

/// <summary> Applies a registration diff: the ApplyDiff orchestration (ordering, resume report) and the Custom API detach before the publish. </summary>
internal static partial class DataverseRegistration
{
    internal static void ApplyDiff(IOrganizationService service, RegistrationDiff diff, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, bool verbose, IReadOnlyDictionary<string, string>? secureConfigByStepKey = null, int batchChunkSize = DefaultBatchChunkSize, ManagedIdentityPlan? managedIdentity = null, ApplyRun? run = null)
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
    // --managed-identity: linked right after the publish, so a package this apply creates is linked too.
    ManagedIdentityPackageLink[] managedIdentityLinks = managedIdentity?.Pending ?? [];
    planned.AddRange(managedIdentityLinks.Select(link => DescribeManagedIdentityLink(link.Package, managedIdentity!.Id)));
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
    if (run != null)
    {
        run.Progress = progress;
    }
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

        EnsurePluginPackages(svc, packagePayloads, solutionUniqueName, verbose);

        HashSet<string> requiredPluginTypes = GetRequiredPluginTypeNames(diff);
        Dictionary<string, Guid> pluginTypeIds = GetPluginTypeIdByNameForAssemblies(service, diff.LocalAssemblies);
        // The package import writes its plugin types in the same request (verified live: the plugintype rows exist as
        // soon as the publish returns), so a type missing here is normally missing from the package itself. The poll
        // below is a bounded safety net (60 s) for a slower import, not a wait the platform requires; the types still
        // missing after it fail the run. It does not cover the ~15 s after an update during which executions may still
        // run the previous build (see troubleshooting).
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

        foreach (ManagedIdentityPackageLink link in managedIdentityLinks)
        {
            LinkPluginPackageToManagedIdentity(svc, service, link.Package, managedIdentity!.Id, verbose);
            progress.CompleteNext();
        }

        // When applying into a solution, Custom API unique names must carry the publisher prefix. Resolve it once
        // (only when there is a Custom API to write) so UpsertCustomApi can validate the prefix locally.
        string? customApiSolutionPrefix = (addedApis.Count > 0 || changedApis.Count > 0) && !string.IsNullOrWhiteSpace(solutionUniqueName)
            ? ResolveSolutionCustomizationPrefix(service, solutionUniqueName!)
            : null;

        // Shared across every step in this run so message/filter ids are queried once, not per step.
        StepMetadataCache metadataCache = new StepMetadataCache();

        // Custom APIs and steps are written via chunked ExecuteMultiple: first the Custom APIs, then the steps (a step
        // can register on the message of a Custom API this run creates). Each is dependency-tiered so a child never
        // precedes its just-created parent: tier 1 upserts the step/custom-API rows (and disables a just-created step,
        // see ApplyUpsertsBatched), tier 2 writes their images, parameters, response properties and secure config.
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
            Console.Error.WriteLine(OutputWriter.EscapeControl(line));
        }

        throw;
    }
}

    // True when a changed Custom API keeps its uniqueName but is now implemented by another plugin type (its class
    // was renamed or moved): its live row still points at the old type until it is detached.
    // A Custom API of a retired assembly moves to the local type even when the class kept its name.
    internal static bool IsCustomApiImplementationChange(RegistrationChange change)
    {
        string? before = TryGetString(change.Before, "pluginTypeName");
        return !string.IsNullOrWhiteSpace(before)
            && (change.MovesFromRetiredAssembly || !string.Equals(before, TryGetString(change.After, "pluginTypeName"), StringComparison.Ordinal));
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
}
