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
    // scaffold emits the strongly-typed call. Any other name is registered via its request type, On<XxxRequest>().
    // They are SDK request names: the sdkmessage a step registers on can be shorter (SendEmail runs as Send), see
    // ResolveKnownMessage. The list is generated from the step builder's message model (Vela.Steps.TypedTriggers,
    // linked as source), so it never drifts from the typed triggers.
    internal static readonly string[] KnownMessages = Vela.Steps.TypedTriggers.Messages;

    // The typed On...() trigger for --message: a request name (SendEmail), or the sdkmessage it runs as (Send),
    // resolved through the table when several requests share it (Win is WinQuote on quote, WinOpportunity on
    // opportunity). Null for a message with no curated trigger, registered through its request type (RegistrationCall).
    internal static string? ResolveKnownMessage(string rawMessage, string entity)
        => ResolveRequestName(rawMessage, entity, out _) is { } request ? FindKnownMessage(request) : null;

    private static string? FindKnownMessage(string request)
        => KnownMessages.FirstOrDefault(m => string.Equals(m, request, StringComparison.OrdinalIgnoreCase));

    // The request --message names. A request name (SendEmail, CloseQuote), or a name that is no other request's
    // sdkmessage (Create, a custom API), is itself. An sdkmessage the platform runs other requests as (Close, Send,
    // SetStateDynamicEntity) is the request of that sdkmessage on the entity's table, or the one bound to no table:
    // Close on quote is CloseQuote, SetStateDynamicEntity is SetState. No request type is ever synthesized from such an
    // sdkmessage (there is no CloseRequest), so when none of its requests runs on the table, or several could, this is
    // null and error says which request names to pass instead.
    internal static string? ResolveRequestName(string rawMessage, string entity, out string error)
    {
        error = string.Empty;
        if (FindKnownMessage(rawMessage) is { } known)
        {
            return known;
        }

        string[] requests = Vela.Steps.SdkMessageNames.RequestNamesFor(rawMessage).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        if (requests.Length == 0)
        {
            return rawMessage;
        }

        string[] onTable = requests.Where(r => string.Equals(Vela.Steps.SdkMessageNames.PrimaryEntityOf(r), entity, StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] candidates = onTable.Length > 0 ? onTable : requests.Where(r => Vela.Steps.SdkMessageNames.PrimaryEntityOf(r) is null).ToArray();
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        string Describe(string request) => Vela.Steps.SdkMessageNames.PrimaryEntityOf(request) is { } table ? $"{request} on {table}" : request;
        error = candidates.Length == 0
            ? $"'{rawMessage}' runs only as {string.Join(", ", requests.Select(Describe))}, none of them on {entity}. Check --entity, or pass --message with one of these request names."
            : $"'{rawMessage}' is the sdkmessage of several requests ({string.Join(", ", candidates)}). Pass --message with the request name.";
        return null;
    }

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

        string? entity = TryReadOption(args, "--entity") ?? TryReadOption(args, "-e");
        if (entity is not null && !IdentifierPattern.IsMatch(entity))
        {
            return Fail($"Invalid entity model type '{entity}'. Use the early-bound class name, e.g. 'Account'.", ExitInvalidArguments, includeUsage: false);
        }

        string? name = FindPositional(args.Skip(1).ToArray(), ValueOptions);
        if (string.IsNullOrWhiteSpace(name) && kind == "plugin")
        {
            // A plugin's name is optional: it defaults to <Entity>Plugin, so --entity (required anyway) names it.
            if (string.IsNullOrWhiteSpace(entity))
            {
                return Fail("A plugin needs an entity. Pass --entity <ModelType> (the early-bound class name, e.g. Account); without a name, the plugin is named <ModelType>Plugin.", ExitInvalidArguments, includeUsage: false);
            }

            name = DefaultPluginName(entity!);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail($"Missing name. Usage: vela add {kind} <Name> [--entity <ModelType>] [--project <csproj>] [--force]", ExitInvalidArguments, includeUsage: false);
        }

        if (!IdentifierPattern.IsMatch(name))
        {
            return Fail($"Invalid name '{name}'. Use a valid C# identifier (letters, digits, '_'; not starting with a digit).", ExitInvalidArguments, includeUsage: false);
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

    // SetStateRequest is deprecated in favour of an Update of statecode, whose Update steps also run for a caller that
    // still sends SetStateRequest, so the note points at the Update step.
    internal const string SetStateDeprecatedNote =
        "Note: SetStateRequest is deprecated (Microsoft: use an Update of statecode). An Update step filtered on statecode " +
        "also runs when a caller still sends SetStateRequest: prefer '--message Update --filtering-attributes StateCode'. " +
        "Keep On<SetStateRequest>() only for logic that needs its own inputs (EntityMoniker, State, Status).";

    // The trigger for --message: the curated On{Message}() when Vela models it, else On<XxxRequest>() over the
    // message's request type - the SDK class (fully qualified, so no using is needed) when one ships, otherwise the
    // request `vela models --messages` generates into the models namespace (a note says to generate it).
    internal static string RegistrationCall(string? known, string message)
    {
        if (known is not null)
        {
            return $"On{known}()";
        }

        if (SdkMessageClassResolver.TryGetSdkRequestTypeName(message, out string sdkType))
        {
            // ResolveRequestName already maps the SetStateDynamicEntity sdkmessage to its SetState request.
            if (message.Equals("SetState", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(SetStateDeprecatedNote);
            }

            return $"On<{sdkType}>()";
        }

        Console.Error.WriteLine($"Note: '{message}' is registered through On<{message}Request>(). Generate the request with 'vela models --messages {message}' (or '--custom-apis {message}' for a Custom API) if it does not exist yet.");
        return $"On<{message}Request>()";
    }

    // The handler parameter type: the {Handler}Context the image projection generator emits, nested in the (partial)
    // plugin class, for every named handler `add` can bind - a typed On{Message}() trigger (each has its
    // Vela.Context.{Message}Pre/PostContext) and an On<TRequest>() trigger (MessagePre/PostContext<TRequest, TEntity>).
    // One signature for every step, and it stays the same when an image is captured later.
    internal static string HandlerContextType(string handlerName) => handlerName + "Context";

    // The handler a step binds to when --handler is not passed, the same for `vela add plugin` and `vela add step`.
    internal static string DefaultHandlerName(string message) => $"Handle{message}";

    // The class `vela add plugin` writes when no name is passed.
    internal static string DefaultPluginName(string entity) => $"{entity}Plugin";

    // The --handler of `add plugin` / `add step`, else the default Handle{Message}; null (with the error) when invalid.
    private static string? ResolveHandler(string[] args, string message, out string error)
    {
        error = string.Empty;
        string handler = (TryReadOption(args, "--handler") ?? DefaultHandlerName(message)).Trim();
        if (!IdentifierPattern.IsMatch(handler))
        {
            error = $"Invalid --handler '{handler}'. Use a valid C# method name.";
            return null;
        }

        return handler;
    }

    // The TRequest of an On<TRequest>() registration call.
    internal static bool TryGetRequestType(string registrationCall, out string requestType)
    {
        requestType = string.Empty;
        if (!registrationCall.StartsWith("On<", StringComparison.Ordinal) || !registrationCall.EndsWith(">()", StringComparison.Ordinal))
        {
            return false;
        }

        requestType = registrationCall.Substring(3, registrationCall.Length - 6);
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

        string? request = ResolveRequestName(rawMessage, entity, out string messageError);
        if (request is null)
        {
            return Fail(messageError, ExitInvalidArguments, includeUsage: false);
        }

        string? known = FindKnownMessage(request);
        string message = known ?? request;
        string registrationCall = RegistrationCall(known, message);

        string? handler = ResolveHandler(args, message, out string handlerError);
        if (handler is null)
        {
            return Fail(handlerError, ExitInvalidArguments, includeUsage: false);
        }

        if (!TryResolveStageMode(args, out string stageCall, out string modeCall, out string stageError))
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

        if (!StepInjector.TryInject(source, message, registrationCall, handler, stageCall, modeCall, filterAttributes, out string updated, out string error, out string? note))
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

        // The class under test is read from the source of the projects the test project references (else the
        // solution), so the test names the real class, namespace, steps, unique name and request type.
        List<AddTestSources.SourceClass> classes = AddTestSources.FindClasses(AddTestSources.SearchRoots(projectFile, projectDir));
        string modelsNamespace = $"{prefix}.Shared.Models.Generated";
        bool json = HasFlag(args, "--json");

        if (kind == "plugin")
        {
            AddTestSources.SourceClass? plugin = classes.FirstOrDefault(c => c.Name == name && AddTestSources.PluginEntity(c) is not null)
                ?? classes.FirstOrDefault(c => c.Name == name);
            if (plugin is null)
            {
                return Fail($"No plugin class '{name}' was found in the projects this test project references. Create it first with 'vela add plugin {name}', or pass the plugin's class name.", ExitInvalidArguments, includeUsage: false);
            }

            entity = string.IsNullOrWhiteSpace(entity) ? AddTestSources.PluginEntity(plugin) : entity;
            if (string.IsNullOrWhiteSpace(entity))
            {
                return Fail("A plugin test needs the entity under test. Pass --entity <ModelType> (the early-bound class name, e.g. Account).", ExitInvalidArguments, includeUsage: false);
            }

            string testClass = $"{name}Tests";
            (string ns, string folder) = TestPlacement(plugin.Namespace, pluginsRoot, rootNamespace, Path.Combine(entity!, "Plugins"));
            // A partial plugin may register its steps in another file: read every declaration of the class.
            List<AddTestSources.SourceClass> declarations = classes.Where(c => c.Name == plugin.Name && c.Namespace == plugin.Namespace).ToList();
            if (!TryChoosePluginTestSteps(args, plugin.Name, entity!, declarations, out List<(PluginTestStep Step, string Name)> steps, out string stepError))
            {
                return Fail(stepError, ExitInvalidArguments, includeUsage: false);
            }

            string relativePath = Path.Combine(folder, $"{testClass}.cs");
            string fullPath = Path.Combine(projectDir, relativePath);
            if (File.Exists(fullPath) && !force)
            {
                return CompletePluginTestFile(fullPath, testClass, name, entity!, plugin.Namespace, modelsNamespace, steps, json);
            }

            string content = AddPluginTestFile(ns, plugin.Namespace, modelsNamespace, testClass, name, entity!, steps);
            return WriteFile(projectDir, relativePath, content, force, $"plugin test {testClass} (for {name})", json);
        }

        // A Custom API: the class named <name> or <name>CustomApi, else the one registering "<prefix>_<name>".
        List<AddTestSources.SourceClass> apis = classes.Where(c => AddTestSources.IsCustomApi(c, out _)).ToList();
        List<AddTestSources.SourceClass> matches = apis.Where(c => c.Name == name).ToList();
        if (matches.Count == 0)
        {
            matches = apis.Where(c => c.Name == name + "CustomApi").ToList();
        }

        if (matches.Count == 0)
        {
            matches = apis.Where(c => AddTestSources.CustomApiUniqueName(c) is { } unique
                && unique.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase)
                && unique.IndexOf('_') == unique.Length - name.Length - 1).ToList();
        }

        if (matches.Count == 0)
        {
            return Fail($"No Custom API '{name}' was found in the projects this test project references (a class {name} or {name}CustomApi deriving from CustomApiBase, or one registering '<prefix>_{name}'). Create it first with 'vela add customapi {name}', or pass the Custom API's class name.", ExitInvalidArguments, includeUsage: false);
        }

        if (matches.Count > 1)
        {
            return Fail($"Several Custom APIs match '{name}': {string.Join(", ", matches.Select(m => $"{m.Namespace}.{m.Name}"))}. Pass the class name of the one to test.", ExitInvalidArguments, includeUsage: false);
        }

        AddTestSources.SourceClass api = matches[0];
        AddTestSources.IsCustomApi(api, out string? requestType);
        string? uniqueName = AddTestSources.CustomApiUniqueName(api);
        if (uniqueName is null)
        {
            // Computed some other way: fall back to the name `vela add customapi` would have given it.
            if (!TryResolvePublisherPrefix(args, projectFile, prefix, out string testApiPrefix, out int prefixExit))
            {
                return prefixExit;
            }

            uniqueName = $"{testApiPrefix}_{name}";
            Console.Error.WriteLine($"Note: could not read the unique name {api.Name} registers; the test asserts '{uniqueName}'. Adjust it if that is not the name.");
        }

        string apiTestClass = $"{api.Name}Tests";
        string fallbackFolder = requestType is null && string.IsNullOrWhiteSpace(entity) ? "GlobalCustomApis" : Path.Combine(entity ?? string.Empty, "CustomApis");
        (string apiTestNs, string apiFolder) = TestPlacement(api.Namespace, pluginsRoot, rootNamespace, fallbackFolder);
        string apiRelativePath = Path.Combine(apiFolder, $"{apiTestClass}.cs");
        string apiFullPath = Path.Combine(projectDir, apiRelativePath);
        if (File.Exists(apiFullPath) && !force)
        {
            // A Custom API test is the same set of tests whatever the API declares: an existing class already has them.
            if (!TryReadTestClass(apiFullPath, apiTestClass, out _, out _, out _, out int readExit))
            {
                return readExit;
            }

            return ReportNothingToAdd(apiFullPath, $"{apiTestClass} already tests {api.Name}", json);
        }

        string apiContent = AddCustomApiTestFile(apiTestNs, api.Namespace, apiTestClass, api.Name, uniqueName, requestType);
        return WriteFile(projectDir, apiRelativePath, apiContent, force, $"Custom API test {apiTestClass} (for {api.Name})", json);
    }

    // An existing plugin test class is completed, not replaced: the tests of every chosen step it does not test yet
    // (no Fires_On_<name> method, see ScaffoldTemplates.PluginTestNames) go before its closing brace, with any using
    // they need; the rest of the file is left byte for byte. Files written before step names carried the stage name the
    // dispatch test Fires_On_<Message>, which counts for the first step of that message.
    private static int CompletePluginTestFile(string fullPath, string testClass, string pluginType, string entity, string pluginNamespace, string modelsNamespace, List<(PluginTestStep Step, string Name)> steps, bool json)
    {
        if (!TryReadTestClass(fullPath, testClass, out string source, out System.Text.Encoding encoding, out TestClassAppender.ClassSpan span, out int readExit))
        {
            return readExit;
        }

        HashSet<string> seenMessages = new(StringComparer.Ordinal);
        List<(PluginTestStep Step, string Name, bool Behaviour)> missing = [];
        foreach ((PluginTestStep step, string testName) in steps)
        {
            bool firstOfMessage = seenMessages.Add(step.Message);
            if (TestClassAppender.DeclaresMethod(source, span, PluginTestFiresMethod(testName))
                || (firstOfMessage && TestClassAppender.DeclaresMethod(source, span, PluginTestFiresMethod(step.Message))))
            {
                continue;
            }

            // A behaviour test the user kept (or wrote) under the generated name is not generated again.
            missing.Add((step, testName, !TestClassAppender.DeclaresMethod(source, span, PluginTestBehaviourMethod(testName))));
        }

        if (missing.Count == 0)
        {
            return ReportNothingToAdd(fullPath, $"{testClass} already tests every step of {pluginType}", json);
        }

        string members = string.Join("\n", missing.Select(m => PluginTestMethods(pluginType, entity, m.Step, m.Name, m.Behaviour)));
        File.WriteAllText(fullPath, TestClassAppender.Insert(source, span, members, PluginTestUsings(pluginNamespace, modelsNamespace)), encoding);
        string added = string.Join(", ", missing.Select(m => PluginTestFiresMethod(m.Name)));
        string description = $"tests {added} to {testClass} (for {pluginType})";
        if (json)
        {
            Console.WriteLine(BuildAddReport($"plugin {description}", [fullPath]));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Added {description}");
            Console.WriteLine($"  {fullPath}");
        }

        return ExitSuccess;
    }

    // Reads an existing test file and locates its class; a file whose class cannot be located safely is left alone.
    private static bool TryReadTestClass(string fullPath, string testClass, out string source, out System.Text.Encoding encoding, out TestClassAppender.ClassSpan span, out int exit)
    {
        source = TestClassAppender.Read(fullPath, out encoding);
        exit = ExitSuccess;
        if (TestClassAppender.TryFindClass(source, testClass, out span, out string reason))
        {
            return true;
        }

        exit = Fail($"Cannot complete {fullPath}: {reason}. Fix the file, or pass --force to overwrite it with a fresh test class.", ExitInvalidArguments, includeUsage: false);
        return false;
    }

    private static int ReportNothingToAdd(string fullPath, string reason, bool json)
    {
        if (json)
        {
            Console.WriteLine(BuildAddReport($"nothing to add: {reason}", []));
        }
        else
        {
            Console.WriteLine($"Nothing to add: {reason}.");
            Console.WriteLine($"  {fullPath}");
        }

        return ExitSuccess;
    }

    // Where a test for a class in <paramref name="sourceNamespace"/> goes: the same sub-namespace and folder under the
    // test project (Zava.Plugins.Account.Plugins -> Zava.Plugins.Tests.Account.Plugins, Account/Plugins), or, for a
    // namespace outside the plugins root, <paramref name="fallbackFolder"/>.
    private static (string Namespace, string Folder) TestPlacement(string sourceNamespace, string pluginsRoot, string rootNamespace, string fallbackFolder)
    {
        if (sourceNamespace.StartsWith(pluginsRoot + ".", StringComparison.Ordinal))
        {
            string relative = sourceNamespace.Substring(pluginsRoot.Length + 1);
            return ($"{rootNamespace}.{relative}", Path.Combine(relative.Split('.')));
        }

        string folder = fallbackFolder.Trim(Path.DirectorySeparatorChar);
        return (folder.Length == 0 ? rootNamespace : $"{rootNamespace}.{folder.Replace(Path.DirectorySeparatorChar, '.')}", folder);
    }

    // The steps a plugin test covers, each with the name its tests carry (ScaffoldTemplates.PluginTestNames over all the
    // plugin's steps, so a step keeps its name whichever run writes its tests): the --message one when given, else every
    // step the plugin registers. Their FilterOn members are set by the test so the step fires; the request type serves a
    // message the test cannot drive through a dedicated service call.
    private static bool TryChoosePluginTestSteps(string[] args, string pluginName, string entity, List<AddTestSources.SourceClass> declarations, out List<(PluginTestStep Step, string Name)> chosenSteps, out string error)
    {
        error = string.Empty;
        chosenSteps = [];
        List<AddTestSources.PluginStep> steps = declarations.SelectMany(AddTestSources.ReadSteps).ToList();
        List<PluginTestStep> testSteps = steps.Select(s => ToTestStep(s.Message, s)).ToList();
        string[] names = PluginTestNames(testSteps);
        string? rawMessage = TryReadOption(args, "--message")?.Trim();
        if (string.IsNullOrEmpty(rawMessage))
        {
            if (steps.Count == 0)
            {
                // Guessing a message would emit a test that can never see the step fire.
                error = $"Could not read a step registration in {pluginName}. Pass --message <Message> with the message its step is registered on.";
                return false;
            }

            chosenSteps = testSteps.Select((s, i) => (s, names[i])).ToList();
            return true;
        }

        if (!IdentifierPattern.IsMatch(rawMessage))
        {
            error = $"Invalid --message '{rawMessage}'. Use an SDK message name (letters, digits, '_'; not starting with a digit).";
            return false;
        }

        // The request --message names, as for add plugin: an sdkmessage alias (Send) resolves to its request
        // (SendEmail), which is what the plugin's typed trigger names.
        string? request = ResolveRequestName(rawMessage, entity, out string messageError);
        if (request is null)
        {
            error = messageError;
            return false;
        }

        int index = steps.FindIndex(s => string.Equals(s.Message, request, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = steps.FindIndex(s => string.Equals(s.Message, rawMessage, StringComparison.OrdinalIgnoreCase));
        }

        if (index >= 0)
        {
            chosenSteps = [(testSteps[index], names[index])];
            return true;
        }

        if (steps.Count > 0)
        {
            Console.Error.WriteLine($"Note: no {request} step was found in {pluginName}; the test drives {request} anyway. Set the request it needs.");
        }

        PluginTestStep guessed = ToTestStep(request, null);
        chosenSteps = [(guessed, PluginTestNames([guessed])[0])];
        return true;
    }

    // A typed trigger's message is a request name; any other is the request type the trigger names (fully qualified
    // when the plugin's usings would not reach the test), or the SDK's, or the generated one.
    private static PluginTestStep ToTestStep(string message, AddTestSources.PluginStep? step)
    {
        string? known = FindKnownMessage(message);
        string requestType = step?.RequestType is { } fromSource && fromSource.Contains('.')
            ? fromSource
            : SdkMessageClassResolver.TryGetSdkRequestTypeName(known ?? message, out string sdkType)
                ? sdkType
                : step?.RequestType ?? $"{known ?? message}Request";
        return new PluginTestStep(known ?? message, requestType, step?.Filters ?? [], step?.Stage);
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
        // registered through its request type, On<XxxRequest>() (see RegistrationCall).
        string? request = ResolveRequestName(rawMessage, entity!, out string messageError);
        if (request is null)
        {
            return Fail(messageError, ExitInvalidArguments, includeUsage: false);
        }

        string? known = FindKnownMessage(request);
        string message = known ?? request;
        string registrationCall = RegistrationCall(known, message);

        string? handler = ResolveHandler(args, message, out string handlerError);
        if (handler is null)
        {
            return Fail(handlerError, ExitInvalidArguments, includeUsage: false);
        }

        if (!TryResolveStageMode(args, out string stageCall, out string modeCall, out string stageError))
        {
            return Fail(stageError, ExitInvalidArguments, includeUsage: false);
        }

        // Create/Update (and their bulk variants) require a FilterOn: the builder (and VELA020) reject such a step
        // without one, so the caller must name the column(s). We do NOT guess one from the model.
        string filteringCall = string.Empty;
        string[] filterAttributes = [];
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

                filterAttributes = attributes;
                string lambdas = string.Join(", ", attributes.Select(a => $"e => e.{a}"));
                filteringCall = $"\n                .FilterOn({lambdas})";
            }
            else
            {
                return Fail(MissingFilterMessage(message), ExitInvalidArguments, includeUsage: false);
            }
        }

        string ns = $"{rootNamespace}.{entity}.Plugins";
        string relativePath = Path.Combine(entity!, "Plugins", $"{name}.cs");
        string fullPath = Path.Combine(projectDir, relativePath);
        if (File.Exists(fullPath) && !force)
        {
            // `add plugin` never edits an existing class: `add step` is the one way to add a step to it.
            string addStep = AddStepCommandFor(args, name, entity!, message, filterAttributes);
            return Fail($"File already exists: {fullPath}. To add this step to the existing {name}, run:\n  {addStep}\nOr pass --force to overwrite the file.", ExitInvalidArguments, includeUsage: false);
        }

        string content = AddPluginFile(ns, name, entity!, modelsNamespace, message, registrationCall, stageCall, modeCall, filteringCall, handler);
        return WriteFile(projectDir, relativePath, content, force, $"plugin {name} ({message} {entity})", HasFlag(args, "--json"));
    }

    // The `vela add step` command that adds the step an `add plugin` run asked for to the plugin class that already
    // exists: the same entity, message, handler, stage, mode, filtering columns and project as given.
    internal static string AddStepCommandFor(string[] args, string name, string entity, string message, string[] filterAttributes)
    {
        List<string> parts = ["vela add step", name, "--entity", entity, "--message", message];
        void AddOption(string option, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(option);
                parts.Add(QuoteIfNeeded(value!.Trim()));
            }
        }

        AddOption("--handler", TryReadOption(args, "--handler"));
        AddOption("--stage", TryReadOption(args, "--stage"));
        AddOption("--mode", TryReadOption(args, "--mode"));
        AddOption("--filtering-attributes", filterAttributes.Length > 0 ? string.Join(",", filterAttributes) : null);
        AddOption("--project", TryReadOption(args, "--project") ?? TryReadOption(args, "-p"));
        return string.Join(" ", parts);
    }

    private static string QuoteIfNeeded(string value) =>
        value.IndexOfAny([' ', '\t', '"', '\'']) >= 0 ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static int AddService(string name, string? entity, bool force, string projectDir, string rootNamespace, string modelsNamespace, bool json)
    {
        // With --entity the service is table-scoped, next to that table's plugins; without it, it is shared by
        // several tables and lives under Services/ at the project root, taking a late-bound Entity.
        bool shared = string.IsNullOrWhiteSpace(entity);
        string interfaceName = $"I{name}";
        string ns = shared ? $"{rootNamespace}.Services" : $"{rootNamespace}.{entity}.Services";
        string relativePath = shared ? Path.Combine("Services", $"{name}.cs") : Path.Combine(entity!, "Services", $"{name}.cs");
        string content = AddServiceFile(ns, interfaceName, name, shared ? null : entity, modelsNamespace);
        // The exact line to wire the service into a plugin's ConfigureServices. Surfaced both in the human
        // output and in the --json report (field "registration") so an agent driving the CLI gets it too.
        string registration = $"services.AddScoped<{interfaceName}>(r => new {name}(r.GetRequiredService<IOrganizationService>()));";
        return WriteFile(projectDir, relativePath, content, force, shared ? $"service {name} (shared)" : $"service {name} (scoped to {entity})", json, registration);
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
        ["plugin"] = With("--force", "--entity", "-e", "--message", "--handler", "--stage", "--mode", "--filtering-attributes", "--filter"),
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

    // Resolves --stage / --mode into the fluent builder calls. Defaults: PostOperation + Sync (the prior
    // hard-coded behaviour). Dataverse only allows asynchronous steps at PostOperation.
    private static bool TryResolveStageMode(string[] args, out string stageCall, out string modeCall, out string error)
    {
        stageCall = ".PostOperation()";
        modeCall = ".Sync()";
        bool postStage = true;
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

    // Validates explicit --filtering-attributes values and resolves each to the early-bound property FilterOn selects.
    // With the generated model: an exact property name is kept, a column logical name (the property's
    // [AttributeLogicalName], e.g. 'name') maps to its property ('Name'), and a wrong-case property name is corrected;
    // an unknown name only warns (the model may be stale). Without a model, a value with no upper-case letter is
    // refused: it reads as a logical name nothing can map, and `e => e.name` would not compile against the model.
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

        if (explicitAttributes.Length == 0)
        {
            return true;
        }

        if (!TryReadModelColumns(projectDir, entity, out HashSet<string> modelProperties, out Dictionary<string, string> logicalToProperty))
        {
            string[] logicalNames = explicitAttributes.Where(a => !a.Any(char.IsUpper)).ToArray();
            if (logicalNames.Length > 0)
            {
                error = $"--filtering-attributes {string.Join(", ", logicalNames.Select(a => $"'{a}'"))}: no early-bound model for {entity} was found to map a column logical name to its property, and FilterOn selects the property (e.g. Name, not name). Run 'vela models' first, or pass the property name.";
                return false;
            }

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

            string? suggestion = logicalToProperty.TryGetValue(attribute, out string? mapped)
                ? mapped
                : modelProperties.FirstOrDefault(p => string.Equals(p, attribute, StringComparison.OrdinalIgnoreCase));
            if (suggestion is not null)
            {
                Console.Error.WriteLine($"Note: filtering attribute '{attribute}' corrected to '{suggestion}' (FilterOn selects the early-bound property; property names are case-sensitive).");
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

    // Reads the generated properties off the entity's early-bound model (each declared right after an
    // [AttributeLogicalName("...")] attribute, facet attributes possibly in between) with the column logical name each
    // one maps. False when the model file cannot be located or declares no column (then no validation is attempted).
    private static bool TryReadModelColumns(string projectDir, string entity, out HashSet<string> properties, out Dictionary<string, string> logicalToProperty)
    {
        properties = new HashSet<string>(StringComparer.Ordinal);
        logicalToProperty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryFindEntityModelFile(projectDir, entity, out string modelPath))
        {
            return false;
        }

        string? logical = null;
        foreach (string line in File.ReadLines(modelPath))
        {
            Match logicalMatch = LogicalNameAttr.Match(line);
            if (logicalMatch.Success)
            {
                logical = logicalMatch.Groups["logical"].Value;
                continue;
            }

            if (logical is null)
            {
                continue;
            }

            Match propertyMatch = PropertyDecl.Match(line);
            if (propertyMatch.Success)
            {
                string property = propertyMatch.Groups["name"].Value;
                properties.Add(property);
                logicalToProperty.TryAdd(logical, property);
                logical = null;
            }
        }

        return properties.Count > 0;
    }

    // The solution root above a project: the nearest directory (within a few levels) holding a .sln or .slnx, else the
    // project directory itself rather than climbing toward the filesystem root and scanning an enormous tree.
    internal static string FindSolutionRoot(string projectDir)
    {
        string? dir = projectDir;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            if (Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly).Any())
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return projectDir;
    }

    // Locates the generated early-bound model file for an entity: from the solution root (FindSolutionRoot, so a
    // sibling models project is reached), finds a "{entity}.cs" that declares "class {entity} : Entity", skipping build
    // output. False when not found.
    private static bool TryFindEntityModelFile(string projectDir, string entity, out string path)
    {
        path = string.Empty;
        string root = FindSolutionRoot(projectDir);

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

    // The add help comes from the command table, like every other command's.
    private static void PrintAddUsage() => CliCommands.PrintCommandHelp("add");
}
