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
    "sign" => SignCommand.Run(commandArgs),
    "logout" => LogoutCommand.Run(commandArgs),
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

    if (options.Execute)
    {
        return ExecuteApply(options, jsonOptions);
    }

    return ExecutePlanLike("apply-dry-run", options, jsonOptions);
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
            () => BuildDataverseState(local.SourceAssemblies, options, jsonOptions));

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
            () => BuildDataverseState(local.SourceAssemblies, options, jsonOptions));
        RegistrationDiff diff = BuildDiff(local, current);
        PlanPreview preview = BuildPlanPreview("apply-execute-preview", options.Environment, diff, local.Diagnostics, current.Diagnostics);
        if (options.Strict && preview.Diagnostics.Length > 0)
        {
            return Fail($"Strict mode failed with {preview.Diagnostics.Length} diagnostic(s).", ExitError, includeUsage: false);
        }

        if (!preview.HasChanges)
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
                Notes = [.. preview.Notes, "No changes detected; nothing executed."]
            }, jsonOptions);
            WriteOutput(options.OutPath, unchanged);
            return ExitSuccess;
        }

        using ServiceClient client = CreateDataverseClient(options);
        IOrganizationService service = client;
        ApplyDiff(service, diff, local.SourceAssemblyPayloads, local.PackagePayloads, options.SolutionUniqueName, tempDirectories, options.Verbose, local.SecureConfigByStepKey);

        RegistrationState after = ConsoleStatus.Run(
            "Re-reading Dataverse state…",
            () => BuildDataverseState(local.SourceAssemblies, options, jsonOptions));
        RegistrationDiff afterDiff = BuildDiff(local, after);
        PlanPreview result = BuildPlanPreview("apply-execute", options.Environment, afterDiff, local.Diagnostics, after.Diagnostics) with
        {
            Notes =
            [
                "Compared local .nupkg plugin metadata with live Dataverse state.",
                "Dataverse changes were executed.",
                $"Executed operations: create={diff.Added.Length}, update={diff.Changed.Length}, delete={diff.Removed.Length}"
            ]
        };

        if (options.Strict && result.Diagnostics.Length > 0)
        {
            return Fail($"Strict mode failed with {result.Diagnostics.Length} diagnostic(s) after apply.", ExitError, includeUsage: false);
        }

        if (WantsHumanOutput(options))
        {
            Console.Out.WriteLine(ConsoleTheme.Success("Apply complete.", ConsoleTheme.StdoutColor));
            WritePlanSummary(result, Console.Out, ConsoleTheme.StdoutColor);
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

static int ExecutePlanLike(string mode, SharedOptions options, JsonSerializerOptions jsonOptions)
{
    List<string> tempDirectories = [];

    try
    {
        RegistrationState local = BuildLocalStateFromPackages(options, jsonOptions, tempDirectories);
        RegistrationState current = ConsoleStatus.Run(
            "Connecting to Dataverse and reading current registrations…",
            () => BuildDataverseState(local.SourceAssemblies, options, jsonOptions));
        RegistrationDiff diff = BuildDiff(local, current);
        PlanPreview plan = BuildPlanPreview(mode, options.Environment, diff, local.Diagnostics, current.Diagnostics);
        if (options.Strict && plan.Diagnostics.Length > 0)
        {
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

// Best-effort cleanup of the temp directories created while extracting/transforming packages.
// Failures are surfaced to stderr rather than silently swallowed so a leaked directory is visible.
static void CleanupTempDirectories(List<string> tempDirectories)
{
    foreach (string directory in tempDirectories)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to delete temp directory '{directory}': {ex.Message}");
        }
    }
}

// This file is the CLI entry point and command orchestration only. The implementation lives in the
// Vela.Cli namespace: RegistrationModels, CliExitCodes, JsonReader,
// DefinitionNormalizer, DiffEngine, OutputWriter, ApplyResumeReport, CommandLineOptions,
// PackageScanner, and DataverseRegistration (Dataverse state reads + apply).
