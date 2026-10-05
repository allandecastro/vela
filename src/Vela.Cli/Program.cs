using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml.Linq;
using Vela.Cli;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DefinitionNormalizer;
using static Vela.Cli.DiffEngine;
using static Vela.Cli.OutputWriter;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.PackageScanner;
using static Vela.Cli.DataverseRegistration;

JsonSerializerOptions jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

string[] effectiveArgs = args;

// A bare `vela` (or explicit `vela menu`) launches the interactive Spectre menu when attached to a real
// terminal. When stdin is redirected (CI, pipes), bare `vela` keeps printing usage so scripts are unaffected.
bool wantsMenu = effectiveArgs.Length == 0 || string.Equals(effectiveArgs[0], "menu", StringComparison.OrdinalIgnoreCase);
if (wantsMenu)
{
    if (Console.IsInputRedirected)
    {
        PrintUsage();
        return effectiveArgs.Length == 0 ? ExitInvalidArguments : ExitSuccess;
    }

    string[]? chosen = InteractiveMenu.Prompt();
    if (chosen is null)
    {
        return ExitSuccess;
    }

    effectiveArgs = chosen;
}

string command = effectiveArgs[0].ToLowerInvariant();

// `vela help`, `vela --help`, `vela -h` all print usage and succeed.
if (command is "help" or "--help" or "-h" or "-?" or "/?")
{
    PrintUsage();
    return ExitSuccess;
}

// `vela version` / `vela --version` print the tool version (plain, script-friendly).
if (command is "version" or "--version")
{
    Console.WriteLine($"{ToolInfo.ToolName} {ToolInfo.Version}");
    return ExitSuccess;
}

string[] commandArgs = effectiveArgs.Skip(1).ToArray();

int exitCode = command switch
{
    "plan" => RunPlan(commandArgs, jsonOptions),
    "apply" => RunApply(commandArgs, jsonOptions),
    "inspect" => RunInspect(commandArgs, jsonOptions),
    "init" or "new" => RunNew(commandArgs),
    "add" => AddCommand.Run(commandArgs),
    "models" => ModelsCommand.Run(commandArgs),
    "logs" or "trace" => LogsCommand.Run(commandArgs),
    "solution" => SolutionCommand.Run(commandArgs),
    "sign" => SignCommand.Run(commandArgs),
    "logout" => LogoutCommand.Run(commandArgs),
    "update" => UpdateCommand.Run(commandArgs),
    _ => Fail($"Unknown command '{effectiveArgs[0]}'.", ExitInvalidArguments)
};

// After the command runs, best-effort notice if a newer CLI is on nuget.org (throttled, silent for --json / dev
// builds / VELA_NO_UPDATE_CHECK). Never affects the command's exit code.
UpdateNotifier.MaybeNotify(ToolInfo.Version, commandArgs.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase)));

return exitCode;

// `init` scaffolds a new solution on disk. Unlike plan/apply/inspect it never connects to Dataverse,
// so it has its own option parser instead of TryParseSharedOptions.
static int RunNew(string[] args)
{
    if (IsHelpRequested(args))
    {
        PrintUsage();
        return ExitSuccess;
    }

    if (!ScaffoldOptions.TryParse(args, out ScaffoldOptions options, out int exitCode))
    {
        return exitCode;
    }

    // Unless the caller pinned a version, scaffold against the latest published framework resolved from
    // nuget.org, so the CLI and framework can release on independent tags without the scaffold going stale.
    // Falls back to the built-in default when offline.
    if (!args.Any(x => string.Equals(x, "--core-version", StringComparison.OrdinalIgnoreCase)))
    {
        string? latest = ScaffoldOptions.ResolveLatestFrameworkVersion();
        if (latest != null)
        {
            options = options with { CoreVersion = latest };
            Console.WriteLine($"Scaffolding against Vela.Plugins {latest} (latest on nuget.org; override with --core-version).");
        }
        else
        {
            Console.WriteLine($"Could not reach nuget.org; scaffolding against Vela.Plugins {options.CoreVersion} (built-in default). Override with --core-version.");
        }
    }

    bool verbose = args.Any(x => string.Equals(x, "--verbose", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "-v", StringComparison.OrdinalIgnoreCase));
    try
    {
        return ProjectScaffolder.Generate(options);
    }
    catch (Exception ex)
    {
        return FailWithException(ex, ExitError, verbose);
    }
}

static int RunPlan(string[] args, JsonSerializerOptions jsonOptions)
{
    if (IsHelpRequested(args))
    {
        PrintUsage();
        return ExitSuccess;
    }

    if (!TryParseSharedOptions(args, out SharedOptions options, out int exitCode))
    {
        return exitCode;
    }

    return ExecutePlanLike("plan", options, jsonOptions);
}

static int RunApply(string[] args, JsonSerializerOptions jsonOptions)
{
    if (IsHelpRequested(args))
    {
        PrintUsage();
        return ExitSuccess;
    }

    if (!TryParseSharedOptions(args, out SharedOptions options, out int exitCode))
    {
        return exitCode;
    }

    // apply executes by default. The confirmation gate (interactive prompt / --auto-approve requirement in a
    // non-interactive session) lives in ExecuteApply, after the plan is computed. Use `vela plan` to preview.
    return ExecuteApply(options, jsonOptions);
}

static int RunInspect(string[] args, JsonSerializerOptions jsonOptions)
{
    if (IsHelpRequested(args))
    {
        PrintUsage();
        return ExitSuccess;
    }

    if (!TryParseSharedOptions(args, out SharedOptions options, out int exitCode))
    {
        return exitCode;
    }

    return ExecuteInspect(options, jsonOptions);
}

static int ExecuteInspect(SharedOptions options, JsonSerializerOptions jsonOptions)
{
    List<string> tempDirectories = [];
    try
    {
        RegistrationState local = BuildLocalStateFromPackages(options, jsonOptions, tempDirectories);
        RegistrationState current = ConsoleStatus.Run(
            "Connecting to Dataverse and reading current registrations…",
            report => BuildDataverseState(local.SourceAssemblies, options, jsonOptions, ExtractLocalRankSteps(local), report));

        var localSteps = local.Items.Where(IsStep)
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => new { x.Kind, x.Type, definition = x.Definition })
            .ToArray();

        var currentSteps = current.Items.Where(IsStep)
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => new { x.Kind, x.Type, definition = x.Definition })
            .ToArray();

        var localCustomApis = local.Items.Where(x => !IsStep(x) && !IsWebResource(x))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => new { x.Kind, x.Type, definition = x.Definition })
            .ToArray();

        var currentCustomApis = current.Items.Where(x => !IsStep(x) && !IsWebResource(x))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => new { x.Kind, x.Type, definition = x.Definition })
            .ToArray();

        var output = new
        {
            mode = "inspect",
            environment = options.Environment,
            local = new { assemblies = local.SourceAssemblies, steps = localSteps, customApis = localCustomApis },
            current = new { assemblies = current.SourceAssemblies, steps = currentSteps, customApis = currentCustomApis },
            diagnostics = local.Diagnostics.Concat(current.Diagnostics).ToArray()
        };

        if (WantsHumanOutput(options))
        {
            WriteInspectSummary(local, current, options.Environment, Console.Out, ConsoleTheme.StdoutColor);
            return ExitSuccess;
        }

        if (options.Verbose)
        {
            WriteInspectSummary(local, current, options.Environment, Console.Error, ConsoleTheme.StderrColor);
            if (string.IsNullOrWhiteSpace(options.OutPath))
            {
                return ExitSuccess;
            }
        }

        WriteOutput(options.OutPath, JsonSerializer.Serialize(output, jsonOptions));
        return ExitSuccess;
    }
    catch (DataverseConnectionException ex)
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
        CleanupTempDirectories(tempDirectories);
    }
}

static int ExecuteApply(SharedOptions options, JsonSerializerOptions jsonOptions)
{
    List<string> tempDirectories = [];

    try
    {
        RegistrationState local = BuildLocalStateFromPackages(options, jsonOptions, tempDirectories);
        RegistrationState current = ConsoleStatus.Run(
            "Connecting to Dataverse and reading current registrations…",
            report => BuildDataverseState(local.SourceAssemblies, options, jsonOptions, ExtractLocalRankSteps(local), report));
        RegistrationDiff diff = BuildDiff(local, current);
        PlanPreview preview = BuildPlanPreview("apply-execute-preview", options.Environment, diff, local.Diagnostics, current.Diagnostics);
        if (options.Strict && preview.Diagnostics.Length > 0)
        {
            WriteDiagnostics(preview.Diagnostics, Console.Error, ConsoleTheme.StderrColor);
            return Fail($"Strict mode failed with {preview.Diagnostics.Length} diagnostic(s).", ExitError, includeUsage: false);
        }

        using ServiceClient client = CreateDataverseClient(options);
        IOrganizationService service = client;

        // A code-only change (unchanged step/custom-API definitions but a rebuilt assembly) is invisible to the
        // registration diff, so also check whether the plugin package content differs from what is deployed - the
        // assembly must still be re-published even when the step/custom-API diff is empty.
        bool packageNeedsPublish = PluginPackagesNeedPublish(service, local.PackagePayloads, options.SolutionUniqueName, tempDirectories);

        if (!preview.HasChanges && !packageNeedsPublish)
        {
            if (WantsHumanOutput(options))
            {
                WritePlanSummary(preview with { Mode = "apply-execute" }, Console.Out, ConsoleTheme.StdoutColor);
                Console.Out.WriteLine(ConsoleTheme.Success("No changes detected; nothing executed.", ConsoleTheme.StdoutColor));
                return ExitSuccess;
            }

            if (options.Verbose)
            {
                WritePlanSummary(preview with
                {
                    Mode = "apply-execute"
                }, Console.Error, ConsoleTheme.StderrColor);
                Console.Error.WriteLine("no changes detected");
                if (string.IsNullOrWhiteSpace(options.OutPath))
                {
                    return ExitSuccess;
                }
            }

            string unchanged = JsonSerializer.Serialize(preview with
            {
                Mode = "apply-execute",
                Notes = [.. preview.Notes, "No changes detected; nothing executed."],
                Executed = ExecutedCounts.FromDiff(diff, packageRepublished: false)
            }, jsonOptions);
            WriteOutput(options.OutPath, unchanged);
            return ExitSuccess;
        }

        // Warn about a cross-solution assembly overwrite BEFORE the confirmation gate, so an interactive user can
        // decline knowing the risk (and an auto-approved / non-interactive run still gets the heads-up). The
        // registered assembly name is the DLL's (unique per org), so two projects that build the same-named
        // assembly collide; if one already exists outside the target solution, applying rewrites its content there.
        if (!string.IsNullOrWhiteSpace(options.SolutionUniqueName) && local.SourceAssemblyPayloads.Length > 0)
        {
            foreach (string name in DataverseRegistration.FindCrossSolutionAssemblyCollisions(service, local.SourceAssemblyPayloads, options.SolutionUniqueName!))
            {
                Console.Error.WriteLine(
                    $"Warning: plugin assembly '{name}' already exists in this org and is not part of solution '{options.SolutionUniqueName}'. " +
                    "Applying overwrites its content, modifying an assembly owned by another solution. " +
                    "Set a unique <AssemblyName> in the plugins project (for example prefix it with the solution name) to avoid the collision.");
            }
        }

        // Confirmation gate: apply writes by default. In a terminal, show the plan and require a typed "yes".
        // In a non-interactive session (no stdin TTY) refuse unless --auto-approve/--yes was passed, so a
        // script can never write unattended by accident. The decision is a pure, unit-tested helper; the prompt
        // side effects live in the closure, which only runs in the interactive case.
        switch (ApplyConfirmation.Decide(options.AutoApprove, Console.IsInputRedirected, () =>
        {
            WritePlanSummary(preview with { Mode = "apply" }, Console.Error, ConsoleTheme.StderrColor);
            // Surface a code-only republish (changed assembly with an otherwise-empty diff) so the prompt is not
            // a misleading "0 to create / 0 to update / 0 to delete".
            string packageNote = packageNeedsPublish ? " + plugin package (code change)" : string.Empty;
            Console.Error.Write(
                $"Apply {preview.Summary.Added} to create / {preview.Summary.Changed} to update / {preview.Summary.Removed} to delete{packageNote} against {options.Environment ?? "Dataverse"}? Type 'yes' to continue: ");
            return Console.ReadLine();
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

        try
        {
            ApplyDiff(service, diff, local.SourceAssemblyPayloads, local.PackagePayloads, options.SolutionUniqueName, tempDirectories, options.Verbose, local.SecureConfigByStepKey, options.BatchChunkSize ?? DataverseRegistration.DefaultBatchChunkSize);
        }
        catch (Exception applyEx) when (applyEx is not DataverseConnectionException)
        {
            // Dataverse has no cross-entity transaction, so a mid-run failure may leave the org partially updated.
            // ApplyDiff already wrote the human-readable resume report to stderr; add a machine-readable result (for
            // --json / --out callers) and a distinct exit code so a partial apply is not confused with a bad-args or
            // connection failure. Already-applied writes are idempotent, so a re-run reconciles the rest.
            PlanPreview failure = preview with
            {
                Mode = "apply-failed",
                Notes = [.. preview.Notes, "Apply failed mid-run; Dataverse may be left partially updated. Re-run 'apply' to reconcile.", applyEx.Message]
            };
            if (!WantsHumanOutput(options) || !string.IsNullOrWhiteSpace(options.OutPath))
            {
                WriteOutput(options.OutPath, JsonSerializer.Serialize(failure, jsonOptions));
            }

            return Fail($"Apply failed mid-run; Dataverse may be partially updated: {applyEx.Message}", ExitPartialApply, includeUsage: false);
        }

        RegistrationState after = ConsoleStatus.Run(
            "Re-reading Dataverse state…",
            report => BuildDataverseState(local.SourceAssemblies, options, jsonOptions, ExtractLocalRankSteps(local), report));
        RegistrationDiff afterDiff = BuildDiff(local, after);
        PlanPreview result = BuildPlanPreview("apply-execute", options.Environment, afterDiff, local.Diagnostics, after.Diagnostics) with
        {
            Notes =
            [
                "Compared local .nupkg plugin metadata with live Dataverse state.",
                "Dataverse changes were executed.",
                $"Executed operations: create={diff.Added.Length}, update={diff.Changed.Length}, delete={diff.Removed.Length}"
            ],
            // Structured executed counts (totals + per-kind) from the PRE-apply diff (Summary above is the
            // post-apply remaining = 0 on success), so an agent reads what was done without parsing Notes.
            Executed = ExecutedCounts.FromDiff(diff, packageNeedsPublish)
        };

        if (options.Strict && result.Diagnostics.Length > 0)
        {
            WriteDiagnostics(result.Diagnostics, Console.Error, ConsoleTheme.StderrColor);
            return Fail($"Strict mode failed with {result.Diagnostics.Length} diagnostic(s) after apply.", ExitError, includeUsage: false);
        }

        if (WantsHumanOutput(options))
        {
            Console.Out.WriteLine(ConsoleTheme.Success("Apply complete.", ConsoleTheme.StdoutColor));
            // Report what was EXECUTED (from the pre-apply diff), not the post-apply re-plan - that re-plan is
            // "+0 -0 ~0" precisely because the apply succeeded, which read as "nothing happened".
            string executed =
                $"Executed against {options.Environment ?? "Dataverse"}: {diff.Added.Length} created, " +
                $"{diff.Changed.Length} updated, {diff.Removed.Length} deleted" +
                (packageNeedsPublish ? ", plugin package re-published" : string.Empty) + ".";
            Console.Out.WriteLine(executed);
            if (result.HasChanges)
            {
                // Unexpected: the environment still differs after a successful apply - surface it.
                Console.Out.WriteLine("Note: the environment still shows differences after apply:");
                WritePlanSummary(result, Console.Out, ConsoleTheme.StdoutColor);
            }
            else
            {
                // A clean apply skips the summary above, but rank collisions are not resolved by applying,
                // so still surface any diagnostics (WritePlanSummary already includes them when it runs).
                WriteDiagnostics(result.Diagnostics, Console.Out, ConsoleTheme.StdoutColor);
            }

            return ExitSuccess;
        }

        if (options.Verbose)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Apply complete.");
            WritePlanSummary(result, Console.Error, ConsoleTheme.StderrColor);
            if (string.IsNullOrWhiteSpace(options.OutPath))
            {
                return ExitSuccess;
            }
        }

        string output = JsonSerializer.Serialize(result, jsonOptions);
        WriteOutput(options.OutPath, output);
        return ExitSuccess;
    }
    catch (DataverseConnectionException ex)
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
        CleanupTempDirectories(tempDirectories);
    }
}

// The unique names of the Custom APIs a plan would create or update (removed ones need no prefix). Read from the
// diff's local-side definitions so the plan-time publisher-prefix check sees exactly what apply would write.
static IEnumerable<string> ExtractCustomApiUniqueNames(RegistrationDiff diff)
{
    IEnumerable<JsonElement> definitions = diff.Added.Where(item => item.Kind == "custom-api").Select(item => item.Definition)
        .Concat(diff.Changed.Where(change => change.Kind == "custom-api").Select(change => change.After));

    foreach (JsonElement definition in definitions)
    {
        if (definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty("uniqueName", out JsonElement uniqueName)
            && uniqueName.ValueKind == JsonValueKind.String)
        {
            string? value = uniqueName.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value!;
            }
        }
    }
}

static int ExecutePlanLike(string mode, SharedOptions options, JsonSerializerOptions jsonOptions)
{
    List<string> tempDirectories = [];

    try
    {
        RegistrationState local = BuildLocalStateFromPackages(options, jsonOptions, tempDirectories);
        RegistrationState current = ConsoleStatus.Run(
            "Connecting to Dataverse and reading current registrations…",
            report => BuildDataverseState(local.SourceAssemblies, options, jsonOptions, ExtractLocalRankSteps(local), report));
        RegistrationDiff diff = BuildDiff(local, current);

        // A code-only change (unchanged step/custom-API definitions but a rebuilt assembly) is invisible to the
        // registration diff, so also surface whether each plugin package would be created or republished - exactly
        // what apply checks - so plan does not report "no changes" for a rebuild that apply would deploy. The same
        // importable-prefix guard apply uses runs first (local, no connection) so plan fails the same way apply
        // would on an unprefixed package without --solution rather than advertising an impossible create. Skips the
        // (short-lived) connection entirely when there are no local packages to compare.
        // When applying into a solution, a Custom API unique name without the publisher prefix is rejected by apply.
        // Surface that mismatch at plan time as a warning (and fail under --strict) so it is caught before apply,
        // not discovered by it. Resolved on the same short-lived connection as the package check.
        IReadOnlyList<PackagePublishAction> packageActions = [];
        string[] prefixDiagnostics = [];
        bool checksPackages = local.PackagePayloads.Length > 0;
        bool checksSolutionPrefix = !string.IsNullOrWhiteSpace(options.SolutionUniqueName);
        if (checksPackages || checksSolutionPrefix)
        {
            if (checksPackages)
            {
                EnsurePluginPackagesImportable(local.PackagePayloads, options.SolutionUniqueName);
            }

            using ServiceClient planClient = CreateDataverseClient(options);
            if (checksPackages)
            {
                packageActions = ComputePackagePublishActions(planClient, local.PackagePayloads, options.SolutionUniqueName, tempDirectories);
            }

            if (checksSolutionPrefix)
            {
                string solutionPrefix = ResolveSolutionCustomizationPrefix(planClient, options.SolutionUniqueName!);
                prefixDiagnostics = ComputeCustomApiPrefixDiagnostics(solutionPrefix, ExtractCustomApiUniqueNames(diff)).ToArray();
            }
        }

        string[] planDiagnostics = prefixDiagnostics.Length == 0
            ? local.Diagnostics
            : [.. local.Diagnostics, .. prefixDiagnostics];
        PlanPreview plan = BuildPlanPreview(mode, options.Environment, diff, planDiagnostics, current.Diagnostics, packageActions);
        if (options.Strict && plan.Diagnostics.Length > 0)
        {
            WriteDiagnostics(plan.Diagnostics, Console.Error, ConsoleTheme.StderrColor);
            return Fail($"Strict mode failed with {plan.Diagnostics.Length} diagnostic(s).", ExitError, includeUsage: false);
        }

        // On an interactive terminal (no --out, no --json), the styled human summary is the primary output on
        // stdout in place of JSON. Piped/redirected/--json/--out runs keep emitting JSON so scripts and agents
        // are unaffected.
        if (WantsHumanOutput(options))
        {
            WritePlanSummary(plan, Console.Out, ConsoleTheme.StdoutColor);
            return ExitSuccess;
        }

        if (options.Verbose)
        {
            WritePlanSummary(plan, Console.Error, ConsoleTheme.StderrColor);
            if (string.IsNullOrWhiteSpace(options.OutPath))
            {
                return ExitSuccess;
            }
        }

        string output = JsonSerializer.Serialize(plan, jsonOptions);
        WriteOutput(options.OutPath, output);

        return ExitSuccess;
    }
    catch (DataverseConnectionException ex)
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
        CleanupTempDirectories(tempDirectories);
    }
}

// Whether plan/apply/inspect should print a styled human summary to stdout instead of JSON. True only on a
// real terminal with neither --json nor --out: piped/redirected runs, --json, and file output all keep JSON so
// scripts and coding agents parse the same contract they always have.
static bool WantsHumanOutput(SharedOptions options) =>
    !options.Json && string.IsNullOrWhiteSpace(options.OutPath) && !Console.IsOutputRedirected;

// This file is the CLI entry point and command orchestration only. The implementation lives in the
// Vela.Cli namespace: RegistrationModels, CliExitCodes, JsonReader,
// DefinitionNormalizer, DiffEngine, OutputWriter, ApplyResumeReport, CommandLineOptions,
// PackageScanner, and DataverseRegistration (Dataverse state reads + apply).
