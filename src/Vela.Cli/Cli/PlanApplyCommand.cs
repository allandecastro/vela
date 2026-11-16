using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.DiffEngine;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>An open Dataverse connection: the service plus whatever owns it (a <c>ServiceClient</c>).</summary>
internal sealed class OrganizationConnection(IOrganizationService service, IDisposable? lifetime = null, string? url = null) : IDisposable
{
    public IOrganizationService Service { get; } = service;

    /// <summary>The organization URL the connection reached, when known; the apply prompt names it.</summary>
    public string? Url { get; } = url;

    public void Dispose() => lifetime?.Dispose();
}

/// <summary>
/// The side effects of <see cref="PlanApplyCommand"/>, injectable so the orchestration and its exit codes are tested
/// offline (a fake organization service, a canned local state, a scripted confirmation).
/// </summary>
internal sealed class PlanApplyHost
{
    public static PlanApplyHost Default { get; } = new();
    /// <summary>Builds the local state from the plugin packages: the package scan, in a child process that does not
    /// share the CLI's connection, token or secrets (IsolatedPackageScan; not a sandbox).</summary>
    public Func<SharedOptions, JsonSerializerOptions, List<string>, RegistrationState> ScanLocal { get; init; } =
        IsolatedPackageScan.Scan;

    /// <summary>Opens the one connection the whole run uses.</summary>
    public Func<SharedOptions, OrganizationConnection> Connect { get; init; } = options =>
    {
        Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
        return new OrganizationConnection(client, client, client.ConnectedOrgUriActual?.GetLeftPart(UriPartial.Authority));
    };

    public Func<bool> IsInputRedirected { get; init; } = () => Console.IsInputRedirected;

    public Func<bool> IsOutputRedirected { get; init; } = () => Console.IsOutputRedirected;

    public Func<string?> ReadLine { get; init; } = Console.ReadLine;
}

/// <summary>
/// The one orchestration path behind <c>plan</c>, <c>apply</c> and <c>inspect</c>: scan the local packages, open one
/// connection, read and scope the live state, resolve secure configs, diff, run the live checks the diff cannot express
/// (package publish, Custom API publisher prefix, step message and filter), gate on <c>--strict</c>, then emit (plan / inspect) or confirm,
/// apply and re-read (apply). Exit codes are part of the contract (see <see cref="CliExitCodes"/>).
/// <para>
/// Output contract: stdout carries only the result (JSON when piped / <c>--json</c>, or the human summary on a
/// terminal); every notice, warning and the <c>--verbose</c> summary go to stderr, so <c>--verbose --json</c> still
/// writes the JSON document.
/// </para>
/// </summary>
internal static class PlanApplyCommand
{
    internal const string PlanMode = "plan";
    internal const string ApplyMode = "apply";
    internal const string InspectMode = "inspect";

    // The last step of apply, after every write: the environment is read again to check that it converged.
    internal const string ReadBackOperation = "read the environment back to check that it converged";

    public static int Run(string command, SharedOptions options, JsonSerializerOptions jsonOptions, PlanApplyHost? host = null)
    {
        host ??= PlanApplyHost.Default;
        // The command-specific parser already rejects it; this guards a caller that built the options itself.
        if (options.DetailedExitCode && command != PlanMode)
        {
            return Fail("--detailed-exitcode applies to 'vela plan' only.", ExitInvalidArguments, includeUsage: false);
        }

        if (options.ManagedIdentityId.HasValue && command == InspectMode)
        {
            return Fail("--managed-identity applies to 'vela plan' and 'vela apply' only.", ExitInvalidArguments, includeUsage: false);
        }

        List<string> tempDirectories = [];
        try
        {
            // The scan runs before any connection is opened.
            RegistrationState local = host.ScanLocal(options, jsonOptions, tempDirectories);
            using OrganizationConnection connection = ConsoleStatus.Run("Connecting to Dataverse…", () => host.Connect(options));
            IOrganizationService service = connection.Service;

            // plan / apply refuse a --solution that is missing or managed in the target environment (the wrong
            // environment), before reading or writing anything. inspect is read-only and runs anywhere.
            if (command != InspectMode && !string.IsNullOrWhiteSpace(options.SolutionUniqueName))
            {
                DataverseRegistration.EnsureSolutionIsUnmanaged(service, options.SolutionUniqueName!, options.Environment);
            }

            RegistrationState current = ReadCurrent(service, local, options, jsonOptions, out string[] unmanaged, withRetired: command != InspectMode);
            local = ResolveSecureConfigs(local, current, options, jsonOptions);

            return command switch
            {
                InspectMode => EmitInspect(local, current, unmanaged, options, jsonOptions, host),
                PlanMode => RunPlan(service, local, current, unmanaged, options, jsonOptions, host, tempDirectories),
                _ => RunApply(service, connection.Url, local, current, unmanaged, options, jsonOptions, host, tempDirectories),
            };
        }
        catch (Exception ex) when (DataverseConnectionException.IsConnectionFailure(ex))
        {
            return FailWithException(ex, ExitConnectionFailure, options.Verbose);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FailWithException(ex, ExitFileNotFound, options.Verbose);
        }
        catch (Exception ex)
        {
            return FailWithException(ex, ExitError, options.Verbose);
        }
        finally
        {
            PackageScanner.CleanupTempDirectories(tempDirectories);
        }
    }

    // Reads the live state of the local assemblies and scopes it to what Vela owns (OwnershipScope). plan and apply
    // also read the retired assemblies of the run's packages (a renamed assembly), whose steps block the package update.
    private static RegistrationState ReadCurrent(IOrganizationService service, RegistrationState local, SharedOptions options, JsonSerializerOptions jsonOptions, out string[] unmanaged, bool withRetired)
    {
        RegistrationState current = ConsoleStatus.Run(
            "Reading current registrations…",
            report => DataverseRegistration.ReadDataverseState(service, local.SourceAssemblies, options, jsonOptions, DataverseRegistration.ExtractLocalRankSteps(local), report));
        RegistrationState scoped = OwnershipScope.ExcludeUnmanaged(current, local.UnmanagedPluginTypes, out unmanaged);
        return withRetired
            ? DataverseRegistration.WithRetiredAssemblies(service, scoped, local, options.SolutionUniqueName, jsonOptions)
            : scoped;
    }

    // Code only names a step's secure configuration; its value comes from --secure-config or VELA_SECURE_CONFIG_<NAME>.
    // Resolved against the live state so a configuration Vela was given no value for keeps its live digest (no diff,
    // never written). See SecureConfigResolver.
    private static RegistrationState ResolveSecureConfigs(RegistrationState local, RegistrationState current, SharedOptions options, JsonSerializerOptions jsonOptions) =>
        SecureConfigResolver.Resolve(
            local,
            current,
            SecureConfigResolver.ParseSpecs(options.SecureConfigs ?? [], File.ReadAllText, Console.Error.WriteLine),
            Environment.GetEnvironmentVariable,
            jsonOptions,
            Console.Error.WriteLine);

    // The preview shared by plan and apply: the registration diff plus the live checks it cannot express.
    //  1. Package publish: a code-only rebuild (unchanged steps/custom APIs, rebuilt assembly) produces no diff item,
    //     yet apply republishes the package - listed as a "package" operation so neither plan nor apply reports "no
    //     changes". The importable-prefix guard runs first, so an unprefixed package without --solution fails before
    //     anything is written rather than mid-apply.
    //  2. Blocking errors: apply would fail on them after its deletes and the package publish, so the preview refuses
    //     the run (exit 4) before the confirmation prompt and before any write, listing every one of them:
    //     - a Custom API unique name without the solution publisher prefix (Dataverse rejects it). Resolved only when a
    //       solution is targeted AND a Custom API is created or updated, so a step-only or delete-only run neither
    //       pays for nor can fail on the lookup;
    //     - an added or changed step whose message, or whose table's message filter, does not exist
    //       (FindUnregistrableSteps; the message of a Custom API this run creates is exempt);
    //     - a plugin type the package update removes (its class was deleted or renamed, or its assembly was renamed)
    //       that still has steps the run does not delete first: Dataverse refuses the update (0x80040265), so a step
    //       Vela does not manage stops the run here (FindPluginTypeRemovalBlockers). Vela's own steps on that type are
    //       in the delete phase, which runs before the publish.
    internal static PlanPreview BuildPreview(string mode, IOrganizationService service, RegistrationState local, RegistrationState current, string[] unmanaged, SharedOptions options, List<string> tempDirectories, out RegistrationDiff diff)
    {
        diff = BuildDiff(local, current);

        List<string> info = [.. local.Info ?? [], .. current.Info ?? []];
        IReadOnlyList<PackagePublishAction> packageActions = [];
        if (local.PackagePayloads.Length > 0)
        {
            DataverseRegistration.EnsurePluginPackagesImportable(local.PackagePayloads, options.SolutionUniqueName);
            packageActions = DataverseRegistration.ComputePackagePublishActions(service, local.PackagePayloads, options.SolutionUniqueName, tempDirectories, info);
        }

        List<string> blocking = [];
        string[] customApiNames = string.IsNullOrWhiteSpace(options.SolutionUniqueName) ? [] : ExtractCustomApiUniqueNames(diff).ToArray();
        if (customApiNames.Length > 0)
        {
            string solutionPrefix = DataverseRegistration.ResolveSolutionCustomizationPrefix(service, options.SolutionUniqueName!);
            blocking.AddRange(DataverseRegistration.FindCustomApiPrefixViolations(solutionPrefix, customApiNames));
        }

        blocking.AddRange(DataverseRegistration.FindUnregistrableSteps(service, diff));
        blocking.AddRange(DataverseRegistration.FindPluginTypeRemovalBlockers(service, local, current, diff, packageActions.Any(a => !a.IsCreate)));
        if (blocking.Count > 0)
        {
            throw new InvalidOperationException(BlockingErrorsMessage(blocking));
        }

        IReadOnlyList<SolutionMembershipAction> membership = OnlyUnwritten(MissingFromSolution(service, local, options, info), diff, packageActions.Count > 0);
        string[] currentDiagnostics = [.. current.Diagnostics, .. DataverseRegistration.RetiredSecureConfigWarnings(local, current)];
        return BuildPlanPreview(mode, options.Environment, diff, local.Diagnostics, currentDiagnostics, packageActions, unmanaged, [.. info], membership,
            ReadManagedIdentityLinks(service, local, options, validate: true));
    }

    // 4. Managed identity link (--managed-identity only): each published package's pluginpackage.managedidentityid.
    //    Without the option the link is neither read nor written. The record is validated before the plan (validate);
    //    apply's convergence re-read skips that.
    private static ManagedIdentityPlan? ReadManagedIdentityLinks(IOrganizationService service, RegistrationState local, SharedOptions options, bool validate)
    {
        if (options.ManagedIdentityId is not { } managedIdentityId)
        {
            return null;
        }

        if (validate)
        {
            DataverseRegistration.EnsureManagedIdentityExists(service, managedIdentityId);
        }

        return new ManagedIdentityPlan(managedIdentityId,
            DataverseRegistration.ComputeManagedIdentityLinks(service, local.PackagePayloads, options.SolutionUniqueName, managedIdentityId));
    }

    // 3. Solution membership (--solution only): a component that did not change is never written, so it was never
    //    added to the solution by the SolutionUniqueName request parameter. Listed as add-to-solution operations.
    private static IReadOnlyList<SolutionMembershipAction> MissingFromSolution(IOrganizationService service, RegistrationState local, SharedOptions options, List<string> info) =>
        string.IsNullOrWhiteSpace(options.SolutionUniqueName)
            ? []
            : DataverseRegistration.ComputeSolutionMembershipActions(service, options.SolutionUniqueName!, local.SourceAssemblies, local.PackagePayloads, local.UnmanagedPluginTypes, info);

    // Keeps the add-to-solution actions for components apply does not otherwise write: a changed step or Custom API
    // joins the solution through its solution-scoped update, a removed one (and its parameters) is deleted, and a
    // published package is imported into the solution. Listing those too would advertise a duplicate write.
    internal static IReadOnlyList<SolutionMembershipAction> OnlyUnwritten(IReadOnlyList<SolutionMembershipAction> actions, RegistrationDiff diff, bool packageWritten)
    {
        if (actions.Count == 0)
        {
            return actions;
        }

        HashSet<string> writtenSteps = new(
            diff.Removed.Where(IsStep).Select(i => JsonReader.TryGetString(i.Definition, "name"))
                .Concat(diff.Changed.Where(c => c.Kind == "step").Select(c => JsonReader.TryGetString(c.Before, "name")))
                .Where(n => !string.IsNullOrWhiteSpace(n))!,
            StringComparer.Ordinal);
        HashSet<string> removedApis = new(
            diff.Removed.Where(i => i.Kind == "custom-api").Select(i => JsonReader.TryGetString(i.Definition, "uniqueName") ?? i.Type),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> writtenApis = new(
            diff.Changed.Where(c => c.Kind == "custom-api").Select(c => JsonReader.TryGetString(c.Before, "uniqueName") ?? c.Type),
            StringComparer.OrdinalIgnoreCase);
        writtenApis.UnionWith(removedApis);

        return actions.Where(action => action.Kind switch
        {
            "step" => !writtenSteps.Contains(action.Name),
            "custom-api" => !writtenApis.Contains(action.Name),
            "custom-api-parameter" or "custom-api-response-property" => action.Parent == null || !removedApis.Contains(action.Parent),
            "package" => !packageWritten,
            _ => true,
        }).ToArray();
    }

    private static int RunPlan(IOrganizationService service, RegistrationState local, RegistrationState current, string[] unmanaged, SharedOptions options, JsonSerializerOptions jsonOptions, PlanApplyHost host, List<string> tempDirectories)
    {
        PlanPreview plan = BuildPreview("plan", service, local, current, unmanaged, options, tempDirectories, out _);
        if (FailsStrict(options, plan.Diagnostics, afterApply: false, out int strictExit))
        {
            return strictExit;
        }

        Emit(plan, options, jsonOptions, host);
        return options.DetailedExitCode && plan.HasChanges ? ExitChangesPending : ExitSuccess;
    }

    private static int RunApply(IOrganizationService service, string? orgUrl, RegistrationState local, RegistrationState current, string[] unmanaged, SharedOptions options, JsonSerializerOptions jsonOptions, PlanApplyHost host, List<string> tempDirectories)
    {
        PlanPreview preview = BuildPreview("apply", service, local, current, unmanaged, options, tempDirectories, out RegistrationDiff diff);
        if (FailsStrict(options, preview.Diagnostics, afterApply: false, out int strictExit))
        {
            return strictExit;
        }

        // Only a package create/update republishes it; an add-to-solution of the package is not a publish.
        bool packageNeedsPublish = preview.Operations.Any(o => o.Kind == "package" && o.Action is "create" or "update");
        int linksToWrite = preview.ManagedIdentity?.Pending.Length ?? 0;
        if (!preview.HasChanges)
        {
            const string nothing = "No changes detected; nothing executed.";
            PlanPreview unchanged = preview with
            {
                Mode = "apply-execute",
                Notes = [.. preview.Notes, nothing],
                Executed = ExecutedCounts.FromDiff(diff, packageRepublished: false),
            };
            Emit(unchanged, options, jsonOptions, host, humanFooter: writer =>
                writer.WriteLine(ConsoleTheme.Success(nothing, ConsoleTheme.StdoutColor)));
            return ExitSuccess;
        }

        // Warn about a cross-solution assembly overwrite BEFORE the confirmation gate, so an interactive user can
        // decline knowing the risk (and an auto-approved run still gets the heads-up). The registered assembly name
        // is the DLL's (unique per org), so two projects that build the same-named assembly collide.
        if (!string.IsNullOrWhiteSpace(options.SolutionUniqueName) && local.SourceAssemblyPayloads.Length > 0)
        {
            foreach (string name in DataverseRegistration.FindCrossSolutionAssemblyCollisions(service, local.SourceAssemblyPayloads, options.SolutionUniqueName!))
            {
                Console.Error.WriteLine(EscapeControl(
                    $"Warning: plugin assembly '{name}' already exists in this org and is not part of solution '{options.SolutionUniqueName}'. " +
                    "Applying overwrites its content, modifying an assembly owned by another solution. " +
                    "Set a unique <AssemblyName> in the plugins project (for example prefix it with the solution name) to avoid the collision."));
            }
        }

        // Confirmation gate: apply writes by default. In a terminal, show the plan and require a typed "yes". In a
        // non-interactive session refuse unless --auto-approve/--yes was passed, so a script never writes unattended.
        switch (ApplyConfirmation.Decide(options.AutoApprove, host.IsInputRedirected(), () =>
        {
            WritePlanSummary(preview, Console.Error, ConsoleTheme.StderrColor);
            string packageNote = packageNeedsPublish ? " + plugin package (code change)" : string.Empty;
            string linkNote = linksToWrite > 0 ? $" + {linksToWrite} managed identity link(s)" : string.Empty;
            Console.Error.Write(
                $"Apply {preview.Summary.Added} to create / {preview.Summary.Changed} to update / {preview.Summary.Removed} to delete{packageNote}{linkNote} against {EscapeControl(DescribeTarget(options.Environment, orgUrl))}? Type 'yes' to continue: ");
            return host.ReadLine();
        }))
        {
            case ApplyDecision.RefuseNonInteractive:
                return Fail(
                    "Refusing to apply without confirmation in a non-interactive session. Re-run with --auto-approve (or --yes), or use 'vela plan' to preview.",
                    ExitError, includeUsage: false);
            case ApplyDecision.Cancel:
                Console.Error.WriteLine("Apply cancelled.");
                return ExitSuccess;
        }

        int addedToSolution;
        RegistrationState after;
        RegistrationDiff afterDiff;
        ManagedIdentityPlan? afterLinks;
        ApplyRun run = new();
        try
        {
            DataverseRegistration.ApplyDiff(service, diff, local.PackagePayloads, options.SolutionUniqueName, tempDirectories, options.Verbose, local.SecureConfigByStepKey, options.BatchChunkSize ?? DataverseRegistration.DefaultBatchChunkSize, preview.ManagedIdentity, run);

            // Recomputed after the writes: what apply created or updated is already in the solution, a deleted
            // component is gone, and what remains is the unchanged components to add.
            if (!string.IsNullOrWhiteSpace(options.SolutionUniqueName))
            {
                run.Pending = $"add unchanged components to solution {options.SolutionUniqueName}";
            }

            IReadOnlyList<SolutionMembershipAction> missing = OnlyUnwritten(MissingFromSolution(service, local, options, []), diff, packageNeedsPublish);
            if (missing.Count > 0)
            {
                DataverseRegistration.AddComponentsToSolution(service, options.SolutionUniqueName!, missing, options.Verbose);
            }

            addedToSolution = missing.Count;
            run.CompletePending();

            // The read-back is part of the run: every write already landed, so a failure here still ends with the
            // apply-failed document and the partial-apply exit, listing the writes as completed.
            run.Pending = ReadBackOperation;
            after = ReadCurrent(service, local, options, jsonOptions, out _, withRetired: true);
            afterDiff = BuildDiff(local, after);
            // The managed identity link is read again too: a link the platform did not keep is not a success either.
            afterLinks = ReadManagedIdentityLinks(service, local, options, validate: false);
        }
        catch (Exception applyEx) when (applyEx is not DataverseConnectionException)
        {
            // Dataverse has no cross-entity transaction, so a mid-run failure may leave the org partially updated.
            // ApplyDiff already wrote the resume report to stderr; add a machine-readable result and a distinct exit
            // code so a partial apply is not confused with a bad-args or connection failure.
            // The document lists what landed (failure.completed), what failed and what was not attempted, the same lists
            // as the resume report on stderr.
            string error = RedactSecrets(applyEx.Message);
            ApplyFailure outcome = run.ToFailure(error);
            if (run.Pending != null)
            {
                // A failure after ApplyDiff (the solution add or the read-back): ApplyDiff wrote no report for it.
                foreach (string line in ApplyResumeReport.Build(outcome.Failed, error, outcome.Completed, outcome.NotApplied))
                {
                    Console.Error.WriteLine(EscapeControl(line));
                }
            }

            PlanPreview failure = preview with
            {
                Mode = "apply-failed",
                Notes = [.. preview.Notes, "Apply failed mid-run; Dataverse may be left partially updated. Re-run 'apply' to reconcile.", error],
                Failure = outcome,
            };
            if (!WantsHumanOutput(options, host) || !string.IsNullOrWhiteSpace(options.OutPath))
            {
                WriteOutput(options.OutPath, JsonSerializer.Serialize(failure, jsonOptions));
            }

            return Fail($"Apply failed mid-run; Dataverse may be partially updated: {applyEx.Message}", ExitPartialApply, includeUsage: false);
        }

        // Every write succeeded, yet the environment can still differ (the platform silently ignored or normalized a
        // value). That is not a success: the next apply would plan the same change again.
        int linksRemaining = afterLinks?.Pending.Length ?? 0;
        bool converged = afterDiff.Summary.Added == 0 && afterDiff.Summary.Changed == 0 && afterDiff.Summary.Removed == 0 && linksRemaining == 0;
        PlanPreview result = BuildPlanPreview(converged ? "apply-execute" : "apply-not-converged", options.Environment, afterDiff, local.Diagnostics, after.Diagnostics, unmanaged: unmanaged, info: [.. local.Info ?? [], .. after.Info ?? []], managedIdentity: afterLinks) with
        {
            Notes =
            [
                "Compared local .nupkg plugin metadata with live Dataverse state.",
                "Dataverse changes were executed.",
                $"Executed operations: create={diff.Added.Length}, update={diff.Changed.Length}, delete={diff.Removed.Length}",
                .. converged ? Array.Empty<string>() : ["The environment still differs from the packages after apply; summary and diff show what did not converge."],
            ],
            // What was DONE, from the pre-apply diff: Summary above is the post-apply remaining diff (0 on success).
            Executed = ExecutedCounts.FromDiff(diff, packageNeedsPublish) with { AddedToSolution = addedToSolution, LinkedToManagedIdentity = linksToWrite },
        };

        if (FailsStrict(options, result.Diagnostics, afterApply: true, out strictExit))
        {
            return strictExit;
        }

        Emit(result, options, jsonOptions, host, humanBody: writer =>
        {
            bool color = ConsoleTheme.StdoutColor;
            writer.WriteLine(converged ? ConsoleTheme.Success("Apply complete.", color) : "Apply finished, but did not converge.");
            writer.WriteLine(
                $"Executed against {options.Environment ?? "Dataverse"}: {diff.Added.Length} created, " +
                $"{diff.Changed.Length} updated, {diff.Removed.Length} deleted" +
                (packageNeedsPublish ? ", plugin package re-published" : string.Empty) +
                (addedToSolution > 0 ? $", {addedToSolution} component(s) added to solution '{options.SolutionUniqueName}'" : string.Empty) +
                (linksToWrite > 0 ? $", {linksToWrite} plugin package(s) linked to managed identity {options.ManagedIdentityId}" : string.Empty) + ".");
            if (!converged)
            {
                writer.WriteLine("The environment still shows differences after apply:");
                WritePlanSummary(result, writer, color);
            }
            else
            {
                // Rank collisions are not resolved by applying, so still surface diagnostics and the info notices.
                WriteManagedIdentity(result.ManagedIdentity, writer, color);
                WriteDiagnostics(result.Diagnostics, writer, color);
                WriteUnmanaged(result.Unmanaged, writer, color);
                WriteInfo(result.Info, writer, color);
            }
        });

        return converged
            ? ExitSuccess
            : Fail(
                $"Apply did not converge: {afterDiff.Summary.Added} to create / {afterDiff.Summary.Changed} to update / {afterDiff.Summary.Removed} to delete" +
                (linksRemaining > 0 ? $" / {linksRemaining} managed identity link(s)" : string.Empty) + " remain. " +
                "Dataverse accepted the writes but did not keep them; re-running apply would plan the same changes.",
                ExitDriftDetected, includeUsage: false);
    }

    // The target the apply prompt names: the --environment label with the URL the connection reached, so a label (or a
    // connection string, which leaves no --url) cannot hide which organization is about to be written.
    internal static string DescribeTarget(string? label, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.IsNullOrWhiteSpace(label) ? "Dataverse" : label!;
        }

        return string.IsNullOrWhiteSpace(label) || string.Equals(label.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{label} ({url})";
    }

    private static bool FailsStrict(SharedOptions options, string[] diagnostics, bool afterApply, out int exitCode)
    {
        exitCode = ExitSuccess;
        if (!options.Strict || diagnostics.Length == 0)
        {
            return false;
        }

        WriteDiagnostics(diagnostics, Console.Error, ConsoleTheme.StderrColor);
        exitCode = Fail($"Strict mode failed with {diagnostics.Length} diagnostic(s){(afterApply ? " after apply" : string.Empty)}.", ExitError, includeUsage: false);
        return true;
    }

    private static int EmitInspect(RegistrationState local, RegistrationState current, string[] unmanaged, SharedOptions options, JsonSerializerOptions jsonOptions, PlanApplyHost host)
    {
        static object[] Shape(RegistrationState state, Func<RegistrationItem, bool> filter) => state.Items.Where(filter)
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => (object)new { x.Kind, x.Type, definition = x.Definition })
            .ToArray();

        static bool IsCustomApi(RegistrationItem x) => !IsStep(x) && !IsWebResource(x);

        var output = new
        {
            schemaVersion = MachineOutput.SchemaVersion,
            mode = "inspect",
            environment = options.Environment,
            local = new { assemblies = local.SourceAssemblies, steps = Shape(local, IsStep), customApis = Shape(local, IsCustomApi) },
            current = new { assemblies = current.SourceAssemblies, steps = Shape(current, IsStep), customApis = Shape(current, IsCustomApi) },
            unmanaged,
            diagnostics = local.Diagnostics.Concat(current.Diagnostics).ToArray(),
            info = (local.Info ?? []).Concat(current.Info ?? []).ToArray(),
        };

        void Human(TextWriter writer, bool color) => WriteInspectSummary(local, current, options.Environment, writer, color, unmanaged);
        if (WantsHumanOutput(options, host))
        {
            Human(Console.Out, ConsoleTheme.StdoutColor);
            return ExitSuccess;
        }

        if (options.Verbose)
        {
            Human(Console.Error, ConsoleTheme.StderrColor);
        }

        WriteOutput(options.OutPath, JsonSerializer.Serialize(output, jsonOptions));
        return ExitSuccess;
    }

    // Writes a plan/apply result: the human summary on stdout on a terminal (humanBody overrides it, humanFooter is
    // appended), otherwise the JSON document on stdout or in --out. --verbose adds the human summary on stderr and
    // never replaces the JSON.
    private static void Emit(PlanPreview preview, SharedOptions options, JsonSerializerOptions jsonOptions, PlanApplyHost host, Action<TextWriter>? humanBody = null, Action<TextWriter>? humanFooter = null)
    {
        if (WantsHumanOutput(options, host))
        {
            if (humanBody != null)
            {
                humanBody(Console.Out);
            }
            else
            {
                WritePlanSummary(preview, Console.Out, ConsoleTheme.StdoutColor);
            }

            humanFooter?.Invoke(Console.Out);
            return;
        }

        if (options.Verbose)
        {
            WritePlanSummary(preview, Console.Error, ConsoleTheme.StderrColor);
        }

        WriteOutput(options.OutPath, JsonSerializer.Serialize(preview, jsonOptions));
    }

    // Whether to print the styled human summary on stdout instead of JSON: only on a real terminal with neither --json
    // nor --out. Piped/redirected runs, --json and file output all keep JSON for scripts and agents.
    internal static bool WantsHumanOutput(SharedOptions options, PlanApplyHost host) =>
        !options.Json && string.IsNullOrWhiteSpace(options.OutPath) && !host.IsOutputRedirected();

    // One message for every blocking error of the preview: the first alone, or a count and one line per error.
    internal static string BlockingErrorsMessage(IReadOnlyList<string> errors) =>
        errors.Count == 1
            ? $"{errors[0]} Nothing was written."
            : $"{errors.Count} problems block this run; nothing was written:{Environment.NewLine}" +
              string.Join(Environment.NewLine, errors.Select(e => "  - " + e));

    // The unique names of the Custom APIs a plan would create or update (removed ones need no prefix), read from the
    // diff's local-side definitions so the publisher-prefix check sees exactly what apply would write.
    internal static IEnumerable<string> ExtractCustomApiUniqueNames(RegistrationDiff diff)
    {
        IEnumerable<JsonElement> definitions = diff.Added.Where(item => item.Kind == "custom-api").Select(item => item.Definition)
            .Concat(diff.Changed.Where(change => change.Kind == "custom-api").Select(change => change.After));

        foreach (JsonElement definition in definitions)
        {
            if (definition.ValueKind == JsonValueKind.Object
                && definition.TryGetProperty("uniqueName", out JsonElement uniqueName)
                && uniqueName.ValueKind == JsonValueKind.String
                && uniqueName.GetString() is { Length: > 0 } value
                && !string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }
}
