using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    public static void PrintUsage()
    {
        Console.WriteLine("Vela.Cli");
        Console.WriteLine("Commands:");
        Console.WriteLine("  plan [--package <plugin.nupkg>]... [--packages-dir <folder>] [--package-assembly <name.dll>] [--connection-string <value>] [--url <envUrl> --tenant-id <tenant> --client-id <appId> --client-secret <secret>] [--environment <label>] [--out <file>] [--interactive] [--strict] [--verbose]");
        Console.WriteLine("  apply [same args as plan] [--solution <solutionUniqueName>] [--execute] [--strict] [--verbose]");
        Console.WriteLine("  inspect [same args as plan] [--verbose]");
        Console.WriteLine("  init --name <SolutionName> [--prefix <Prefix>] [--output <dir>] [--no-integrations] [--no-tests] [--no-console] [--core-version <ver>] [--managed-identity] [--force] [--interactive]");
        Console.WriteLine("  add plugin <Name> --entity <ModelType> [--message Create|Update|Delete] [--project <csproj>] [--force]");
        Console.WriteLine("  add customapi <Name> [--entity <ModelType>] [--project <csproj>] [--force]");
        Console.WriteLine("  models (--solution <uniquename> | --entities <a,b,c>) [--namespace <ns>] [--out <dir>] [--no-annotations] [--overwrite] [connection options]");
        Console.WriteLine("  sign --package <path.nupkg> (--fingerprint <hex> | (--pfx <file> | --pfx-base64 <content>) (--pfx-password <pw> | --pfx-credential <target>)) [--timestamp <url>]");
        Console.WriteLine("  menu                        Launch the interactive menu (also the default when run with no arguments in a terminal).");
        Console.WriteLine("  version                     Print the tool version.");
        Console.WriteLine();
        Console.WriteLine("Auth options:");
        Console.WriteLine("  Client secret resolution order: --client-secret <value> (warned), --client-secret-stdin, DATAVERSE_CLIENT_SECRET env var.");
        Console.WriteLine("  --auth-client-id <appId>   Override the MSAL public client id used for --interactive browser login.");
        Console.WriteLine("  --authority <url>          Override the MSAL authority (e.g. for sovereign clouds).");
        Console.WriteLine("  --verbose also prints full exception stack traces on failure.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dataverse-framework plan --package .\\bin\\Release\\Zava.Plugins.Package.1.0.0.nupkg --url https://zava.crm.dynamics.com --tenant-id <tenantId> --client-id <appId> --client-secret <secret>");
        Console.WriteLine("  dataverse-framework plan --packages-dir .\\artifacts\\packages --connection-string \"$env:DATAVERSE_CONNECTION_STRING\"");
        Console.WriteLine("  dataverse-framework plan --interactive");
        Console.WriteLine("  vela init --name ZavaSales --prefix Zava --output .");
        Console.WriteLine();
        Console.WriteLine("Exit codes:");
        Console.WriteLine("  0: Success");
        Console.WriteLine("  2: Invalid arguments");
        Console.WriteLine("  3: File not found");
        Console.WriteLine("  4: Parse/read error");
        Console.WriteLine("  6: Dataverse connection failure");
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
