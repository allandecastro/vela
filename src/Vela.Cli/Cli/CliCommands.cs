using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Spectre.Console;

namespace Vela.Cli;

/// <summary>How an option reads its value.</summary>
internal enum CliOptionKind
{
    /// <summary>A boolean flag: takes no value.</summary>
    Flag,

    /// <summary>Consumes the next token, which is required.</summary>
    Value,

    /// <summary>Consumes the next token when there is one (bare = a mode, e.g. <c>--messages</c> = all).</summary>
    OptionalValue,
}

/// <summary>One documented option. <see cref="Aliases"/> are accepted spellings of the same option.</summary>
internal sealed record CliOption(string Name, CliOptionKind Kind, string Description, string? ValueName = null, string[]? Aliases = null)
{
    public IEnumerable<string> Spellings => [Name, .. Aliases ?? []];

    public string Signature => Kind switch
    {
        CliOptionKind.Value => $"{string.Join(", ", Spellings)} <{ValueName ?? "value"}>",
        CliOptionKind.OptionalValue => $"{string.Join(", ", Spellings)} [{ValueName ?? "value"}]",
        _ => string.Join(", ", Spellings),
    };
}

/// <summary>One command: its usage lines, its options (grouped), and examples.</summary>
internal sealed record CliCommand(string Name, string Summary, string[] Usage, (string Group, CliOption[] Options)[] OptionGroups, string[]? Aliases = null, string[]? Notes = null, string[]? Examples = null)
{
    public IEnumerable<CliOption> Options => OptionGroups.SelectMany(g => g.Options);
}

/// <summary>
/// The declarative command and option table of the CLI. It is the single source for the help text (<c>vela --help</c>,
/// <c>vela &lt;command&gt; --help</c>, <c>vela help --json</c>) and for the unknown-option allowlists of the commands that
/// read it (plan/apply/inspect, sign, logout, update, solution, logs, models), so the help can never list an option the
/// parser rejects or miss one it accepts. <c>init</c> and <c>add</c> keep their own parsers; a test pins their
/// allowlists to this table.
/// </summary>
internal static class CliCommands
{
    private static readonly CliOption Help = new("--help", CliOptionKind.Flag, "Show this help.", Aliases: ["-h"]);
    private static readonly CliOption Json = new("--json", CliOptionKind.Flag, "Machine-readable output on stdout (see docs/reference/machine-readable-output.md).");
    private static readonly CliOption Verbose = new("--verbose", CliOptionKind.Flag, "More detail on stderr (and full stack traces on failure); never changes stdout.", Aliases: ["-v"]);

    internal static readonly CliOption[] ConnectionOptions =
    [
        new("--interactive", CliOptionKind.Flag, "Browser (MSAL) login; the token is cached under ~/.vela so repeat runs are silent.", Aliases: ["-i"]),
        new("--url", CliOptionKind.Value, "Environment URL. Alone, uses your ambient Azure credential (az login / managed identity).", "https://org.crm.dynamics.com"),
        new("--tenant-id", CliOptionKind.Value, "Tenant for client-secret auth.", "id"),
        new("--client-id", CliOptionKind.Value, "App registration for client-secret auth.", "appId"),
        new("--client-secret", CliOptionKind.Value, "Client secret (warned: it leaks to shell history; prefer the two below).", "secret"),
        new("--client-secret-stdin", CliOptionKind.Flag, "Read the client secret from piped stdin (or set DATAVERSE_CLIENT_SECRET)."),
        new("--connection-string", CliOptionKind.Value, "Full Dataverse connection string (or DATAVERSE_CONNECTION_STRING).", "cs"),
        new("--auth-client-id", CliOptionKind.Value, "MSAL public client id for --interactive.", "appId"),
        new("--authority", CliOptionKind.Value, "MSAL authority (sovereign clouds).", "url"),
        new("--fresh-login", CliOptionKind.Flag, "Forget the cached interactive account and prompt again."),
        new("--timeout", CliOptionKind.Value, "Dataverse connection timeout (1-60, default 10).", "minutes"),
    ];

    private static readonly CliOption[] PackageInputs =
    [
        new("--package", CliOptionKind.Value, "A built plugin package (repeatable).", "file.nupkg", ["-p"]),
        new("--packages-dir", CliOptionKind.Value, "Every .nupkg in a folder (the newest version of each package id).", "folder"),
        new("--package-assembly", CliOptionKind.Value, "The primary assembly inside the package(s), when it cannot be inferred.", "name.dll"),
    ];

    private static readonly CliOption Solution = new("--solution", CliOptionKind.Value, "Target solution: writes and components not yet in it are added to it; also resolves the publisher prefix.", "uniquename", ["--solution-unique-name"]);
    private static readonly CliOption WebResources = new("--webresources", CliOptionKind.Value, "(preview) Mirror a web resources folder; requires --solution.", "folder");
    private static readonly CliOption Environment = new("--environment", CliOptionKind.Value, "Label shown in the output (defaults to --url).", "label");
    private static readonly CliOption Out = new("--out", CliOptionKind.Value, "Write the JSON document to a file instead of stdout.", "file");
    private static readonly CliOption SecureConfig = new("--secure-config", CliOptionKind.Value, "Value of a step's named secure configuration (repeatable); or VELA_SECURE_CONFIG_<NAME>.", "NAME=VALUE|NAME=@file");
    private static readonly CliOption Strict = new("--strict", CliOptionKind.Flag, "Fail (exit 4) when there is any warning in diagnostics. Info notices never fail.");

    private static readonly CliOption[] ModelsPackageInputs =
    [
        new("--package", CliOptionKind.Value, "Built plugin package(s): with a bare --custom-apis, excludes the typed Custom APIs you authored (repeatable).", "file.nupkg", ["-p"]),
        new("--packages-dir", CliOptionKind.Value, "A folder of .nupkg packages, used like --package.", "folder"),
        new("--package-assembly", CliOptionKind.Value, "The primary assembly inside the package(s).", "name.dll"),
    ];

    private static readonly string[] ExitCodeNotes =
    [
        "Exit codes: 0 success, 2 invalid arguments, 3 file not found, 4 error (incl. --strict), 5 drift (models --verify, apply not converged),",
        "6 connection failure, 7 partial apply (re-run apply), 8 changes pending (plan --detailed-exitcode only).",
    ];

    public static IReadOnlyList<CliCommand> All { get; } =
    [
        new("plan", "Preview registration changes; never writes to Dataverse.",
            ["vela plan (--package <file.nupkg>... | --packages-dir <folder>) [options] [connection options]"],
            [
                ("Inputs", [.. PackageInputs, Solution, WebResources, SecureConfig]),
                ("Output", [Environment, Out, Json, Verbose, Strict,
                    new("--detailed-exitcode", CliOptionKind.Flag, "Exit 8 when the plan has changes to apply, 0 when there is nothing to do.")]),
                ("Connection", ConnectionOptions),
                ("Other", [Help]),
            ],
            Notes: ["A terminal gets a colored summary; piped, redirected, --json or --out gets the JSON document.", .. ExitCodeNotes],
            Examples:
            [
                "vela plan --packages-dir artifacts --interactive",
                "vela plan --package artifacts/Zava.Plugins.1.0.0.nupkg --url https://zava.crm.dynamics.com --json --detailed-exitcode",
            ]),
        new("apply", "Apply the plan (prompts for confirmation on a terminal).",
            ["vela apply (--package <file.nupkg>... | --packages-dir <folder>) [options] [connection options]"],
            [
                ("Inputs", [.. PackageInputs, Solution, WebResources, SecureConfig]),
                ("Execution", [
                    new("--auto-approve", CliOptionKind.Flag, "Apply without the confirmation prompt (required in a non-interactive session).", Aliases: ["--yes"]),
                    new("--batch-size", CliOptionKind.Value, "ExecuteMultiple chunk size (1-1000, default 100).", "n"),
                    Strict]),
                ("Output", [Environment, Out, Json, Verbose]),
                ("Connection", ConnectionOptions),
                ("Other", [Help]),
            ],
            Notes: ["The JSON 'executed' block reports what was done; 'summary' is what remains after the apply.", .. ExitCodeNotes],
            Examples: ["vela apply --packages-dir artifacts --solution ZavaSales --auto-approve --url https://zava.crm.dynamics.com"]),
        new("inspect", "Show local and live registrations side by side (read-only).",
            ["vela inspect (--package <file.nupkg>... | --packages-dir <folder>) [options] [connection options]"],
            [
                ("Inputs", [.. PackageInputs, Solution, WebResources, SecureConfig]),
                ("Output", [Environment, Out, Json, Verbose]),
                ("Connection", ConnectionOptions),
                ("Other", [Help]),
            ],
            Notes: ExitCodeNotes),
        new("init", "Scaffold a new solution on disk (minimal by default; --samples for worked examples).",
            ["vela init --name <SolutionName> [--prefix <Prefix>] [--publisher-prefix <prefix>] [--samples] [options]"],
            [
                ("Options", [
                    new("--name", CliOptionKind.Value, "Solution name (also accepted as the first positional argument).", "SolutionName"),
                    new("--prefix", CliOptionKind.Value, "Namespace / assembly prefix of the projects.", "Prefix"),
                    new("--publisher-prefix", CliOptionKind.Value, "Dataverse publisher prefix (2-8 chars) naming the Custom APIs; recorded for 'vela add customapi'.", "prefix"),
                    new("--output", CliOptionKind.Value, "Parent folder: the solution is created in <dir>/<SolutionName> (default: current directory).", "dir", ["-o"]),
                    new("--samples", CliOptionKind.Flag, "Add the demo code and the Integrations/ConsoleApp projects."),
                    new("--minimal", CliOptionKind.Flag, "The default: no demo code (kept as an alias).", Aliases: ["--no-demos"]),
                    new("--integrations", CliOptionKind.Flag, "Add an empty Integrations plugin project (and its tests) without --samples."),
                    new("--no-integrations", CliOptionKind.Flag, "With --samples: skip the Integrations project."),
                    new("--no-tests", CliOptionKind.Flag, "Skip the unit tests project."),
                    new("--no-console", CliOptionKind.Flag, "With --samples: skip the console project."),
                    new("--core-version", CliOptionKind.Value, "Vela.Plugins version (default: latest on nuget.org).", "ver"),
                    new("--managed-identity", CliOptionKind.Flag, "Add the signing step to the Integrations package (implies --integrations).", Aliases: ["--mi"]),
                    new("--web-resources", CliOptionKind.Flag, "(preview) Add a TypeScript web resources project.", Aliases: ["--webresources"]),
                    new("--web-resources-namespace", CliOptionKind.Value, "(preview) Namespace of the web resources.", "name"),
                    new("--web-resources-folder", CliOptionKind.Value, "(preview) Folder of the web resources.", "dir"),
                    new("--force", CliOptionKind.Flag, "Regenerate into a non-empty folder; only files Vela wrote and you did not edit are overwritten or removed."),
                    new("--interactive", CliOptionKind.Flag, "Guided prompts.", Aliases: ["-i"]),
                    Json,
                    Verbose,
                    Help]),
            ],
            Aliases: ["new"],
            Examples: ["vela init --name ZavaSales --prefix Zava --publisher-prefix zava --output .", "vela init --name ZavaDemo --prefix Zava --samples"]),
        new("add", "Add a plugin, service, Custom API, test or step to a project.",
            ["vela add (plugin|service|customapi|test|step|form-script|ribbon-script) <Name> [options]", "vela add --interactive"],
            [
                ("Options", [
                    new("--entity", CliOptionKind.Value, "Table model type (plugin/service/step; optional for customapi).", "ModelType", ["-e"]),
                    new("--message", CliOptionKind.Value, "SDK message (plugin/step/test).", "Message"),
                    new("--stage", CliOptionKind.Value, "PreValidation | PreOperation | PostOperation.", "stage"),
                    new("--mode", CliOptionKind.Value, "Sync | Async.", "mode"),
                    new("--filtering-attributes", CliOptionKind.Value, "Columns the step filters on (required for Create/Update).", "Col1,Col2", ["--filter"]),
                    new("--handler", CliOptionKind.Value, "Handler method name (step).", "Method"),
                    new("--kind", CliOptionKind.Value, "plugin | customapi (test).", "kind"),
                    new("--bound", CliOptionKind.Flag, "Entity-bound Custom API."),
                    new("--global", CliOptionKind.Flag, "Global (unbound) Custom API."),
                    new("--function", CliOptionKind.Flag, "Custom API is a function."),
                    new("--action", CliOptionKind.Flag, "Custom API is an action."),
                    new("--prefix", CliOptionKind.Value, "Custom API publisher prefix (customapi/test; default: the project's VelaPublisherPrefix).", "prefix"),
                    new("--project", CliOptionKind.Value, "Target project (default: the single .csproj in the current folder).", "csproj", ["-p"]),
                    new("--force", CliOptionKind.Flag, "Overwrite an existing file (not step; test completes an existing class without it)."),
                    new("--interactive", CliOptionKind.Flag, "Guided wizard.", Aliases: ["-i"]),
                    Json,
                    Help]),
            ],
            Notes: [
                "plugin <Name> --entity <ModelType> [--message] [--stage] [--mode] [--filtering-attributes] (required for Create/Update(+Multiple)).",
                "service <Name> --entity <ModelType>.",
                "customapi <Name> [--entity <ModelType>] [--bound|--global] [--function|--action] [--prefix <publisherprefix>].",
                "test <Name> [--kind plugin|customapi] [--entity] [--message] [--prefix]: run inside the test project (RootNamespace ending in .Tests). An existing test class is completed with the tests of the steps it lacks.",
                "step <PluginName> --entity <ModelType> [--message] [--handler] [--stage] [--mode] [--filtering-attributes]: edits an existing multi-step plugin.",
                "form-script|ribbon-script <entity> [--project <web-resources dir>] [--force]: (preview).",
                "Each kind accepts only its own options. A Custom API is named <publisherprefix>_<Name>: --prefix, else the project's VelaPublisherPrefix, else the project prefix.",
            ]),
        new("models", "Generate early-bound models, typed messages or roles.",
            ["vela models <scope>... [options] [connection options]", "vela models --from-solution <solution.zip> --namespace <ns>   (offline)"],
            [
                ("Scopes (combinable)", [
                    new("--solution", CliOptionKind.Value, "Tables taken from the solution's components.", "uniquename"),
                    new("--entities", CliOptionKind.Value, "Table logical names.", "a,b,c"),
                    new("--custom-apis", CliOptionKind.OptionalValue, "Custom API unique names (bare: every Custom API, with a warning).", "a,b,c"),
                    new("--messages", CliOptionKind.OptionalValue, "SDK request names, e.g. SendEmail (bare: one class per request of every consumable message).", "a,b,c"),
                    new("--roles", CliOptionKind.Value, "Security role names: a dependency-light Roles class.", "a,b,c"),
                    new("--solution-roles", CliOptionKind.Flag, "Every security role in --solution."),
                    new("--intersect", CliOptionKind.Flag, "Also the N:N intersect tables of the scoped tables."),
                    new("--from-solution", CliOptionKind.Value, "Offline: entity models and option sets from an exported solution zip.", "zip"),
                    new("--forms", CliOptionKind.Flag, "(preview) TypeScript FormModel files."),
                    new("--tables", CliOptionKind.Flag, "(preview) TypeScript Web API record interfaces."),
                ]),
                ("Packages", ModelsPackageInputs),
                ("Output", [
                    new("--lang", CliOptionKind.Value, "csharp (default) | ts.", "lang"),
                    new("--namespace", CliOptionKind.Value, "Output namespace (default: inferred from the project).", "ns"),
                    new("--project", CliOptionKind.Value, "Project whose namespace and Models/Generated folder are used.", "csproj"),
                    new("--out", CliOptionKind.Value, "Output directory (default: current directory).", "dir"),
                    new("--no-annotations", CliOptionKind.Flag, "Omit DataAnnotations ([StringLength]/[Range])."),
                    new("--overwrite", CliOptionKind.Flag, "Overwrite existing files."),
                    new("--verify", CliOptionKind.Flag, "Do not write: report drift of the model on disk (exit 5 on drift)."),
                    Json,
                    Verbose]),
                ("Connection", ConnectionOptions),
                ("Other", [Help]),
            ],
            Examples: ["vela models --solution ZavaSales --interactive", "vela models --custom-apis --package artifacts/Zava.Plugins.1.0.0.nupkg -i"]),
        new("logs", "Read plug-in trace logs or failed system jobs (read-only).",
            ["vela logs [options] [connection options]"],
            [
                ("Options", [
                    new("--jobs", CliOptionKind.Flag, "Failed system jobs (asyncoperation) instead of the trace log."),
                    new("--all-statuses", CliOptionKind.Flag, "With --jobs: every status, not only failed."),
                    new("--errors", CliOptionKind.Flag, "Trace log entries with an exception only."),
                    new("--top", CliOptionKind.Value, "Max entries, newest first (1-200, default 20).", "n"),
                    new("--since", CliOptionKind.Value, "A duration (30m, 2h, 3d) or a date.", "when"),
                    new("--type", CliOptionKind.Value, "Plugin type name (substring).", "name"),
                    new("--message", CliOptionKind.Value, "SDK message.", "m"),
                    new("--entity", CliOptionKind.Value, "Table logical name.", "logical"),
                    new("--correlation", CliOptionKind.Value, "Correlation id.", "guid"),
                    new("--full", CliOptionKind.Flag, "Do not truncate trace and exception text."),
                    Json,
                    Verbose]),
                ("Connection", ConnectionOptions),
                ("Other", [Help]),
            ],
            Aliases: ["trace"],
            Examples: ["vela logs --errors --since 1h -i", "vela logs --jobs --since 1d --json"]),
        new("solution", "Print a solution's publisher prefix (read-only).",
            ["vela solution show <uniquename> [--json] [connection options]"],
            [
                ("Options", [Json, Verbose]),
                ("Connection", ConnectionOptions),
                ("Other", [Help]),
            ],
            Notes: ["JSON: {\"solution\":\"...\",\"publisherPrefix\":\"...\"}."],
            Examples: [
                "vela init --name ZavaSales --prefix Zava --publisher-prefix \"$(vela solution show ZavaSales -i)\"",
                "vela add customapi ApproveOrder --prefix \"$(vela solution show ZavaSales -i)\"",
            ]),
        new("sign", "Sign a plugin package (managed-identity plugins).",
            ["vela sign --package <path.nupkg> (--fingerprint <hex> | (--pfx <file> | --pfx-base64-env <VAR>) (--pfx-password-env <VAR> | --pfx-credential <target>)) [--timestamp <url>]"],
            [
                ("Options", [
                    new("--package", CliOptionKind.Value, "The package to sign.", "path.nupkg"),
                    new("--fingerprint", CliOptionKind.Value, "SHA-256 fingerprint of a certificate in the certificate store.", "hex"),
                    new("--pfx", CliOptionKind.Value, "A PFX certificate file.", "file"),
                    new("--pfx-base64-env", CliOptionKind.Value, "Environment variable holding the PFX as base64 (CI secrets).", "VAR"),
                    new("--pfx-base64", CliOptionKind.Value, "The PFX as base64 on the command line (warns: visible to other users; prefer --pfx-base64-env).", "content"),
                    new("--pfx-password-env", CliOptionKind.Value, "Environment variable holding the PFX password.", "VAR"),
                    new("--pfx-password", CliOptionKind.Value, "The PFX password on the command line (warns: visible to other users; prefer --pfx-password-env).", "pw"),
                    new("--pfx-credential", CliOptionKind.Value, "Read the PFX password from the OS credential store.", "target"),
                    new("--timestamp", CliOptionKind.Value, "RFC 3161 timestamp server.", "url"),
                    Json,
                    Help]),
            ]),
        new("logout", "Clear the cached login token and the remembered URL.",
            ["vela logout"],
            [("Options", [Help])]),
        new("update", "Update the vela global tool.",
            ["vela update [--version <x.y.z>]"],
            [("Options", [new("--version", CliOptionKind.Value, "Install this version instead of the latest.", "x.y.z"), Help])],
            Notes: ["On Windows the running executable is locked: run it from a new shell if the update fails."]),
        new("menu", "Launch the interactive menu (the default with no arguments).", ["vela menu"], []),
        new("version", "Print the tool version.", ["vela version"], []),
        new("help", "Help for one command (vela help <command>), or all as JSON (--json).", ["vela help [<command>] [--json]"], []),
    ];

    public static CliCommand? Find(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            || (c.Aliases ?? []).Contains(name, StringComparer.OrdinalIgnoreCase));

    private static CliCommand Get(string name) => Find(name) ?? throw new ArgumentException($"Unknown command '{name}'.", nameof(name));

    /// <summary>Every spelling of the command's value options (required or optional value).</summary>
    public static HashSet<string> ValueOptions(string command) =>
        new(Get(command).Options.Where(o => o.Kind != CliOptionKind.Flag).SelectMany(o => o.Spellings), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every spelling of the command's flags.</summary>
    public static HashSet<string> FlagOptions(string command) =>
        new(Get(command).Options.Where(o => o.Kind == CliOptionKind.Flag).SelectMany(o => o.Spellings), StringComparer.OrdinalIgnoreCase);

    /// <summary>The value options that may appear without a value.</summary>
    public static HashSet<string> OptionalValueOptions(string command) =>
        new(Get(command).Options.Where(o => o.Kind == CliOptionKind.OptionalValue).SelectMany(o => o.Spellings), StringComparer.OrdinalIgnoreCase);

    /// <summary><c>vela --help</c>: the commands and their one-line summary.</summary>
    public static void PrintOverview()
    {
        // Bound to the current Console.Out (not the process stdout captured once), so a redirected stdout gets it too;
        // Spectre drops the styling when that writer is not a terminal.
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Out) });
        console.MarkupLine($"[bold #6366f1]{Markup.Escape(ToolInfo.ProductName)}[/]  [#22d3ee]v{Markup.Escape(ToolInfo.Version)}[/]");
        console.MarkupLine("[grey]Register Dataverse plugins and Custom APIs from your build packages.[/]");
        console.WriteLine();
        console.MarkupLine("[bold]Usage:[/] vela <command> [[options]]   (vela <command> --help for its options)");
        console.WriteLine();
        console.MarkupLine("[bold]Commands:[/]");
        foreach (CliCommand command in All)
        {
            string aliases = command.Aliases is { Length: > 0 } a ? $" (alias: {string.Join(", ", a)})" : string.Empty;
            console.MarkupLine($"  [#22d3ee]{command.Name.PadRight(9)}[/] [grey]{Markup.Escape(command.Summary + aliases)}[/]");
        }

        console.WriteLine();
        console.MarkupLine("[bold]Output:[/]");
        Console.WriteLine("  plan/apply/inspect print a styled summary on a terminal and a versioned JSON document when piped,");
        Console.WriteLine("  with --json or with --out <file>. Notices and --verbose detail go to stderr. NO_COLOR / VELA_NO_COLOR disable colors.");
        console.WriteLine();
        console.MarkupLine("[bold]Exit codes:[/]");
        foreach (string line in ExitCodeNotes)
        {
            Console.WriteLine("  " + line.Replace("Exit codes: ", string.Empty, StringComparison.Ordinal));
        }
    }

    /// <summary><c>vela &lt;command&gt; --help</c>: usage, options by group, notes and examples.</summary>
    public static void PrintCommandHelp(string name)
    {
        CliCommand command = Get(name);
        Console.WriteLine($"vela {command.Name}: {command.Summary}");
        if (command.Aliases is { Length: > 0 })
        {
            Console.WriteLine($"Aliases: {string.Join(", ", command.Aliases)}");
        }

        Console.WriteLine();
        Console.WriteLine("Usage:");
        foreach (string usage in command.Usage)
        {
            Console.WriteLine($"  {usage}");
        }

        int width = command.Options.Select(o => o.Signature.Length).DefaultIfEmpty(0).Max();
        foreach ((string group, CliOption[] options) in command.OptionGroups)
        {
            Console.WriteLine();
            Console.WriteLine($"{group}:");
            foreach (CliOption option in options)
            {
                Console.WriteLine($"  {option.Signature.PadRight(width)}  {option.Description}");
            }
        }

        foreach ((string title, string[]? lines) in new[] { ("Notes", command.Notes), ("Examples", command.Examples) })
        {
            if (lines is not { Length: > 0 })
            {
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"{title}:");
            foreach (string line in lines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    /// <summary><c>vela help [&lt;command&gt;] [--json]</c>.</summary>
    public static int RunHelp(string[] args, JsonSerializerOptions jsonOptions)
    {
        if (args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(ToJson(jsonOptions));
            return CliExitCodes.ExitSuccess;
        }

        string? name = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (name == null)
        {
            PrintOverview();
            return CliExitCodes.ExitSuccess;
        }

        if (Find(name) is not { } command)
        {
            return OutputWriter.Fail($"Unknown command '{name}'. Run 'vela help' for the list.", CliExitCodes.ExitInvalidArguments, includeUsage: false);
        }

        PrintCommandHelp(command.Name);
        return CliExitCodes.ExitSuccess;
    }

    /// <summary><c>vela help --json</c>: the table above, for agents and completion scripts.</summary>
    public static string ToJson(JsonSerializerOptions jsonOptions) => JsonSerializer.Serialize(new
    {
        schemaVersion = MachineOutput.SchemaVersion,
        tool = ToolInfo.ToolName,
        version = ToolInfo.Version,
        commands = All.Select(c => new
        {
            name = c.Name,
            aliases = c.Aliases ?? [],
            summary = c.Summary,
            usage = c.Usage,
            options = c.Options.Select(o => new
            {
                name = o.Name,
                aliases = o.Aliases ?? [],
                kind = o.Kind switch { CliOptionKind.Flag => "flag", CliOptionKind.Value => "value", _ => "optional-value" },
                value = o.ValueName,
                description = o.Description,
            }),
        }),
        exitCodes = new Dictionary<string, string>
        {
            ["0"] = "success",
            ["2"] = "invalid arguments",
            ["3"] = "file not found",
            ["4"] = "error (including a --strict failure)",
            ["5"] = "drift detected (models --verify)",
            ["6"] = "Dataverse connection failure",
            ["7"] = "partial apply",
            ["8"] = "changes pending (plan --detailed-exitcode)",
        },
    }, new JsonSerializerOptions(jsonOptions) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
