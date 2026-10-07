using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;
using static Vela.Cli.ScaffoldTemplates;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela add</c>: generates a single plugin or Custom API file into an existing project,
/// following the same by-table conventions as <c>vela init</c>. Namespaces are inferred from the target
/// project's <c>RootNamespace</c>, so generated code drops straight into a scaffolded solution.
/// </summary>
internal static class AddCommand
{
    // Class names / entity model types must be valid C# identifiers (kept simple: no dotted names).
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // RootNamespace suffixes that denote a project role; stripped to recover the solution prefix so the
    // shared models namespace ({prefix}.Shared.Models.Generated, where `vela models` writes) can be inferred.

    // SDK messages that have a dedicated On...() builder method, matched case-insensitively so the
    // scaffold emits the strongly-typed call. Any other name is registered via .OnMessage("..."), which the
    // analyzers flag (VELA006) for a modelled message - so this list must cover every typed trigger in StepApi
    // (pinned by AddCommandTests.KnownMessages_CoverEveryTypedTriggerInStepApi).
    internal static readonly string[] KnownMessages =
    [
        "Create", "Update", "CreateMultiple", "UpdateMultiple", "Delete", "Retrieve", "RetrieveMultiple",
        "Associate", "Disassociate", "Assign", "Merge", "GrantAccess", "ModifyAccess", "RevokeAccess",
        "QualifyLead", "WinOpportunity", "LoseOpportunity", "ConvertQuoteToSalesOrder", "ConvertSalesOrderToInvoice", "GenerateQuoteFromOpportunity", "GenerateSalesOrderFromOpportunity", "GenerateInvoiceFromOpportunity", "AddToQueue", "RemoveFromQueue", "PickFromQueue", "RouteTo", "ReleaseToQueue", "SendEmail", "SendEmailFromTemplate", "DeliverIncomingEmail", "DeliverPromoteEmail", "AddMembersTeam", "RemoveMembersTeam", "AddListMembersList", "AddMemberList", "RemoveMemberList", "CloseIncident", "CancelContract", "CloneContract", "RetrievePrincipalAccess", "RetrieveSharedPrincipalsAndAccess",
        "WinQuote", "ReviseQuote", "FulfillSalesOrder", "GetQuoteProductsFromOpportunity", "GetSalesOrderProductsFromOpportunity", "GetInvoiceProductsFromOpportunity",
    ];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintAddUsage();
            return ExitInvalidArguments;
        }

        // Help anywhere (the leading kind or a later `--help`) prints usage and succeeds.
        if (IsHelp(args[0]) || CommandLineOptions.IsHelpRequested(args))
        {
            PrintAddUsage();
            return ExitSuccess;
        }

        // `vela add --interactive` launches the wizard, then re-runs with the composed args. Redirected
        // stdin (CI, pipes) has no terminal to prompt on, so fall back to usage.
        if (HasFlag(args, "--interactive") || HasFlag(args, "-i"))
        {
            if (Console.IsInputRedirected)
            {
                PrintAddUsage();
                return ExitInvalidArguments;
            }

            return Run(InteractiveMenu.PromptAdd());
        }

        // Fail fast on an unknown/misspelled option. The kind and name are positionals, so they pass; the
        // allowlist is the union over all add kinds (per-kind option validity is enforced further down).
        if (!CommandLineOptions.TryTokenize(args, ValueOptions, FlagOptions, out args, out int optExit))
        {
            return optExit;
        }

        string kind = args[0].ToLowerInvariant();

        // Reject an option the kind does not use rather than silently ignoring it (e.g. `add service --message`).
        if (ApplicableOptions.TryGetValue(kind, out HashSet<string>? applicable) && !TryCheckApplicable(kind, args, applicable, out int applicableExit))
        {
            return applicableExit;
        }

        // form-script / ribbon-script target the TypeScript web-resources project (a folder with web-resources.entries.ts),
        // not a .csproj, so they resolve their target themselves and return before the C# project plumbing below.
        if (kind == "form-script")
        {
            return AddFormScript(args);
        }

        if (kind == "ribbon-script")
        {
            return AddRibbonScript(args);
        }

        if (kind is not ("plugin" or "customapi" or "service" or "test" or "step"))
        {
            return Fail($"Unknown 'add' kind '{args[0]}'. Expected 'plugin', 'customapi', 'service', 'test', 'step', 'form-script' or 'ribbon-script'.", ExitInvalidArguments, includeUsage: false);
        }

        string? name = FindPositional(args.Skip(1).ToArray(), ValueOptions);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail($"Missing name. Usage: vela add {kind} <Name> [--entity <ModelType>] [--project <csproj>] [--force]", ExitInvalidArguments, includeUsage: false);
        }

        if (!IdentifierPattern.IsMatch(name))
        {
            return Fail($"Invalid name '{name}'. Use a valid C# identifier (letters, digits, '_'; not starting with a digit).", ExitInvalidArguments, includeUsage: false);
        }

        string? entity = TryReadOption(args, "--entity") ?? TryReadOption(args, "-e");
        if (entity is not null && !IdentifierPattern.IsMatch(entity))
        {
            return Fail($"Invalid entity model type '{entity}'. Use the early-bound class name, e.g. 'Account'.", ExitInvalidArguments, includeUsage: false);
        }

        bool force = HasFlag(args, "--force");

        if (!TryResolveProject(TryReadOption(args, "--project") ?? TryReadOption(args, "-p"), out string projectFile, out string projectError))
        {
            return Fail(projectError, ExitInvalidArguments, includeUsage: false);
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        string rootNamespace = ReadRootNamespace(projectFile);
        string prefix = StripRoleSuffix(rootNamespace);
        string modelsNamespace = $"{prefix}.Shared.Models.Generated";

        return kind switch
        {
            "plugin" => AddPlugin(args, name, entity, force, projectDir, rootNamespace, modelsNamespace),
            "service" => AddService(name, entity, force, projectDir, rootNamespace, modelsNamespace, HasFlag(args, "--json")),
            "test" => AddTest(args, name, entity, force, projectFile, projectDir, rootNamespace),
            "step" => AddStep(args, name, entity, projectDir),
            _ => AddCustomApi(args, name, entity, force, projectFile, projectDir, rootNamespace, modelsNamespace, prefix),
        };
    }

    // vela add form-script <entity>: scaffolds src/<entity>/Forms/main.ts (+ co-located main.test.ts) in the
    // web-resources project and registers its entry in web-resources.entries.ts. Like the rest of `vela add`, it
    // refuses to overwrite an existing file without --force; the entry-map insertion stays idempotent.
    private static int AddFormScript(string[] args)
    {
        string? raw = FindPositional(args.Skip(1).ToArray(), ValueOptions);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fail("Missing table. Usage: vela add form-script <entity> [--project <web-resources dir>] [--force]", ExitInvalidArguments, includeUsage: false);
        }

        // Dataverse logical names are lower-case; accept any casing and normalise so the folder/name are consistent.
        string entity = raw.Trim().ToLowerInvariant();
        if (!FormScriptScaffold.IsValidEntity(entity))
        {
            return Fail($"Invalid table '{raw}'. Use a table logical name (lower-case, letters/digits/'_', e.g. 'account' or 'new_order').", ExitInvalidArguments, includeUsage: false);
        }

        if (!TryResolveWebResourcesProject(TryReadOption(args, "--project") ?? TryReadOption(args, "-p"), out string projectDir, out string error))
        {
            return Fail(error, ExitInvalidArguments, includeUsage: false);
        }

        string tsupPath = Path.Combine(projectDir, "tsup.config.ts");
        string entriesPath = Path.Combine(projectDir, "web-resources.entries.ts");
        string? wrPrefix = FormScriptScaffold.WebResourcePrefixFromTsup(File.ReadAllText(tsupPath));
        if (string.IsNullOrWhiteSpace(wrPrefix))
        {
            return Fail($"Could not read the web resource prefix (globalName) from {tsupPath}.", ExitInvalidArguments, includeUsage: false);
        }

        bool force = HasFlag(args, "--force");
        string formScriptPath = Path.Combine(projectDir, "src", entity, "Forms", "main.ts");
        string testPath = Path.Combine(projectDir, "src", entity, "Forms", "main.test.ts");

        // Match the rest of `vela add`: refuse to clobber an existing file unless --force, rather than silently
        // skipping it. The entry-map insertion stays idempotent (a duplicate key is never re-added).
        if (!force)
        {
            string? existing = new[] { formScriptPath, testPath }.FirstOrDefault(File.Exists);
            if (existing != null)
            {
                return Fail($"File already exists: {existing}. Use --force to overwrite.", ExitInvalidArguments, includeUsage: false);
            }
        }

        var created = new System.Collections.Generic.List<string>();
        WriteScaffoldFile(formScriptPath, FormScriptScaffold.FormScript(entity), created);
        WriteScaffoldFile(testPath, FormScriptScaffold.FormScriptTest(entity), created);

        // Register the tsup entry (idempotent). The map is the source of truth tsup.config.ts and the guard read.
        string entryName = FormScriptScaffold.EntryName(wrPrefix!, entity);
        string sourcePath = FormScriptScaffold.SourcePath(entity);
        if (!FormScriptScaffold.TryInsertEntry(File.ReadAllText(entriesPath), entryName, sourcePath, out string updatedEntries, out bool inserted))
        {
            return Fail($"Could not find the 'export const entries = {{ ... }} as const;' map in {entriesPath}.", ExitInvalidArguments, includeUsage: false);
        }

        if (inserted)
        {
            File.WriteAllText(entriesPath, NormalizeNewlines(updatedEntries));
            created.Add($"{entriesPath} (+ entry '{entryName}')");
        }

        if (HasFlag(args, "--json"))
        {
            Console.WriteLine(BuildAddReport($"form-script {entity}", created.ToArray()));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Added form-script for '{entity}'");
            foreach (string path in created)
            {
                Console.WriteLine($"  {path}");
            }
        }

        return ExitSuccess;
    }

    // vela add ribbon-script <entity>: scaffolds src/<entity>/Ribbon/commands.ts (+ co-located commands.test.ts) in
    // the web-resources project and registers its entry. Same conventions as form-script: refuses to overwrite
    // without --force; the entry-map insertion stays idempotent. The maker wires the button to the printed namespace.
    private static int AddRibbonScript(string[] args)
    {
        string? raw = FindPositional(args.Skip(1).ToArray(), ValueOptions);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fail("Missing table. Usage: vela add ribbon-script <entity> [--project <web-resources dir>] [--force]", ExitInvalidArguments, includeUsage: false);
        }

        string entity = raw.Trim().ToLowerInvariant();
        if (!FormScriptScaffold.IsValidEntity(entity))
        {
            return Fail($"Invalid table '{raw}'. Use a table logical name (lower-case, letters/digits/'_', e.g. 'account' or 'new_order').", ExitInvalidArguments, includeUsage: false);
        }

        if (!TryResolveWebResourcesProject(TryReadOption(args, "--project") ?? TryReadOption(args, "-p"), out string projectDir, out string error))
        {
            return Fail(error, ExitInvalidArguments, includeUsage: false);
        }

        string tsupPath = Path.Combine(projectDir, "tsup.config.ts");
        string entriesPath = Path.Combine(projectDir, "web-resources.entries.ts");
        string? wrPrefix = FormScriptScaffold.WebResourcePrefixFromTsup(File.ReadAllText(tsupPath));
        if (string.IsNullOrWhiteSpace(wrPrefix))
        {
            return Fail($"Could not read the web resource prefix (globalName) from {tsupPath}.", ExitInvalidArguments, includeUsage: false);
        }

        // A form command needs the entity's generated Main form model (its const name depends on the form name), so
        // resolve it from the barrel; this also refuses tables with no generated Main form rather than emit a module
        // that cannot compile.
        string barrelPath = Path.Combine(projectDir, "src", "generated", "index.ts");
        if (!File.Exists(barrelPath))
        {
            return Fail($"No generated barrel at {barrelPath}. Run 'vela models --forms --entities {entity}' first.", ExitInvalidArguments, includeUsage: false);
        }

        if (!RibbonCommandScaffold.TryResolveModel(File.ReadAllText(barrelPath), entity, out string modelConst, out string modelError))
        {
            return Fail(modelError, ExitInvalidArguments, includeUsage: false);
        }

        bool force = HasFlag(args, "--force");
        string commandsPath = Path.Combine(projectDir, "src", entity, "Ribbon", "commands.ts");
        string testPath = Path.Combine(projectDir, "src", entity, "Ribbon", "commands.test.ts");

        if (!force)
        {
            string? existing = new[] { commandsPath, testPath }.FirstOrDefault(File.Exists);
            if (existing != null)
            {
                return Fail($"File already exists: {existing}. Use --force to overwrite.", ExitInvalidArguments, includeUsage: false);
            }
        }

        string ns = RibbonCommandScaffold.Namespace(projectDir, wrPrefix!, entity);
        string contextType = RibbonCommandScaffold.ContextType(entity);

        var created = new System.Collections.Generic.List<string>();
        WriteScaffoldFile(commandsPath, RibbonCommandScaffold.Commands(entity, ns, modelConst, contextType), created);
        WriteScaffoldFile(testPath, RibbonCommandScaffold.CommandsTest(entity, modelConst), created);

        string entryName = RibbonCommandScaffold.EntryName(wrPrefix!, entity);
        string sourcePath = RibbonCommandScaffold.SourcePath(entity);
        if (!FormScriptScaffold.TryInsertEntry(File.ReadAllText(entriesPath), entryName, sourcePath, out string updatedEntries, out bool inserted))
        {
            return Fail($"Could not find the 'export const entries = {{ ... }} as const;' map in {entriesPath}.", ExitInvalidArguments, includeUsage: false);
        }

        if (inserted)
        {
            File.WriteAllText(entriesPath, NormalizeNewlines(updatedEntries));
            created.Add($"{entriesPath} (+ entry '{entryName}')");
        }

        if (HasFlag(args, "--json"))
        {
            Console.WriteLine(BuildAddReport($"ribbon-script {entity}", created.ToArray()));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Added ribbon commands for '{entity}'");
            foreach (string path in created)
            {
                Console.WriteLine($"  {path}");
            }

            Console.WriteLine($"  In the Command Designer, point the button's action at {ns}.<command> (e.g. {ns}.myCommand).");
        }

        return ExitSuccess;
    }

    private static void WriteScaffoldFile(string fullPath, string content, System.Collections.Generic.List<string> created)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, NormalizeNewlines(content));
        created.Add(fullPath);
    }

    // Resolves the web-resources project directory: an explicit --project dir, or the current directory. The
    // directory must contain web-resources.entries.ts and tsup.config.ts (the scaffolded web-resources project).
    private static bool TryResolveWebResourcesProject(string? requested, out string projectDir, out string error)
    {
        projectDir = string.Empty;
        error = string.Empty;

        string candidate = string.IsNullOrWhiteSpace(requested) ? Directory.GetCurrentDirectory() : requested!;
        if (!Directory.Exists(candidate))
        {
            error = $"Directory not found: {candidate}";
            return false;
        }

        if (!File.Exists(Path.Combine(candidate, "web-resources.entries.ts")) || !File.Exists(Path.Combine(candidate, "tsup.config.ts")))
        {
            error = $"'{candidate}' is not a web-resources project (expected web-resources.entries.ts and tsup.config.ts). Run inside the {{prefix}}.WebResources folder or pass --project <dir>.";
            return false;
        }

        projectDir = Path.GetFullPath(candidate);
        return true;
    }

    // The messages whose steps must declare a filtering attribute (FilterOn), shared by 'add plugin', 'add step'
    // and StepInjector so the rule lives in one place.
    // Case-insensitive, like the CLI's message matching (the wizard passes the text as typed). These are also the only
    // messages whose fluent config offers FilterOn.
    internal static bool RequiresFilter(string message) =>
        message.Equals("Create", StringComparison.OrdinalIgnoreCase)
        || message.Equals("Update", StringComparison.OrdinalIgnoreCase)
        || message.Equals("CreateMultiple", StringComparison.OrdinalIgnoreCase)
        || message.Equals("UpdateMultiple", StringComparison.OrdinalIgnoreCase);

    private static string FilterNotSupportedMessage(string message) =>
        $"--filtering-attributes only applies to Create, Update, CreateMultiple and UpdateMultiple steps; a {message} step has no FilterOn. Remove the option.";

    private static string MissingFilterMessage(string message) =>
        $"A {message} step needs a filtering attribute: pass --filtering-attributes <Column[,Column]> with the early-bound property name(s), e.g. --filtering-attributes Name. The step then fires only when the request sets one of them.";

    private static int AddStep(string[] args, string name, string? entity, string projectDir)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return Fail("'vela add step' needs the entity. Pass --entity <ModelType> — it locates <Table>/Plugins/ and types the handler.", ExitInvalidArguments, includeUsage: false);
        }

        string rawMessage = (TryReadOption(args, "--message") ?? "Create").Trim();
        if (!IdentifierPattern.IsMatch(rawMessage))
        {
            return Fail($"Invalid --message '{rawMessage}'. Use an SDK message name (letters, digits, '_'; not starting with a digit).", ExitInvalidArguments, includeUsage: false);
        }

        string? known = KnownMessages.FirstOrDefault(m => string.Equals(m, rawMessage, StringComparison.OrdinalIgnoreCase));
        string message = known ?? rawMessage;
        string registrationCall = known is null ? $"OnMessage(\"{message}\")" : $"On{message}()";

        string handler = (TryReadOption(args, "--handler") ?? $"Handle{message}").Trim();
        if (!IdentifierPattern.IsMatch(handler))
        {
            return Fail($"Invalid --handler '{handler}'. Use a valid C# method name.", ExitInvalidArguments, includeUsage: false);
        }

        if (!TryResolveStageMode(args, out string stageCall, out string modeCall, out bool postStage, out string stageError))
        {
            return Fail(stageError, ExitInvalidArguments, includeUsage: false);
        }

        // Optional --filtering-attributes: when provided, emit a FilterOn selector (with the same identifier
        // validation + wrong-case correction as add plugin). When omitted, add step leaves a TODO comment.
        string[] explicitAttributes = SplitList(TryReadOption(args, "--filtering-attributes") ?? TryReadOption(args, "--filter"));
        string[]? filterAttributes = null;
        if (explicitAttributes.Length > 0 && !RequiresFilter(message))
        {
            return Fail(FilterNotSupportedMessage(message), ExitInvalidArguments, includeUsage: false);
        }

        if (explicitAttributes.Length > 0)
        {
            if (!TryResolveFilteringAttributes(explicitAttributes, projectDir, entity!, out string[] resolved, out string? filteringError))
            {
                return Fail(filteringError!, ExitInvalidArguments, includeUsage: false);
            }

            filterAttributes = resolved;
        }
        else if (RequiresFilter(message))
        {
            return Fail(MissingFilterMessage(message), ExitInvalidArguments, includeUsage: false);
        }

        string filePath = Path.Combine(projectDir, entity!, "Plugins", $"{name}.cs");
        if (!File.Exists(filePath))
        {
            return Fail($"Plugin file not found: {filePath}. 'vela add step' edits an existing plugin — create it first with 'vela add plugin'.", ExitInvalidArguments, includeUsage: false);
        }

        string source = File.ReadAllText(filePath);
        if (!source.Contains($"EntityPluginBase<{entity}>"))
        {
            return Fail($"{filePath} does not look like an EntityPluginBase<{entity}> plugin.", ExitInvalidArguments, includeUsage: false);
        }

        if (!StepInjector.TryInject(source, entity!, message, registrationCall, handler, stageCall, modeCall, postStage, filterAttributes, out string updated, out string error, out string? note))
        {
            return Fail(error, ExitInvalidArguments, includeUsage: false);
        }

        if (note is not null)
        {
            // stderr, so it never pollutes the --json report on stdout.
            Console.Error.WriteLine("Note: " + note);
        }

        File.WriteAllText(filePath, NormalizeNewlines(updated));
        if (HasFlag(args, "--json"))
        {
            Console.WriteLine(BuildAddReport($"step {message} ({handler}) on {name}", [filePath]));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Added step {message} ({handler}) to {name}");
            Console.WriteLine($"  {filePath}");
        }

        return ExitSuccess;
    }

    private static int AddTest(string[] args, string name, string? entity, bool force, string projectFile, string projectDir, string rootNamespace)
    {
        // A test lives in the test project; its plugins project mirrors it without the .Tests suffix.
        if (!rootNamespace.EndsWith(".Tests", StringComparison.Ordinal))
        {
            return Fail("Run 'vela add test' inside your test project (its RootNamespace must end with '.Tests'), or pass --project <Tests.csproj>.", ExitInvalidArguments, includeUsage: false);
        }

        string pluginsRoot = rootNamespace.Substring(0, rootNamespace.Length - ".Tests".Length);
        string prefix = StripRoleSuffix(pluginsRoot);

        string kind = (TryReadOption(args, "--kind") ?? "plugin").Trim().ToLowerInvariant();
        if (kind is not ("plugin" or "customapi"))
        {
            return Fail($"Unsupported --kind '{kind}'. Expected 'plugin' or 'customapi'.", ExitInvalidArguments, includeUsage: false);
        }

        string testClass = $"{name}Tests";

        if (kind == "plugin")
        {
            if (string.IsNullOrWhiteSpace(entity))
            {
                return Fail("A plugin test needs the entity under test. Pass --entity <ModelType> (the early-bound class name, e.g. Account).", ExitInvalidArguments, includeUsage: false);
            }

            string ns = $"{rootNamespace}.{entity}.Plugins";
            string pluginNamespace = $"{pluginsRoot}.{entity}.Plugins";
            string modelsNamespace = $"{prefix}.Shared.Models.Generated";
            string relativePath = Path.Combine(entity!, "Plugins", $"{testClass}.cs");
            // A *Multiple plugin reads ctx.Request.Targets, so the generated test must seed Targets (WithTargets),
            // not Target. --message (optional) tells us which to emit; default single-record.
            string testMessage = (TryReadOption(args, "--message") ?? "Create").Trim();
            // Only CreateMultiple/UpdateMultiple carry a Targets collection; RetrieveMultiple (also ...Multiple)
            // is query-based and keeps a single Target shape, so match the two bulk-write messages explicitly.
            bool multiple = testMessage.Equals("CreateMultiple", StringComparison.OrdinalIgnoreCase)
                || testMessage.Equals("UpdateMultiple", StringComparison.OrdinalIgnoreCase);
            string content = AddPluginTestFile(ns, pluginNamespace, modelsNamespace, testClass, name, entity!, multiple);
            return WriteFile(projectDir, relativePath, content, force, $"plugin test {testClass} (for {name})", HasFlag(args, "--json"));
        }

        if (!TryResolvePublisherPrefix(args, projectFile, prefix, out string testApiPrefix, out int prefixExit))
        {
            return prefixExit;
        }

        string uniqueName = $"{testApiPrefix}_{name}";
        if (string.IsNullOrWhiteSpace(entity))
        {
            string ns = $"{rootNamespace}.GlobalCustomApis";
            string apiNamespace = $"{pluginsRoot}.GlobalCustomApis";
            string relativePath = Path.Combine("GlobalCustomApis", $"{testClass}.cs");
            string content = AddCustomApiTestFile(ns, apiNamespace, testClass, name, uniqueName);
            return WriteFile(projectDir, relativePath, content, force, $"Custom API test {testClass} (for {name})", HasFlag(args, "--json"));
        }
        else
        {
            string ns = $"{rootNamespace}.{entity}.CustomApis";
            string apiNamespace = $"{pluginsRoot}.{entity}.CustomApis";
            string relativePath = Path.Combine(entity!, "CustomApis", $"{testClass}.cs");
            string content = AddCustomApiTestFile(ns, apiNamespace, testClass, name, uniqueName);
            return WriteFile(projectDir, relativePath, content, force, $"Custom API test {testClass} (for {name})", HasFlag(args, "--json"));
        }
    }

    private static int AddPlugin(string[] args, string name, string? entity, bool force, string projectDir, string rootNamespace, string modelsNamespace)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return Fail("A plugin needs an entity. Pass --entity <ModelType> (the early-bound class name, e.g. Account).", ExitInvalidArguments, includeUsage: false);
        }

        string rawMessage = (TryReadOption(args, "--message") ?? "Create").Trim();
        if (!IdentifierPattern.IsMatch(rawMessage))
        {
            return Fail($"Invalid --message '{rawMessage}'. Use an SDK message name (letters, digits, '_'; not starting with a digit), e.g. Update or a custom message.", ExitInvalidArguments, includeUsage: false);
        }

        // Messages with a dedicated On...() builder method get the strongly-typed call; any other name is
        // treated as a custom SDK message and registered through .OnMessage("...").
        string? known = KnownMessages.FirstOrDefault(m => string.Equals(m, rawMessage, StringComparison.OrdinalIgnoreCase));
        string message = known ?? rawMessage;
        string registrationCall = known is null ? $"OnMessage(\"{message}\")" : $"On{message}()";

        if (!TryResolveStageMode(args, out string stageCall, out string modeCall, out bool postStage, out string stageError))
        {
            return Fail(stageError, ExitInvalidArguments, includeUsage: false);
        }

        // Create/Update (and their bulk variants) require a FilterOn: the builder (and VELA018) reject such a step
        // without one, so the caller must name the column(s). We do NOT guess one from the model.
        string filteringCall = string.Empty;
        if (!RequiresFilter(message) && SplitList(TryReadOption(args, "--filtering-attributes") ?? TryReadOption(args, "--filter")).Length > 0)
        {
            return Fail(FilterNotSupportedMessage(message), ExitInvalidArguments, includeUsage: false);
        }

        if (RequiresFilter(message))
        {
            string[] explicitAttributes = SplitList(TryReadOption(args, "--filtering-attributes") ?? TryReadOption(args, "--filter"));
            if (explicitAttributes.Length > 0)
            {
                // Validate the identifiers and, when the model is available, correct wrong casing to the real
                // property (e.g. 'name' -> 'Name') so the emitted FilterOn compiles.
                if (!TryResolveFilteringAttributes(explicitAttributes, projectDir, entity!, out string[] attributes, out string? filteringError))
                {
                    return Fail(filteringError!, ExitInvalidArguments, includeUsage: false);
                }

                string lambdas = string.Join(", ", attributes.Select(a => $"e => e.{a}"));
                filteringCall = $"\n                .FilterOn({lambdas})";
            }
            else
            {
                return Fail(MissingFilterMessage(message), ExitInvalidArguments, includeUsage: false);
            }
        }

        // The redesigned builder ends the chain with .Handle(...), bound to a named instance handler. A modelled
        // message gets the strongly-typed post-stage context (I{Message}PostContext<TEntity>); a custom OnMessage
        // step has no typed context, so its handler takes the plain ILocalPluginContext.
        string handlerContextType = known is null ? "ILocalPluginContext" : $"I{message}{(postStage ? "Post" : "Pre")}Context<{entity}>";

        string ns = $"{rootNamespace}.{entity}.Plugins";
        string relativePath = Path.Combine(entity!, "Plugins", $"{name}.cs");
        string content = AddPluginFile(ns, name, entity!, modelsNamespace, message, registrationCall, stageCall, modeCall, filteringCall, handlerContextType);
        return WriteFile(projectDir, relativePath, content, force, $"plugin {name} ({message} {entity})", HasFlag(args, "--json"));
    }

    private static int AddService(string name, string? entity, bool force, string projectDir, string rootNamespace, string modelsNamespace, bool json)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return Fail("A service needs an entity. Pass --entity <ModelType> (the early-bound class name, e.g. Account); services scaffold table-scoped like 'vela init'.", ExitInvalidArguments, includeUsage: false);
        }

        string interfaceName = $"I{name}";
        string ns = $"{rootNamespace}.{entity}.Services";
        string relativePath = Path.Combine(entity!, "Services", $"{name}.cs");
        string content = AddServiceFile(ns, interfaceName, name, entity!, modelsNamespace);
        // The exact line to wire the service into a plugin's ConfigureServices. Surfaced both in the human
        // output and in the --json report (field "registration") so an agent driving the CLI gets it too.
        string registration = $"services.AddScoped<{interfaceName}>(r => new {name}(r.GetRequiredService<IOrganizationService>()));";
        return WriteFile(projectDir, relativePath, content, force, $"service {name} (scoped to {entity})", json, registration);
    }

    private static int AddCustomApi(string[] args, string name, string? entity, bool force, string projectFile, string projectDir, string rootNamespace, string modelsNamespace, string prefix)
    {
        // --function / --action choose the OperationType (default Action). Functions return data and are
        // typically read-only; actions can mutate. Mutually exclusive.
        bool asFunction = HasFlag(args, "--function");
        bool asAction = HasFlag(args, "--action");
        if (asFunction && asAction)
        {
            return Fail("Use either --function or --action, not both.", ExitInvalidArguments, includeUsage: false);
        }

        string operation = asFunction ? "AsFunction()" : "AsAction()";

        // --bound / --global make binding explicit; otherwise it is inferred from --entity.
        bool bound = HasFlag(args, "--bound");
        bool global = HasFlag(args, "--global");
        if (bound && global)
        {
            return Fail("Use either --bound or --global, not both.", ExitInvalidArguments, includeUsage: false);
        }

        bool generateGlobal;
        if (global)
        {
            if (!string.IsNullOrWhiteSpace(entity))
            {
                return Fail("--global takes no --entity (a global Custom API is unbound).", ExitInvalidArguments, includeUsage: false);
            }

            generateGlobal = true;
        }
        else if (bound)
        {
            if (string.IsNullOrWhiteSpace(entity))
            {
                return Fail("--bound needs --entity <ModelType> (the table the Custom API binds to).", ExitInvalidArguments, includeUsage: false);
            }

            generateGlobal = false;
        }
        else
        {
            generateGlobal = string.IsNullOrWhiteSpace(entity);
        }

        if (!TryResolvePublisherPrefix(args, projectFile, prefix, out string apiPrefix, out int prefixExit))
        {
            return prefixExit;
        }

        string uniqueName = $"{apiPrefix}_{name}";

        if (generateGlobal)
        {
            string ns = $"{rootNamespace}.GlobalCustomApis";
            string relativePath = Path.Combine("GlobalCustomApis", $"{name}.cs");
            string content = AddGlobalCustomApiFile(ns, name, uniqueName, operation);
            return WriteFile(projectDir, relativePath, content, force, $"global Custom API {name} ({uniqueName})", HasFlag(args, "--json"));
        }
        else
        {
            string ns = $"{rootNamespace}.{entity}.CustomApis";
            string relativePath = Path.Combine(entity!, "CustomApis", $"{name}.cs");
            string content = AddBoundCustomApiFile(ns, name, uniqueName, entity!, modelsNamespace, operation);
            return WriteFile(projectDir, relativePath, content, force, $"Custom API {name} bound to {entity} ({uniqueName})", HasFlag(args, "--json"));
        }
    }

    // A Custom API's unique name must carry the publisher customization prefix of the solution it is deployed into
    // (Dataverse rejects it otherwise, and `vela apply --solution` validates this). That publisher prefix is NOT the
    // .NET project prefix. Resolution: --prefix, else the VelaPublisherPrefix recorded by `vela init --publisher-prefix`
    // in the target project, else the project prefix (with a note, since it is a guess). A value that is not a valid
    // publisher prefix (2-8 chars, letter first, alphanumeric) is refused rather than emitted into an unregistrable name.
    private static bool TryResolvePublisherPrefix(string[] args, string projectFile, string projectPrefix, out string apiPrefix, out int exitCode)
    {
        exitCode = ExitSuccess;
        string? explicitPrefix = TryReadOption(args, "--prefix");
        if (!string.IsNullOrWhiteSpace(explicitPrefix))
        {
            apiPrefix = explicitPrefix!.Trim().ToLowerInvariant();
            if (!ScaffoldOptions.IsValidPublisherPrefix(apiPrefix))
            {
                exitCode = Fail($"Invalid --prefix '{explicitPrefix}'. A Dataverse customization prefix is 2-8 characters, starts with a letter, and is alphanumeric (e.g. 'dv', 'acme').", ExitInvalidArguments, includeUsage: false);
                return false;
            }

            return true;
        }

        string? recorded = ReadPublisherPrefix(projectFile);
        if (recorded is not null)
        {
            apiPrefix = recorded;
            if (!ScaffoldOptions.IsValidPublisherPrefix(apiPrefix))
            {
                exitCode = Fail($"The VelaPublisherPrefix '{recorded}' in {projectFile} is not a valid publisher prefix (2-8 characters, letter first, alphanumeric). Fix it or pass --prefix <publisherprefix>.", ExitInvalidArguments, includeUsage: false);
                return false;
            }

            return true;
        }

        apiPrefix = ApiPrefix(projectPrefix);
        if (!ScaffoldOptions.IsValidPublisherPrefix(apiPrefix))
        {
            exitCode = Fail($"The project prefix '{apiPrefix}' is not a valid Dataverse publisher prefix (2-8 characters, letter first, alphanumeric), so it cannot name a Custom API. Pass --prefix <publisherprefix> with your solution publisher's prefix.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        if (!HasFlag(args, "--json"))
        {
            Console.Error.WriteLine($"Note: unique name uses the project prefix '{apiPrefix}_'. It must match your target solution's publisher prefix or 'vela apply --solution' will reject it. Override with --prefix <publisherprefix>, or record it with 'vela init --force --publisher-prefix <prefix>'.");
        }

        return true;
    }

    // Reads the <VelaPublisherPrefix> recorded in a project file, or null when absent or unreadable.
    private static string? ReadPublisherPrefix(string projectFile)
    {
        try
        {
            string? value = System.Xml.Linq.XDocument.Load(projectFile).Descendants("VelaPublisherPrefix").FirstOrDefault()?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The options each kind uses. Anything else in the (union) allowlist is rejected for that kind.
    private static readonly HashSet<string> CommonOptions = new(StringComparer.OrdinalIgnoreCase)
        { "--project", "-p", "--json", "--help", "-h", "--interactive", "-i" };

    private static HashSet<string> With(params string[] options) =>
        new(CommonOptions.Concat(options), StringComparer.OrdinalIgnoreCase);

    internal static readonly IReadOnlyDictionary<string, HashSet<string>> ApplicableOptions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["plugin"] = With("--force", "--entity", "-e", "--message", "--stage", "--mode", "--filtering-attributes", "--filter"),
        ["service"] = With("--force", "--entity", "-e"),
        ["customapi"] = With("--force", "--entity", "-e", "--bound", "--global", "--function", "--action", "--prefix"),
        ["test"] = With("--force", "--entity", "-e", "--kind", "--message", "--prefix"),
        // step edits an existing file in place, so it has no --force.
        ["step"] = With("--entity", "-e", "--message", "--handler", "--stage", "--mode", "--filtering-attributes", "--filter"),
        ["form-script"] = With("--force"),
        ["ribbon-script"] = With("--force"),
    };

    private static bool TryCheckApplicable(string kind, string[] args, HashSet<string> applicable, out int exitCode)
    {
        exitCode = ExitSuccess;
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "--")
            {
                break;
            }

            bool isValue = ValueOptions.Contains(token);
            if (!isValue && !FlagOptions.Contains(token))
            {
                continue; // a positional (the tokenizer already rejected unknown options)
            }

            if (!applicable.Contains(token))
            {
                exitCode = Fail($"Option '{token}' does not apply to 'vela add {kind}'. Run 'vela add --help' for each kind's options.", ExitInvalidArguments, includeUsage: false);
                return false;
            }

            if (isValue)
            {
                i++; // skip the option's value
            }
        }

        return true;
    }

    private static int WriteFile(string projectDir, string relativePath, string content, bool force, string description, bool json = false, string? registration = null)
    {
        string fullPath = Path.Combine(projectDir, relativePath);
        if (File.Exists(fullPath) && !force)
        {
            return Fail($"File already exists: {fullPath}. Use --force to overwrite.", ExitInvalidArguments, includeUsage: false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, NormalizeNewlines(content));
        if (json)
        {
            Console.WriteLine(BuildAddReport(description, [fullPath], registration));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Created {description}");
            Console.WriteLine($"  {fullPath}");
            if (registration is not null)
            {
                Console.WriteLine("  Register it from your plugin's ConfigureServices(...):");
                Console.WriteLine($"    {registration}");
            }
        }

        return ExitSuccess;
    }

    // The --json success report: what was added, the file(s) it created/updated, and (for a service) the exact
    // ConfigureServices registration line. The "registration" field is omitted when there is nothing to wire up.
    internal static string BuildAddReport(string description, string[] files, string? registration = null) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { description, created = files, registration },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    // Resolves the target .csproj: an explicit path/dir, or the single .csproj in the current directory.
    private static bool TryResolveProject(string? requested, out string projectFile, out string error)
    {
        projectFile = string.Empty;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (File.Exists(requested) && requested.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projectFile = requested;
                return true;
            }

            if (Directory.Exists(requested))
            {
                return TrySingleCsproj(requested, out projectFile, out error);
            }

            error = $"Project not found: {requested}";
            return false;
        }

        return TrySingleCsproj(Directory.GetCurrentDirectory(), out projectFile, out error);
    }

    private static bool TrySingleCsproj(string directory, out string projectFile, out string error)
    {
        projectFile = string.Empty;
        error = string.Empty;

        string? single = SingleCsproj(directory, out string[] candidates);
        if (single != null)
        {
            projectFile = single;
            return true;
        }

        error = candidates.Length == 0
            ? $"No .csproj found in '{directory}'. Run inside a project directory or pass --project <csproj>."
            : $"Multiple .csproj files found in '{directory}'. Disambiguate with --project <csproj>.";
        return false;
    }

    // Reads <RootNamespace>, falling back to <AssemblyName>, then the file name without extension.
    private static bool IsHelp(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "help";

    // Resolves --stage / --mode into the fluent builder calls and whether the step runs at a post stage
    // (which selects the pre- vs post- typed handler context). Defaults: PostOperation + Sync (the prior
    // hard-coded behaviour). Dataverse only allows asynchronous steps at PostOperation.
    private static bool TryResolveStageMode(string[] args, out string stageCall, out string modeCall, out bool postStage, out string error)
    {
        stageCall = ".PostOperation()";
        modeCall = ".Sync()";
        postStage = true;
        error = string.Empty;

        string? rawStage = TryReadOption(args, "--stage");
        if (!string.IsNullOrWhiteSpace(rawStage))
        {
            switch (rawStage.Trim().ToLowerInvariant())
            {
                case "prevalidation": stageCall = ".PreValidation()"; postStage = false; break;
                case "preoperation" or "pre": stageCall = ".PreOperation()"; postStage = false; break;
                case "postoperation" or "post": stageCall = ".PostOperation()"; postStage = true; break;
                default:
                    error = $"Invalid --stage '{rawStage}'. Use PreValidation, PreOperation or PostOperation.";
                    return false;
            }
        }

        string? rawMode = TryReadOption(args, "--mode");
        if (!string.IsNullOrWhiteSpace(rawMode))
        {
            switch (rawMode.Trim().ToLowerInvariant())
            {
                case "sync" or "synchronous": modeCall = ".Sync()"; break;
                case "async" or "asynchronous": modeCall = ".Async()"; break;
                default:
                    error = $"Invalid --mode '{rawMode}'. Use Sync or Async.";
                    return false;
            }
        }

        if (modeCall == ".Async()" && !postStage)
        {
            error = "An asynchronous step must run at PostOperation. Use --mode Sync for a pre-stage, or --stage PostOperation.";
            return false;
        }

        return true;
    }

    // Validates explicit --filtering-attributes identifiers and, when the generated model is available,
    // corrects a wrong-case value to the real property (e.g. 'name' -> 'Name', which compiles) or warns when
    // the name is unknown. Returns the (possibly corrected) attributes; false only on an invalid C# identifier.
    private static bool TryResolveFilteringAttributes(string[] explicitAttributes, string projectDir, string entity, out string[] attributes, out string? error)
    {
        attributes = explicitAttributes;
        error = null;

        foreach (string attribute in explicitAttributes)
        {
            if (!IdentifierPattern.IsMatch(attribute))
            {
                error = $"Invalid --filtering-attributes value '{attribute}'. Use the early-bound property name(s), e.g. --filtering-attributes Name.";
                return false;
            }
        }

        if (explicitAttributes.Length == 0 || !TryReadModelPropertyNames(projectDir, entity, out var modelProperties))
        {
            return true;
        }

        string[] corrected = new string[explicitAttributes.Length];
        for (int i = 0; i < explicitAttributes.Length; i++)
        {
            string attribute = explicitAttributes[i];
            if (modelProperties.Contains(attribute))
            {
                corrected[i] = attribute;
                continue;
            }

            string? suggestion = modelProperties.FirstOrDefault(p => string.Equals(p, attribute, StringComparison.OrdinalIgnoreCase));
            if (suggestion is not null)
            {
                Console.Error.WriteLine($"Note: filtering attribute '{attribute}' corrected to '{suggestion}' (property names are case-sensitive).");
                corrected[i] = suggestion;
            }
            else
            {
                Console.Error.WriteLine($"Warning: filtering attribute '{attribute}' is not a property on the early-bound '{entity}' model; check the name or regenerate the model with 'vela models'.");
                corrected[i] = attribute;
            }
        }

        attributes = corrected;
        return true;
    }

    // Splits a comma-separated option value into trimmed, non-empty tokens.
    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Property + logical name preceding attribute on a generated early-bound model.
    private static readonly Regex LogicalNameAttr = new("\\[AttributeLogicalName\\(\"(?<logical>[^\"]+)\"\\)\\]", RegexOptions.Compiled);
    private static readonly Regex PropertyDecl = new("^\\s*public\\s+(?<type>[\\w\\.<>\\?\\[\\],\\s]+?)\\s+(?<name>\\w+)\\s*$", RegexOptions.Compiled);

    // Reads the set of generated property names off the entity's early-bound model (each declared right after an
    // [AttributeLogicalName("...")] attribute). Used to sanity-check explicit --filtering-attributes against the
    // real columns. False when the model file cannot be located (then no validation is attempted).
    private static bool TryReadModelPropertyNames(string projectDir, string entity, out HashSet<string> properties)
    {
        properties = new HashSet<string>(StringComparer.Ordinal);
        if (!TryFindEntityModelFile(projectDir, entity, out string modelPath))
        {
            return false;
        }

        bool sawLogicalName = false;
        foreach (string line in File.ReadLines(modelPath))
        {
            if (LogicalNameAttr.IsMatch(line))
            {
                sawLogicalName = true;
                continue;
            }

            if (!sawLogicalName)
            {
                continue;
            }

            Match propertyMatch = PropertyDecl.Match(line);
            if (propertyMatch.Success)
            {
                properties.Add(propertyMatch.Groups["name"].Value);
                sawLogicalName = false;
            }
        }

        return properties.Count > 0;
    }

    // Locates the generated early-bound model file for an entity: walks up from the target project to the solution
    // root (a directory holding a .sln, else the highest reached), then finds a "{entity}.cs" that declares
    // "class {entity} : Entity", skipping build output. False when not found.
    private static bool TryFindEntityModelFile(string projectDir, string entity, out string path)
    {
        path = string.Empty;

        // Bound the search to the solution root (a directory holding a .sln) so auto-detection can reach a sibling
        // models project. If no .sln is found within a few levels, fall back to the project directory itself rather
        // than climbing toward the filesystem root and scanning an enormous tree.
        string root = projectDir;
        string? dir = projectDir;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            if (Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                root = dir;
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        var classDecl = new Regex($"class\\s+{Regex.Escape(entity)}\\s*:\\s*Entity", RegexOptions.Compiled);
        foreach (string file in Directory.EnumerateFiles(root, entity + ".cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (classDecl.IsMatch(text))
            {
                path = file;
                return true;
            }
        }

        return false;
    }

    // Options that consume the next token as their value; everything else may be a positional. Also the
    // value-option half of the unknown-option allowlist (union over all add kinds). Keep in sync with the kinds.
    internal static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
        { "--entity", "-e", "--project", "-p", "--message", "--kind", "--handler", "--filtering-attributes", "--filter", "--prefix", "--stage", "--mode" };

    // Boolean flags across all add kinds (customapi binding/operation, --force/--json, interactive, help).
    internal static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--force", "--json", "--bound", "--global", "--function", "--action", "--interactive", "-i", "--help", "-h",
    };

    private static void PrintAddUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  vela add plugin <Name> --entity <ModelType> [--message <Message>] [--stage <PreValidation|PreOperation|PostOperation>] [--mode <Sync|Async>] [--filtering-attributes|--filter <Col1,Col2>] [--project <csproj>] [--force] [--json]");
        Console.WriteLine("  vela add service <Name> --entity <ModelType> [--project <csproj>] [--force] [--json]");
        Console.WriteLine("  vela add customapi <Name> [--entity <ModelType>] [--bound|--global] [--function|--action] [--prefix <publisherprefix>] [--project <csproj>] [--force] [--json]");
        Console.WriteLine("  vela add test <Name> [--kind plugin|customapi] [--entity <ModelType>] [--message <Message>] [--prefix <publisherprefix>] [--project <csproj>] [--force] [--json]");
        Console.WriteLine("  vela add step <PluginName> --entity <ModelType> [--message <Message>] [--handler <Method>] [--stage <PreValidation|PreOperation|PostOperation>] [--mode <Sync|Async>] [--filtering-attributes|--filter <Col1,Col2>] [--project <csproj>] [--json]");
        Console.WriteLine("  vela add form-script <entity> [--project <web-resources dir>] [--force]   (preview)");
        Console.WriteLine("  vela add ribbon-script <entity> [--project <web-resources dir>] [--force] (preview, ribbon commands)");
        Console.WriteLine("  vela add --interactive            # guided wizard");
        Console.WriteLine();
        Console.WriteLine("Generates a single file into an existing project, inferring namespaces from its RootNamespace.");
        Console.WriteLine("A service scaffolds a table-scoped interface + implementation; omit --entity on a Custom API for a global (unbound) one.");
        Console.WriteLine("Create/Update(+Multiple) steps need --filtering-attributes. A Custom API is named <publisherprefix>_<Name>: --prefix, else the");
        Console.WriteLine("project's VelaPublisherPrefix (vela init --publisher-prefix), else the project prefix.");
        Console.WriteLine("Run 'vela add test' inside your test project (RootNamespace ending in .Tests); <Name> is the class under test.");
        Console.WriteLine("'vela add step' edits an existing handler-based (multi-step) plugin, adding a step + handler method.");
    }
}
