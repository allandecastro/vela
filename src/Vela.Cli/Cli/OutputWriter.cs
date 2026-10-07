using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    // Generated source is assembled from raw-string templates (whose newlines follow the checked-out EOL, which
    // is CRLF on a Windows checkout) and from code that concatenates explicit "\n", so a file can end up with
    // mixed line endings. Normalize generated files to LF - consistent, and what `vela models` already emits.
    public static string NormalizeNewlines(string content)
        => string.IsNullOrEmpty(content) ? content : content.Replace("\r\n", "\n").Replace("\r", "\n");

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
        // A notice, not output: stderr, so stdout carries nothing but the machine-readable document (or nothing).
        Console.Error.WriteLine($"Plan written: {outputPath}");
    }

    /// <summary>
    /// Renders a human-readable plan/apply summary to <paramref name="writer"/>. Used both for the
    /// <c>--verbose</c> summary (stderr, alongside JSON on stdout) and for the default terminal output on a TTY
    /// (stdout, in place of JSON). Pass <c>color</c> from the matching stream's <see cref="ConsoleTheme"/> flag.
    /// </summary>
    public static void WritePlanSummary(PlanPreview preview, TextWriter writer, bool color)
    {
        writer.WriteLine(ConsoleTheme.Heading($"{preview.Mode} {preview.Environment}", color));
        string added = ConsoleTheme.Added($"+{preview.Summary.Added}", color);
        string removed = ConsoleTheme.Removed($"-{preview.Summary.Removed}", color);
        string changed = ConsoleTheme.Changed($"~{preview.Summary.Changed}", color);
        writer.WriteLine($"{ConsoleTheme.Muted("summary:", color)} {added} {removed} {changed}");

        WriteUnmanaged(preview.Unmanaged, writer, color);
        WriteInfo(preview.Info, writer, color);

        if (preview.Operations.Length > 0)
        {
            writer.WriteLine(ConsoleTheme.Muted("operations:", color));
            foreach (ApplyOperation operation in preview.Operations)
            {
                writer.WriteLine($"  - {ColorAction(operation.Action, color)} {operation.Kind}: {operation.Type}");
            }
        }

        if (preview.Diff.Added.Length > 0)
        {
            writer.WriteLine(ConsoleTheme.Added("added:", color));
            foreach (RegistrationItem item in preview.Diff.Added)
            {
                writer.WriteLine(ConsoleTheme.Added($"  + {item.Kind}: {DescribeItem(item)}", color));
            }
        }

        if (preview.Diff.Removed.Length > 0)
        {
            writer.WriteLine(ConsoleTheme.Removed("removed:", color));
            foreach (RegistrationItem item in preview.Diff.Removed)
            {
                writer.WriteLine(ConsoleTheme.Removed($"  - {item.Kind}: {DescribeItem(item)}", color));
            }
        }

        if (preview.Diff.Changed.Length > 0)
        {
            writer.WriteLine(ConsoleTheme.Changed("changes:", color));
            foreach (RegistrationChange change in preview.Diff.Changed)
            {
                writer.WriteLine(ConsoleTheme.Changed($"  ~ {change.Kind}: {change.Type}", color));

                // For a Custom API, show which request/response parameters were added, removed, or retyped rather
                // than dumping the whole before/after object - the parameter list is where changes almost always
                // are, and the per-parameter view maps directly to what apply does. Fall back to the object diff
                // when the change is elsewhere (a parent-level field) so nothing is silently hidden.
                List<string> paramChanges = string.Equals(change.Kind, "custom-api", StringComparison.Ordinal)
                    ? DescribeCustomApiParamChanges(change.Before, change.After)
                    : [];
                if (paramChanges.Count > 0)
                {
                    foreach (string line in paramChanges)
                    {
                        writer.WriteLine(ColorParamChange(line, color));
                    }

                    continue;
                }

                string before = DescribeDefinition(change.Before);
                string after = DescribeDefinition(change.After);
                writer.WriteLine($"    before: {ConsoleTheme.Removed(before, color)}");
                writer.WriteLine($"    after : {ConsoleTheme.Added(after, color)}");
            }
        }

        WriteDiagnostics(preview.Diagnostics, writer, color);
    }

    /// <summary>
    /// Renders any plan/apply diagnostics (currently step execution-order collision warnings) as an amber
    /// <c>warnings:</c> block. Shown in the default human summary so a rank collision is visible without
    /// <c>--json</c> or <c>--strict</c>; it stays advisory (never fails the command on its own).
    /// </summary>
    public static void WriteDiagnostics(IReadOnlyList<string> diagnostics, TextWriter writer, bool color)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return;
        }

        writer.WriteLine(ConsoleTheme.Changed($"warnings ({diagnostics.Count}):", color));
        foreach (string diagnostic in diagnostics)
        {
            writer.WriteLine(ConsoleTheme.Changed($"  {ConsoleTheme.Bullet} {diagnostic}", color));
        }
    }

    // Colors a per-parameter diff line by its leading verb so a Custom API change reads like the top-level diff.
    private static string ColorParamChange(string line, bool color)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("+ ", StringComparison.Ordinal))
        {
            return ConsoleTheme.Added(line, color);
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            return ConsoleTheme.Removed(line, color);
        }

        return ConsoleTheme.Changed(line, color);
    }

    /// <summary>
    /// Renders a Custom API change as a per-parameter diff: request inputs and response outputs that were added,
    /// removed, or changed (type, collection-ness, optionality, or entity binding), matched by name. Returns an
    /// empty list when the two definitions have identical parameter sets (the change was in a parent-level field),
    /// so the caller can fall back to the whole-object diff.
    /// </summary>
    public static List<string> DescribeCustomApiParamChanges(JsonElement before, JsonElement after)
    {
        List<string> lines = [];
        DiffParamCollection(before, after, "inputParameters", "input param", lines);
        DiffParamCollection(before, after, "outputParameters", "output param", lines);
        return lines;
    }

    private static void DiffParamCollection(JsonElement before, JsonElement after, string arrayName, string label, List<string> lines)
    {
        Dictionary<string, string> beforeParams = ReadParams(before, arrayName);
        Dictionary<string, string> afterParams = ReadParams(after, arrayName);

        foreach (KeyValuePair<string, string> removed in beforeParams.Where(p => !afterParams.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add($"    - {label}: {removed.Key} {removed.Value}");
        }

        foreach (KeyValuePair<string, string> added in afterParams.Where(p => !beforeParams.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add($"    + {label}: {added.Key} {added.Value}");
        }

        foreach (KeyValuePair<string, string> present in afterParams.Where(p => beforeParams.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string beforeSig = beforeParams[present.Key];
            if (!string.Equals(beforeSig, present.Value, StringComparison.Ordinal))
            {
                lines.Add($"    ~ {label}: {present.Key} {beforeSig} {ConsoleTheme.Arrow} {present.Value}");
            }
        }
    }

    // Reduces a Custom API parameter array to name -> a compact "[type; flags]" signature used for the diff.
    private static Dictionary<string, string> ReadParams(JsonElement definition, string arrayName)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        if (definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty(arrayName, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (JsonElement element in array.EnumerateArray())
        {
            string? name = TryGetString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            List<string> facets = [TryGetString(element, "parameterTypeName") ?? "?"];
            if (TryGetBool(element, "isCollection"))
            {
                facets.Add("collection");
            }

            string? boundEntity = TryGetString(element, "logicalEntityName");
            if (!string.IsNullOrWhiteSpace(boundEntity))
            {
                facets.Add($"entity={boundEntity}");
            }

            // isOptional is absent on response properties (always required), so it only affects request inputs.
            if (element.TryGetProperty("isOptional", out JsonElement optional) && optional.ValueKind == JsonValueKind.True)
            {
                facets.Add("optional");
            }

            map[name!] = $"[{string.Join("; ", facets)}]";
        }

        return map;
    }

    // Colors a diff verb (create/update/delete) to match the added/changed/removed palette; other verbs
    // are left unstyled. No-ops to plain text when the target stream is not a terminal.
    private static string ColorAction(string action, bool color) => action switch
    {
        "create" => ConsoleTheme.Added(action, color),
        "update" => ConsoleTheme.Changed(action, color),
        "delete" => ConsoleTheme.Removed(action, color),
        _ => action,
    };

    public static void WriteInspectSummary(RegistrationState local, RegistrationState current, string? environment, TextWriter writer, bool color, IReadOnlyList<string>? unmanaged = null)
    {
        writer.WriteLine(ConsoleTheme.Heading($"inspect {environment}", color));
        WriteStateSummary("local", local, writer, color);
        WriteStateSummary("current", current, writer, color);
        WriteUnmanaged(unmanaged, writer, color);
    }

    // The plugin types of the deployed assembly Vela does not own (OwnershipScope): information, never a warning.
    public static void WriteUnmanaged(IReadOnlyList<string>? unmanaged, TextWriter writer, bool color)
    {
        if (unmanaged is not { Count: > 0 })
        {
            return;
        }

        writer.WriteLine(ConsoleTheme.Muted("unmanaged (left untouched):", color));
        foreach (string line in unmanaged)
        {
            writer.WriteLine(ConsoleTheme.Muted($"  {ConsoleTheme.Bullet} {line}", color));
        }
    }

    // Informational notices (for example an assembly not yet in Dataverse on a first deployment): never a warning,
    // so --strict does not fail on them.
    public static void WriteInfo(IReadOnlyList<string>? info, TextWriter writer, bool color)
    {
        if (info is not { Count: > 0 })
        {
            return;
        }

        writer.WriteLine(ConsoleTheme.Muted("info:", color));
        foreach (string line in info)
        {
            writer.WriteLine(ConsoleTheme.Muted($"  {ConsoleTheme.Bullet} {line}", color));
        }
    }

    public static void WriteStateSummary(string label, RegistrationState state, TextWriter writer, bool color)
    {
        writer.WriteLine($"{ConsoleTheme.Accent(label, color)} assemblies: {string.Join(", ", state.SourceAssemblies)}");

        RegistrationItem[] steps = state.Items.Where(IsStep).OrderBy(x => x.Type, StringComparer.Ordinal).ToArray();
        RegistrationItem[] customApis = state.Items.Where(x => !IsStep(x)).OrderBy(x => x.Type, StringComparer.Ordinal).ToArray();

        writer.WriteLine($"{ConsoleTheme.Accent(label, color)} steps: {ConsoleTheme.Bold(steps.Length.ToString(), color)}");
        foreach (RegistrationItem item in steps)
        {
            writer.WriteLine($"  {ConsoleTheme.Muted("-", color)} {DescribeItem(item)}");
        }

        writer.WriteLine($"{ConsoleTheme.Accent(label, color)} custom-apis: {ConsoleTheme.Bold(customApis.Length.ToString(), color)}");
        foreach (RegistrationItem item in customApis)
        {
            writer.WriteLine($"  {ConsoleTheme.Muted("-", color)} {DescribeItem(item)}");
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

    public static PlanPreview BuildPlanPreview(string mode, string? environment, RegistrationDiff diff, string[] localDiagnostics, string[] currentDiagnostics, IReadOnlyList<PackagePublishAction>? packageActions = null, string[]? unmanaged = null, string[]? info = null)
    {
        List<ApplyOperation> operations = [];
        operations.AddRange(diff.Added.Select(item => new ApplyOperation("create", item.Kind, item.Type, "Exists locally but not in Dataverse.")));
        operations.AddRange(diff.Removed.Select(item => new ApplyOperation("delete", item.Kind, item.Type, "Exists in Dataverse but not locally.")));
        operations.AddRange(diff.Changed.Select(item => new ApplyOperation("update", item.Kind, item.Type, "Exists in both places but differs.")));

        // A code-only rebuild (unchanged steps/custom APIs, rebuilt assembly) produces no diff item, so the package
        // create/republish is the only signal that apply would deploy anything. Surfaced as its own "package"
        // operation and folded into HasChanges; the numeric Summary stays the non-package registration diff (steps
        // and custom APIs, plus web resources on a --webresources run), mirroring the apply-execute JSON where
        // packageRepublished is a field separate from the added/updated/deleted counts.
        IReadOnlyList<PackagePublishAction> packages = packageActions ?? [];
        operations.AddRange(packages.Select(package => new ApplyOperation(
            package.IsCreate ? "create" : "update",
            "package",
            package.PackageName,
            package.IsCreate
                ? "Plugin package is not in Dataverse; apply will create it."
                : "Plugin package version or content differs from Dataverse; apply will republish it.")));

        return new PlanPreview(
            Mode: mode,
            Environment: environment,
            HasChanges: diff.Summary.Added > 0 || diff.Summary.Removed > 0 || diff.Summary.Changed > 0 || packages.Count > 0,
            Summary: diff.Summary,
            Operations: operations.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToArray(),
            Diff: diff,
            Diagnostics: localDiagnostics.Concat(currentDiagnostics).ToArray(),
            Notes:
            [
                "Compared local .nupkg plugin metadata with live Dataverse state.",
                "This is a preview; nothing was written to Dataverse."
            ],
            Unmanaged: unmanaged ?? [],
            Info: info ?? []);
    }

    // Top-level `vela --help`. Section headers and command names are rendered through Spectre so a real
    // terminal gets the teal branding used by the interactive menu, while the dense, bracket-heavy option
    // and example lines are written with plain Console.WriteLine — Spectre would otherwise treat their
    // '[' / ']' as markup and reflow them to the detected console width. When stdout is redirected
    // (pipes, CI) Spectre strips the styling automatically, so scripts still see clean plain text.
    public static void PrintUsage()
    {
        IAnsiConsole console = AnsiConsole.Console;

        console.MarkupLine($"[bold #6366f1]{Markup.Escape(ToolInfo.ProductName)}[/]  [#22d3ee]v{Markup.Escape(ToolInfo.Version)}[/]");
        console.MarkupLine("[grey]Register Dataverse plugins and Custom APIs from your build packages.[/]");
        console.WriteLine();
        console.MarkupLine("[bold]Usage:[/] vela <command> [[options]]");
        console.WriteLine();

        console.MarkupLine("[bold]Commands:[/]");
        WriteCommand(console, "plan", "Preview registration changes; never writes to Dataverse.");
        Console.WriteLine("      [--package <plugin.nupkg>]... [--packages-dir <folder>] [--package-assembly <name.dll>] [--solution <solutionUniqueName>] [--webresources <folder> (preview)] [connection options] [--timeout <minutes>] [--environment <label>] [--out <file>] [--secure-config <NAME=VALUE|NAME=@file>]... [--interactive] [--strict] [--verbose] [--json]");
        WriteCommand(console, "apply", "Same inputs as plan; writes the changes (prompts for confirmation).");
        Console.WriteLine("      [same args as plan] [--solution <solutionUniqueName>] [--webresources <folder> (preview)] [--auto-approve|--yes] [--batch-size <1-1000>] [--strict] [--verbose] [--json]");
        WriteCommand(console, "inspect", "Compare local packages with the live environment.");
        Console.WriteLine("      [same args as plan] [--verbose] [--json]");
        WriteCommand(console, "init", "Scaffold a new solution on disk (aliases: new).");
        Console.WriteLine("      --name <SolutionName> [--prefix <Prefix>] [--minimal|--no-demos] [--output <dir>] [--no-integrations] [--no-tests] [--no-console] [--core-version <ver>] [--managed-identity] [--web-resources (preview) [--web-resources-namespace <name>] [--web-resources-folder <dir>]] [--json] [--force] [--interactive]");
        WriteCommand(console, "add", "Add a plugin, service, Custom API, test, or step to a project.");
        Console.WriteLine("      (plugin|service|customapi|test|step) <Name> [...]   run 'vela add --help' for the full options");
        WriteCommand(console, "models", "Generate early-bound model classes (C#) or TypeScript (preview).");
        Console.WriteLine("      scope: --solution <uniquename> | --entities <a,b,c> | --custom-apis [<a,b,c>] | --messages [<a,b,c>] | --roles <a,b,c> | --solution-roles (combinable)");
        Console.WriteLine("      [--namespace <ns>] [--out <dir>] [--no-annotations] [--overwrite] [--lang csharp|ts] [--forms and/or --tables (preview, TypeScript)] [connection options] [--timeout <minutes>]");
        Console.WriteLine("      run 'vela models --help' for the full scope/option reference");
        WriteCommand(console, "logs", "Read the plug-in trace log or failed system jobs (read-only; alias: trace).");
        Console.WriteLine("      [--jobs [--all-statuses]] [--errors] [--top <n>] [--since 30m|2h|3d|<date>] [--type <name>] [--message <m>] [--entity <logical>] [--correlation <guid>] [--full] [connection options] [--json]");
        WriteCommand(console, "sign", "Sign a plugin package.");
        Console.WriteLine("      --package <path.nupkg> (--fingerprint <hex> | (--pfx <file> | --pfx-base64 <content>) (--pfx-password <pw> | --pfx-credential <target>)) [--timestamp <url>] [--json]");
        WriteCommand(console, "logout", "Clear the cached interactive-login token and remembered Dataverse URL.");
        WriteCommand(console, "update", "Update the vela global tool to the latest version (or --version <x.y.z>).");
        WriteCommand(console, "menu", "Launch the interactive menu (default with no args).");
        WriteCommand(console, "version", "Print the tool version.");
        console.WriteLine();

        console.MarkupLine("[bold]Output:[/]");
        Console.WriteLine("  plan/apply/inspect print a styled human summary on a terminal, and JSON when piped/redirected.");
        Console.WriteLine("  Force JSON on a terminal with --json (or --out <file>); NO_COLOR / VELA_NO_COLOR disable colors.");
        console.WriteLine();

        console.MarkupLine("[bold]Auth options:[/]");
        Console.WriteLine("  Client secret resolution order: --client-secret <value> (warned), --client-secret-stdin, DATAVERSE_CLIENT_SECRET env var.");
        Console.WriteLine("  --interactive persists the login token (cross-platform cache under ~/.vela) so repeat runs are silent; --fresh-login forces a new prompt, 'vela logout' clears it.");
        Console.WriteLine("  --auth-client-id <appId>   Override the MSAL public client id used for --interactive browser login.");
        Console.WriteLine("  --authority <url>          Override the MSAL authority (e.g. for sovereign clouds).");
        Console.WriteLine("  --timeout <minutes>        Max Dataverse connection timeout in minutes (1-60, default 10; raise for very large orgs).");
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
        Console.WriteLine("  5: Drift detected (vela models --verify)");
        Console.WriteLine("  6: Dataverse connection failure");
        Console.WriteLine("  7: Partial apply (re-run apply to reconcile)");
    }

    // Renders one command row: a colored, aligned name followed by a short summary. Names are padded to a
    // fixed width so summaries line up; the padding is applied before markup so styling doesn't skew it.
    private static void WriteCommand(IAnsiConsole console, string name, string summary)
    {
        console.MarkupLine($"  [#22d3ee]{name.PadRight(9)}[/] [grey]{Markup.Escape(summary)}[/]");
    }

    // Scrubs credential values out of any text before it reaches stderr / CI logs. A connection string embeds
    // ClientSecret=... / Password=... (see CommandLineOptions.BuildClientSecretConnectionString) and the SDK / its
    // LastError can echo it verbatim in an exception message, so redact defense-in-depth on the way out.
    private static readonly Regex SecretPattern = new Regex(
        @"(?i)\b(ClientSecret|Password|Secret)\s*=\s*([^;\r\n]*)",
        RegexOptions.Compiled);

    internal static string RedactSecrets(string? text) =>
        string.IsNullOrEmpty(text) ? (text ?? string.Empty) : SecretPattern.Replace(text, "$1=***");

    public static int Fail(string message, int exitCode, bool includeUsage = true)
    {
        message = RedactSecrets(message);

        // On a real terminal, prefix failures with a red "Error:" so they stand out; when stderr is
        // redirected (pipes, CI, tests) keep the bare message so scripted/asserted output is unchanged.
        Console.Error.WriteLine(ConsoleTheme.StderrColor
            ? $"{ConsoleTheme.Failure("Error:", true)} {message}"
            : message);
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
            Console.Error.WriteLine(RedactSecrets(ex.ToString()));
        }

        return Fail(ex.Message, exitCode, includeUsage: false);
    }

    public static string? Prompt(string label, bool allowEmpty = false, bool secret = false, string? defaultValue = null)
    {
        bool hasDefault = !string.IsNullOrWhiteSpace(defaultValue);
        string? value;
        if (Console.IsInputRedirected)
        {
            // Redirected stdin (tests, pipes, CI): plain read so behavior stays deterministic.
            Console.Write(hasDefault ? $"{label} [{defaultValue}] " : label + " ");
            value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value) && hasDefault)
            {
                value = defaultValue; // empty input accepts the remembered default
            }
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

            // A remembered value is shown as the default; pressing Enter accepts it.
            if (hasDefault)
            {
                prompt.DefaultValue(defaultValue!);
                prompt.ShowDefaultValue = true;
            }

            value = Spectre.Console.AnsiConsole.Prompt(prompt);
        }

        if (allowEmpty)
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
