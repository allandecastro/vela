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
    // scaffold emits the strongly-typed call. Any other name is registered via .OnMessage("...").
    private static readonly string[] KnownMessages =
    [
        "Create", "Update", "CreateMultiple", "UpdateMultiple", "Delete", "Retrieve", "RetrieveMultiple",
        "Associate", "Disassociate", "Merge", "GrantAccess", "ModifyAccess", "RevokeAccess",
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
            "test" => AddTest(args, name, entity, force, projectDir, rootNamespace),
            "step" => AddStep(args, name, entity, projectDir),
            _ => AddCustomApi(args, name, entity, force, projectDir, rootNamespace, modelsNamespace, prefix),
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
            File.WriteAllText(entriesPath, updatedEntries);
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
            File.WriteAllText(entriesPath, updatedEntries);
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
        File.WriteAllText(fullPath, content);
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

        if (!StepInjector.TryInject(source, entity!, message, registrationCall, handler, stageCall, modeCall, postStage, out string updated, out string error))
        {
            return Fail(error, ExitInvalidArguments, includeUsage: false);
        }

        File.WriteAllText(filePath, updated);
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

    private static int AddTest(string[] args, string name, string? entity, bool force, string projectDir, string rootNamespace)
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
            string content = AddPluginTestFile(ns, pluginNamespace, modelsNamespace, testClass, name, entity!);
            return WriteFile(projectDir, relativePath, content, force, $"plugin test {testClass} (for {name})", HasFlag(args, "--json"));
        }

        string uniqueName = $"{ApiPrefix(prefix)}_{name}";
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

        // Create/Update (and their bulk variants) require at least one filtering attribute, and the builder's
        // first argument is mandatory at compile time. Prefer an explicit --filtering-attributes; otherwise
        // auto-detect a sensible default column from the entity's generated early-bound model so the emitted file
        // both compiles AND applies. Fall back to a clear error rather than ever emitting non-compiling code.
        bool needsFiltering = string.Equals(message, "Create", StringComparison.Ordinal)
            || string.Equals(message, "Update", StringComparison.Ordinal)
            || string.Equals(message, "CreateMultiple", StringComparison.Ordinal)
            || string.Equals(message, "UpdateMultiple", StringComparison.Ordinal);

        string filteringCall = string.Empty;
        if (needsFiltering)
        {
            string[] explicitAttributes = SplitList(TryReadOption(args, "--filtering-attributes") ?? TryReadOption(args, "--filter"));
            bool autoSelected = false;
            string[] attributes;
            if (explicitAttributes.Length > 0)
            {
                foreach (string attribute in explicitAttributes)
                {
                    if (!IdentifierPattern.IsMatch(attribute))
                    {
                        return Fail($"Invalid --filtering-attributes value '{attribute}'. Use the early-bound property name(s), e.g. --filtering-attributes Name.", ExitInvalidArguments, includeUsage: false);
                    }
                }

                // A valid C# identifier still may not be a real column on the early-bound model (e.g. a wrong-case
                // 'name' instead of 'Name', which compiles against the base Entity but not the generated property).
                // When the model is available, warn on names that do not match a generated property and suggest the
                // correct casing, so the mistake surfaces here instead of as a later compile error.
                if (TryReadModelPropertyNames(projectDir, entity!, out var modelProperties))
                {
                    foreach (string attribute in explicitAttributes)
                    {
                        if (modelProperties.Contains(attribute))
                        {
                            continue;
                        }

                        string? suggestion = modelProperties.FirstOrDefault(p => string.Equals(p, attribute, StringComparison.OrdinalIgnoreCase));
                        string hint = suggestion is null
                            ? $" It is not a property on the early-bound '{entity}' model; check the name or regenerate the model with 'vela models'."
                            : $" Did you mean '{suggestion}'? Property names are case-sensitive.";
                        Console.Error.WriteLine($"Warning: filtering attribute '{attribute}' may not compile.{hint}");
                    }
                }

                attributes = explicitAttributes;
            }
            else if (TryPickDefaultFilteringAttribute(projectDir, entity!, out string picked))
            {
                attributes = [picked];
                autoSelected = true;
            }
            else
            {
                return Fail($"'{message}' steps require at least one filtering attribute and none could be inferred for '{entity}'. Pass --filtering-attributes <PropertyName[,PropertyName...]> (the early-bound column(s) whose change should trigger the step, e.g. --filtering-attributes Name).", ExitInvalidArguments, includeUsage: false);
            }

            string lambdas = string.Join(", ", attributes.Select(a => $"e => e.{a}"));
            string note = autoSelected ? " /* auto-selected; change to the column(s) that should trigger this step */" : string.Empty;
            filteringCall = $"\n                .FilterOn({lambdas}){note}";
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
        int result = WriteFile(projectDir, relativePath, content, force, $"service {name} (scoped to {entity})", json);
        if (result == ExitSuccess && !json)
        {
            Console.WriteLine("  Register it from your plugin's ConfigureServices(...):");
            Console.WriteLine($"    services.AddScoped<{interfaceName}>(r => new {name}(r.GetRequiredService<IOrganizationService>()));");
        }

        return result;
    }

    private static int AddCustomApi(string[] args, string name, string? entity, bool force, string projectDir, string rootNamespace, string modelsNamespace, string prefix)
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

        // A Custom API's unique name must carry the publisher customization prefix of the solution it is deployed
        // into (Dataverse rejects it otherwise, and `vela apply --solution` validates this). That publisher prefix
        // is NOT the same thing as the .NET project prefix (`vela init --prefix`), and it is often unrelated to the
        // environment name. Default to the project prefix for convenience, but let `--prefix` override it and warn
        // when the default is used so a mismatch is caught before deploy rather than at apply time.
        string? prefixOverride = TryReadOption(args, "--prefix");
        string apiPrefix;
        if (!string.IsNullOrWhiteSpace(prefixOverride))
        {
            string normalized = prefixOverride!.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(normalized, "^[a-z][a-z0-9]{1,7}$"))
            {
                return Fail($"Invalid --prefix '{prefixOverride}'. A Dataverse customization prefix is 2-8 characters, starts with a letter, and is alphanumeric (e.g. 'dv', 'acme').", ExitInvalidArguments, includeUsage: false);
            }

            apiPrefix = normalized;
        }
        else
        {
            apiPrefix = ApiPrefix(prefix);
            if (!HasFlag(args, "--json"))
            {
                Console.Error.WriteLine($"Note: unique name uses the project prefix '{apiPrefix}_'. It must match your target solution's publisher prefix or 'vela apply --solution' will reject it. Override with --prefix <publisherprefix>.");
            }
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

    private static int WriteFile(string projectDir, string relativePath, string content, bool force, string description, bool json = false)
    {
        string fullPath = Path.Combine(projectDir, relativePath);
        if (File.Exists(fullPath) && !force)
        {
            return Fail($"File already exists: {fullPath}. Use --force to overwrite.", ExitInvalidArguments, includeUsage: false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        if (json)
        {
            Console.WriteLine(BuildAddReport(description, [fullPath]));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Created {description}");
            Console.WriteLine($"  {fullPath}");
        }

        return ExitSuccess;
    }

    // The --json success report: what was added and the file(s) it created/updated.
    internal static string BuildAddReport(string description, string[] files) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { description, created = files },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

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

    // Auto-selects a default filtering attribute for a Create/Update step by reading the entity's generated
    // early-bound model. Prefers the conventional primary-name column ("name", else a string column whose logical
    // name ends with "name"), else the first non-Id string column, else the first non-Id column. Returns the C#
    // property name (what the FilteringAttributes lambda references). False when no model / no candidate is found.
    private static bool TryPickDefaultFilteringAttribute(string projectDir, string entity, out string property)
    {
        property = string.Empty;
        if (!TryFindEntityModelFile(projectDir, entity, out string modelPath))
        {
            return false;
        }

        var candidates = new List<(string Property, string Logical, bool IsString)>();
        string? pendingLogical = null;
        foreach (string line in File.ReadLines(modelPath))
        {
            Match logicalMatch = LogicalNameAttr.Match(line);
            if (logicalMatch.Success)
            {
                pendingLogical = logicalMatch.Groups["logical"].Value;
                continue;
            }

            if (pendingLogical == null)
            {
                continue;
            }

            Match propertyMatch = PropertyDecl.Match(line);
            if (propertyMatch.Success)
            {
                string prop = propertyMatch.Groups["name"].Value;
                string type = propertyMatch.Groups["type"].Value;
                if (!string.Equals(prop, "Id", StringComparison.Ordinal))
                {
                    candidates.Add((prop, pendingLogical.ToLowerInvariant(), type.Contains("string", StringComparison.OrdinalIgnoreCase)));
                }

                pendingLogical = null;
            }
        }

        var chosen = candidates.FirstOrDefault(c => c.Logical == "name");
        if (string.IsNullOrEmpty(chosen.Property))
        {
            chosen = candidates.FirstOrDefault(c => c.IsString && c.Logical.EndsWith("name", StringComparison.Ordinal));
        }

        if (string.IsNullOrEmpty(chosen.Property))
        {
            chosen = candidates.FirstOrDefault(c => c.IsString);
        }

        if (string.IsNullOrEmpty(chosen.Property))
        {
            chosen = candidates.FirstOrDefault();
        }

        if (string.IsNullOrEmpty(chosen.Property))
        {
            return false;
        }

        property = chosen.Property;
        return true;
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
        Console.WriteLine("  vela add plugin <Name> --entity <ModelType> [--message <Message>] [--stage <PreValidation|PreOperation|PostOperation>] [--mode <Sync|Async>] [--filtering-attributes <Col1,Col2>] [--project <csproj>] [--force]");
        Console.WriteLine("  vela add service <Name> --entity <ModelType> [--project <csproj>] [--force]");
        Console.WriteLine("  vela add customapi <Name> [--entity <ModelType>] [--bound|--global] [--function|--action] [--project <csproj>] [--force]");
        Console.WriteLine("  vela add test <Name> [--kind plugin|customapi] [--entity <ModelType>] [--project <csproj>] [--force]");
        Console.WriteLine("  vela add step <PluginName> --entity <ModelType> [--message <Message>] [--handler <Method>] [--stage <PreValidation|PreOperation|PostOperation>] [--mode <Sync|Async>] [--project <csproj>]");
        Console.WriteLine("  vela add form-script <entity> [--project <web-resources dir>] [--force]   (preview)");
        Console.WriteLine("  vela add ribbon-script <entity> [--project <web-resources dir>] [--force] (preview, ribbon commands)");
        Console.WriteLine("  vela add --interactive            # guided wizard");
        Console.WriteLine();
        Console.WriteLine("Generates a single file into an existing project, inferring namespaces from its RootNamespace.");
        Console.WriteLine("A service scaffolds a table-scoped interface + implementation; omit --entity on a Custom API for a global (unbound) one.");
        Console.WriteLine("Run 'vela add test' inside your test project (RootNamespace ending in .Tests); <Name> is the class under test.");
        Console.WriteLine("'vela add step' edits an existing handler-based (multi-step) plugin, adding a step + handler method.");
    }
}
