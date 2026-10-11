using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Cli;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.OutputWriter;
using static Vela.Cli.CommandLineOptions;

JsonSerializerOptions jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// The package scan re-runs this executable as a child (IsolatedPackageScan): handled before anything else,
// so the child never prompts, opens the menu or checks for updates.
if (args.Length > 0 && args[0] == IsolatedPackageScan.ChildCommand)
{
    return IsolatedPackageScan.RunChild(args[1..]);
}

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

// `vela --help` / `-h` print the overview; `vela help [<command>] [--json]` the overview, one command, or the
// whole command table as JSON (all generated from CliCommands).
if (command is "--help" or "-h" or "-?" or "/?")
{
    PrintUsage();
    return ExitSuccess;
}

if (command is "help")
{
    return CliCommands.RunHelp(effectiveArgs.Skip(1).ToArray(), jsonOptions);
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
    "plan" or "apply" or "inspect" => RunPlanApplyInspect(command, commandArgs, jsonOptions),
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
        CliCommands.PrintCommandHelp("init");
        return ExitSuccess;
    }

    if (!ScaffoldOptions.TryParse(args, out ScaffoldOptions options, out int exitCode))
    {
        return exitCode;
    }

    // Unless the caller pinned a version, scaffold against the latest published framework resolved from
    // nuget.org, so the CLI and framework can release on independent tags without the scaffold going stale.
    // Falls back to the built-in default when offline.
    if (!options.CoreVersionPinned)
    {
        string? latest = ScaffoldOptions.ResolveLatestFrameworkVersion();
        if (latest != null)
        {
            options = options with { CoreVersion = latest };
            // A notice, not output: stderr, so `vela init --json` writes nothing but the JSON document to stdout.
            Console.Error.WriteLine($"Scaffolding against Vela.Plugins {latest} (latest on nuget.org; override with --core-version).");
        }
        else
        {
            Console.Error.WriteLine($"Could not reach nuget.org; scaffolding against Vela.Plugins {options.CoreVersion} (built-in default). Override with --core-version.");
        }
    }

    bool verbose = options.Verbose;
    try
    {
        return ProjectScaffolder.Generate(options);
    }
    catch (Exception ex)
    {
        return FailWithException(ex, ExitError, verbose);
    }
}

// plan / apply / inspect share one parser and one orchestration path (PlanApplyCommand).
static int RunPlanApplyInspect(string command, string[] args, JsonSerializerOptions jsonOptions)
{
    if (IsHelpRequested(args))
    {
        CliCommands.PrintCommandHelp(command);
        return ExitSuccess;
    }

    // Each command has its own allowlist: `plan --auto-approve` is an error, not silently ignored.
    if (!TryParseSharedOptions(command, args, out SharedOptions options, out int exitCode))
    {
        return exitCode;
    }

    // apply executes by default; its confirmation gate (interactive prompt / --auto-approve in a non-interactive
    // session) runs after the plan is computed. Use `vela plan` to preview.
    return PlanApplyCommand.Run(command, options, jsonOptions);
}

// This file is the CLI entry point and command dispatch only. The implementation lives in the Vela.Cli namespace:
// PlanApplyCommand (plan/apply/inspect orchestration), RegistrationModels, CliExitCodes, DiffEngine, OutputWriter,
// ApplyResumeReport, CommandLineOptions, PackageScanner, and DataverseRegistration (Dataverse reads + apply).
