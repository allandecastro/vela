using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spectre.Console;
using static Vela.Cli.JsonReader;
using static Vela.Cli.DefinitionNormalizer;
using static Vela.Cli.DiffEngine;

namespace Vela.Cli;

/// <summary>
/// Console / file output: JSON emission, verbose human summaries, usage, and failure formatting.
/// </summary>
internal static class OutputWriter
{
    public static void WriteOutput(string? outPath, string output)
    {
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Console.WriteLine(output);
            return;
        }

        string outputPath = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, output);
        Console.WriteLine($"Plan written: {outputPath}");
    }

    public static void WriteVerbosePlanSummary(PlanPreview preview)
    {
        Console.Error.WriteLine($"{preview.Mode} {preview.Environment}");
        Console.Error.WriteLine($"summary: +{preview.Summary.Added} -{preview.Summary.Removed} ~{preview.Summary.Changed}");

        if (preview.Operations.Length > 0)
        {
            Console.Error.WriteLine("operations:");
            foreach (ApplyOperation operation in preview.Operations)
            {
                Console.Error.WriteLine($"  - {operation.Action} {operation.Kind}: {operation.Type}");
            }
        }

        if (preview.Diff.Added.Length > 0)
        {
            Console.Error.WriteLine("added:");
            foreach (RegistrationItem item in preview.Diff.Added)
            {
                Console.Error.WriteLine($"  + {item.Kind}: {DescribeItem(item)}");
            }
        }

        if (preview.Diff.Removed.Length > 0)
        {
            Console.Error.WriteLine("removed:");
            foreach (RegistrationItem item in preview.Diff.Removed)
            {
                Console.Error.WriteLine($"  - {item.Kind}: {DescribeItem(item)}");
            }
        }

        if (preview.Diff.Changed.Length > 0)
        {
            Console.Error.WriteLine("changes:");
            foreach (RegistrationChange change in preview.Diff.Changed)
            {
                string before = DescribeDefinition(change.Before);
                string after = DescribeDefinition(change.After);
                Console.Error.WriteLine($"  - {change.Kind}: {change.Type}");
                Console.Error.WriteLine($"    before: {before}");
                Console.Error.WriteLine($"    after : {after}");
            }
        }
    }

    public static void WriteVerboseInspectSummary(RegistrationState local, RegistrationState current, string? environment)
    {
        Console.Error.WriteLine($"inspect {environment}");
        WriteVerboseStateSummary("local", local);
        WriteVerboseStateSummary("current", current);
    }

    public static void WriteVerboseStateSummary(string label, RegistrationState state)
    {
        Console.Error.WriteLine($"{label} assemblies: {string.Join(", ", state.SourceAssemblies)}");

        RegistrationItem[] steps = state.Items.Where(IsStep).OrderBy(x => x.Type, StringComparer.Ordinal).ToArray();
        RegistrationItem[] customApis = state.Items.Where(x => !IsStep(x)).OrderBy(x => x.Type, StringComparer.Ordinal).ToArray();

        Console.Error.WriteLine($"{label} steps: {steps.Length}");
        foreach (RegistrationItem item in steps)
        {
            Console.Error.WriteLine($"  - {DescribeItem(item)}");
        }

        Console.Error.WriteLine($"{label} custom-apis: {customApis.Length}");
        foreach (RegistrationItem item in customApis)
        {
            Console.Error.WriteLine($"  - {DescribeItem(item)}");
        }
    }

    public static string DescribeItem(RegistrationItem item)
    {
        return DescribeDefinition(item.Definition);
    }

    public static string DescribeDefinition(JsonElement definition)
    {
        string baseName = TryGetString(definition, "name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            string pluginTypeName = TryGetString(definition, "pluginTypeName") ?? string.Empty;
            string message = TryGetString(definition, "message") ?? string.Empty;
            string stepKey = TryGetString(definition, "stepKey") ?? string.Empty;
            string uniqueName = TryGetString(definition, "uniqueName") ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(stepKey))
            {
                baseName = $"{pluginTypeName} - {BuildStepLifecycleLabel(TryGetInt(definition, "stage"), TryGetInt(definition, "mode"))}: {stepKey}";
            }
            else if (!string.IsNullOrWhiteSpace(uniqueName))
            {
                baseName = uniqueName;
            }
            else
            {
                baseName = string.IsNullOrWhiteSpace(pluginTypeName) ? definition.ToString() : $"{pluginTypeName} {message}";
            }
        }

        if (!string.IsNullOrWhiteSpace(TryGetString(definition, "message")) || !string.IsNullOrWhiteSpace(TryGetString(definition, "stepKey")))
        {
            string filter = TryGetStringArray(definition, "filteringAttributes").Length == 0
                ? "filter=none"
                : $"filter={string.Join(",", TryGetStringArray(definition, "filteringAttributes"))}";
            int preCount = TryGetArrayLength(definition, "preImages");
            int postCount = TryGetArrayLength(definition, "postImages");
            return $"{baseName} [{filter}; pre={preCount}; post={postCount}]";
        }

        if (!string.IsNullOrWhiteSpace(TryGetString(definition, "uniqueName")))
        {
            bool isBound = TryGetBool(definition, "isBound");
            string binding = isBound ? "bound" : "global";
            int inputs = TryGetArrayLength(definition, "inputParameters");
            int outputs = TryGetArrayLength(definition, "outputParameters");
            return $"{baseName} [{binding}; in={inputs}; out={outputs}]";
        }

        return baseName;
    }

    public static PlanPreview BuildPlanPreview(string mode, string? environment, RegistrationDiff diff, string[] localDiagnostics, string[] currentDiagnostics)
    {
        List<ApplyOperation> operations = [];
        operations.AddRange(diff.Added.Select(item => new ApplyOperation("create", item.Kind, item.Type, "Exists locally but not in Dataverse.")));
        operations.AddRange(diff.Removed.Select(item => new ApplyOperation("delete", item.Kind, item.Type, "Exists in Dataverse but not locally.")));
        operations.AddRange(diff.Changed.Select(item => new ApplyOperation("update", item.Kind, item.Type, "Exists in both places but differs.")));

        return new PlanPreview(
            Mode: mode,
            Environment: environment,
            HasChanges: diff.Summary.Added > 0 || diff.Summary.Removed > 0 || diff.Summary.Changed > 0,
            Summary: diff.Summary,
            Operations: operations.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
            Diff: diff,
            Diagnostics: localDiagnostics.Concat(currentDiagnostics).ToArray(),
            Notes:
            [
                "Compared local .nupkg plugin metadata with live Dataverse state.",
                "No Dataverse changes were executed."
            ]);
    }

    // Top-level `vela --help`. Section headers and command names are rendered through Spectre so a real
    // terminal gets the teal branding used by the interactive menu, while the dense, bracket-heavy option
    // and example lines are written with plain Console.WriteLine — Spectre would otherwise treat their
    // '[' / ']' as markup and reflow them to the detected console width. When stdout is redirected
    // (pipes, CI) Spectre strips the styling automatically, so scripts still see clean plain text.
    public static void PrintUsage()
    {
        IAnsiConsole console = AnsiConsole.Console;

        console.MarkupLine($"[bold teal]{Markup.Escape(ToolInfo.ProductName)}[/]  [green]v{Markup.Escape(ToolInfo.Version)}[/]");
        console.MarkupLine("[grey]Register Dataverse plugins and Custom APIs from your build packages.[/]");
        console.WriteLine();
        console.MarkupLine("[bold]Usage:[/] vela <command> [[options]]");
        console.WriteLine();

        console.MarkupLine("[bold]Commands:[/]");
        WriteCommand(console, "plan", "Preview registration changes; never writes to Dataverse.");
        Console.WriteLine("      [--package <plugin.nupkg>]... [--packages-dir <folder>] [--package-assembly <name.dll>] [--webresources <folder> (preview)] [connection options] [--environment <label>] [--out <file>] [--interactive] [--strict] [--verbose]");
        WriteCommand(console, "apply", "Same inputs as plan; add --execute to write the changes.");
        Console.WriteLine("      [same args as plan] [--solution <solutionUniqueName>] [--webresources <folder> (preview)] [--execute] [--strict] [--verbose]");
        WriteCommand(console, "inspect", "Compare local packages with the live environment.");
        Console.WriteLine("      [same args as plan] [--verbose]");
        WriteCommand(console, "init", "Scaffold a new solution on disk (aliases: new).");
        Console.WriteLine("      --name <SolutionName> [--prefix <Prefix>] [--output <dir>] [--no-integrations] [--no-tests] [--no-console] [--core-version <ver>] [--managed-identity] [--web-resources (preview) [--web-resources-namespace <name>] [--web-resources-folder <dir>]] [--json] [--force] [--interactive]");
        WriteCommand(console, "add", "Add a plugin, service, Custom API, test, or step to a project.");
        Console.WriteLine("      (plugin|service|customapi|test|step) <Name> [...]   run 'vela add --help' for the full options");
        WriteCommand(console, "models", "Generate early-bound model classes.");
        Console.WriteLine("      (--solution <uniquename> | --entities <a,b,c>) [--namespace <ns>] [--out <dir>] [--no-annotations] [--overwrite] [--forms and/or --tables (preview, TypeScript)] [connection options]");
        WriteCommand(console, "sign", "Sign a plugin package.");
        Console.WriteLine("      --package <path.nupkg> (--fingerprint <hex> | (--pfx <file> | --pfx-base64 <content>) (--pfx-password <pw> | --pfx-credential <target>)) [--timestamp <url>] [--json]");
        WriteCommand(console, "menu", "Launch the interactive menu (default with no args).");
        WriteCommand(console, "version", "Print the tool version.");
        console.WriteLine();

        console.MarkupLine("[bold]Auth options:[/]");
        Console.WriteLine("  Client secret resolution order: --client-secret <value> (warned), --client-secret-stdin, DATAVERSE_CLIENT_SECRET env var.");
        Console.WriteLine("  --auth-client-id <appId>   Override the MSAL public client id used for --interactive browser login.");
        Console.WriteLine("  --authority <url>          Override the MSAL authority (e.g. for sovereign clouds).");
        Console.WriteLine("  --verbose also prints full exception stack traces on failure.");
        console.WriteLine();

        console.MarkupLine("[bold]Examples:[/]");
        Console.WriteLine("  vela plan --package .\\bin\\Release\\Zava.Plugins.Package.1.0.0.nupkg --url https://zava.crm.dynamics.com --tenant-id <tenantId> --client-id <appId> --client-secret <secret>");
        Console.WriteLine("  vela plan --packages-dir .\\artifacts\\packages --connection-string \"$env:DATAVERSE_CONNECTION_STRING\"");
        Console.WriteLine("  vela plan --interactive");
        Console.WriteLine("  vela init --name ZavaSales --prefix Zava --output .");
        console.WriteLine();

        console.MarkupLine("[bold]Exit codes:[/]");
        Console.WriteLine("  0: Success");
        Console.WriteLine("  2: Invalid arguments");
        Console.WriteLine("  3: File not found");
        Console.WriteLine("  4: Parse/read error");
        Console.WriteLine("  6: Dataverse connection failure");
    }

    // Renders one command row: a colored, aligned name followed by a short summary. Names are padded to a
    // fixed width so summaries line up; the padding is applied before markup so styling doesn't skew it.
    private static void WriteCommand(IAnsiConsole console, string name, string summary)
    {
        console.MarkupLine($"  [aqua]{name.PadRight(9)}[/] [grey]{Markup.Escape(summary)}[/]");
    }

    public static int Fail(string message, int exitCode, bool includeUsage = true)
    {
        Console.Error.WriteLine(message);
        if (includeUsage)
        {
            PrintUsage();
        }

        return exitCode;
    }

    // Fails with just the exception message on stderr, but prints the full exception (type + stack
    // trace) when --verbose is set, so failures are diagnosable without changing the default output.
    public static int FailWithException(Exception ex, int exitCode, bool verbose)
    {
        if (verbose)
        {
            Console.Error.WriteLine(ex.ToString());
        }

        return Fail(ex.Message, exitCode, includeUsage: false);
    }

    public static string? Prompt(string label, bool allowEmpty = false, bool secret = false)
    {
        string? value;
        if (Console.IsInputRedirected)
        {
            // Redirected stdin (tests, pipes, CI): plain read so behavior stays deterministic.
            Console.Write(label + " ");
            value = Console.ReadLine();
        }
        else
        {
            // Interactive terminal: use Spectre for a consistent prompt experience (and masked secrets).
            // Markup.Escape guards against any '[' / ']' in the label being parsed as markup.
            var prompt = new Spectre.Console.TextPrompt<string>(Spectre.Console.Markup.Escape(label))
            {
                AllowEmpty = true,
                IsSecret = secret,
                Mask = '*',
            };

            value = Spectre.Console.AnsiConsole.Prompt(prompt);
        }

        if (allowEmpty)
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
