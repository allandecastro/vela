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
    // shared models namespace ({prefix}.Shared.Models) can be inferred.

    // SDK messages that have a dedicated On...() builder method, matched case-insensitively so the
    // scaffold emits the strongly-typed call. Any other name is registered via .OnMessage("...").
    private static readonly string[] KnownMessages =
    [
        "Create", "Update", "Delete", "Retrieve", "RetrieveMultiple",
        "Associate", "Disassociate", "SetState", "SetStateDynamicEntity",
    ];

    public static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintAddUsage();
            return args.Length == 0 ? ExitInvalidArguments : ExitSuccess;
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

        string kind = args[0].ToLowerInvariant();
        if (kind is not ("plugin" or "customapi" or "service" or "test" or "step"))
        {
            return Fail($"Unknown 'add' kind '{args[0]}'. Expected 'plugin', 'customapi', 'service', 'test' or 'step'.", ExitInvalidArguments, includeUsage: false);
        }

        string? name = FindPositional(args.Skip(1).ToArray());
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
        string modelsNamespace = $"{prefix}.Shared.Models";

        return kind switch
        {
            "plugin" => AddPlugin(args, name, entity, force, projectDir, rootNamespace, modelsNamespace),
            "service" => AddService(name, entity, force, projectDir, rootNamespace, modelsNamespace),
            "test" => AddTest(args, name, entity, force, projectDir, rootNamespace),
            "step" => AddStep(args, name, entity, projectDir),
            _ => AddCustomApi(args, name, entity, force, projectDir, rootNamespace, modelsNamespace, prefix),
        };
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

        if (!StepInjector.TryInject(source, entity!, message, registrationCall, handler, out string updated, out string error))
        {
            return Fail(error, ExitInvalidArguments, includeUsage: false);
        }

        File.WriteAllText(filePath, updated);
        Console.WriteLine($"Added step {message} ({handler}) to {name}");
        Console.WriteLine($"  {filePath}");
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
            string modelsNamespace = $"{prefix}.Shared.Models";
            string relativePath = Path.Combine(entity!, "Plugins", $"{testClass}.cs");
            string content = AddPluginTestFile(ns, pluginNamespace, modelsNamespace, testClass, name, entity!);
            return WriteFile(projectDir, relativePath, content, force, $"plugin test {testClass} (for {name})");
        }

        string uniqueName = $"{ApiPrefix(prefix)}_{name}";
        if (string.IsNullOrWhiteSpace(entity))
        {
            string ns = $"{rootNamespace}.GlobalCustomApis";
            string apiNamespace = $"{pluginsRoot}.GlobalCustomApis";
            string relativePath = Path.Combine("GlobalCustomApis", $"{testClass}.cs");
            string content = AddCustomApiTestFile(ns, apiNamespace, testClass, name, uniqueName);
            return WriteFile(projectDir, relativePath, content, force, $"Custom API test {testClass} (for {name})");
        }
        else
        {
            string ns = $"{rootNamespace}.{entity}.CustomApis";
            string apiNamespace = $"{pluginsRoot}.{entity}.CustomApis";
            string relativePath = Path.Combine(entity!, "CustomApis", $"{testClass}.cs");
            string content = AddCustomApiTestFile(ns, apiNamespace, testClass, name, uniqueName);
            return WriteFile(projectDir, relativePath, content, force, $"Custom API test {testClass} (for {name})");
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

        // Create/Update steps require filtering attributes at compile time; the generator does not know the
        // entity's columns, so it emits a TODO placeholder on a property that exists on every early-bound type.
        bool needsFiltering = string.Equals(message, "Create", StringComparison.Ordinal)
            || string.Equals(message, "Update", StringComparison.Ordinal);
        string filteringCall = needsFiltering
            ? "\n                .FilteringAttributes(e => e.Id /* TODO: choose the attributes that should trigger this step */)"
            : string.Empty;

        string ns = $"{rootNamespace}.{entity}.Plugins";
        string relativePath = Path.Combine(entity!, "Plugins", $"{name}.cs");
        string content = AddPluginFile(ns, name, entity!, modelsNamespace, message, registrationCall, filteringCall);
        return WriteFile(projectDir, relativePath, content, force, $"plugin {name} ({message} {entity})");
    }

    private static int AddService(string name, string? entity, bool force, string projectDir, string rootNamespace, string modelsNamespace)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return Fail("A service needs an entity. Pass --entity <ModelType> (the early-bound class name, e.g. Account); services scaffold table-scoped like 'vela init'.", ExitInvalidArguments, includeUsage: false);
        }

        string interfaceName = $"I{name}";
        string ns = $"{rootNamespace}.{entity}.Services";
        string relativePath = Path.Combine(entity!, "Services", $"{name}.cs");
        string content = AddServiceFile(ns, interfaceName, name, entity!, modelsNamespace);
        int result = WriteFile(projectDir, relativePath, content, force, $"service {name} (scoped to {entity})");
        if (result == ExitSuccess)
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

        string uniqueName = $"{ApiPrefix(prefix)}_{name}";

        if (generateGlobal)
        {
            string ns = $"{rootNamespace}.GlobalCustomApis";
            string relativePath = Path.Combine("GlobalCustomApis", $"{name}.cs");
            string content = AddGlobalCustomApiFile(ns, name, uniqueName, operation);
            return WriteFile(projectDir, relativePath, content, force, $"global Custom API {name} ({uniqueName})");
        }
        else
        {
            string ns = $"{rootNamespace}.{entity}.CustomApis";
            string relativePath = Path.Combine(entity!, "CustomApis", $"{name}.cs");
            string content = AddBoundCustomApiFile(ns, name, uniqueName, entity!, modelsNamespace, operation);
            return WriteFile(projectDir, relativePath, content, force, $"Custom API {name} bound to {entity} ({uniqueName})");
        }
    }

    private static int WriteFile(string projectDir, string relativePath, string content, bool force, string description)
    {
        string fullPath = Path.Combine(projectDir, relativePath);
        if (File.Exists(fullPath) && !force)
        {
            return Fail($"File already exists: {fullPath}. Use --force to overwrite.", ExitInvalidArguments, includeUsage: false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        Console.WriteLine($"Created {description}");
        Console.WriteLine($"  {fullPath}");
        return ExitSuccess;
    }

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

        string[] candidates = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 1)
        {
            projectFile = candidates[0];
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

    // Options that consume the next token as their value; everything else may be a positional.
    private static readonly string[] ValueOptions = ["--entity", "-e", "--project", "-p", "--message", "--kind", "--handler"];

    private static string? FindPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            if (current.StartsWith('-'))
            {
                if (i + 1 < args.Length && ValueOptions.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    i++;
                }

                continue;
            }

            return current;
        }

        return null;
    }

    private static void PrintAddUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  vela add plugin <Name> --entity <ModelType> [--message <Message>] [--project <csproj>] [--force]");
        Console.WriteLine("  vela add service <Name> --entity <ModelType> [--project <csproj>] [--force]");
        Console.WriteLine("  vela add customapi <Name> [--entity <ModelType>] [--bound|--global] [--function|--action] [--project <csproj>] [--force]");
        Console.WriteLine("  vela add test <Name> [--kind plugin|customapi] [--entity <ModelType>] [--project <csproj>] [--force]");
        Console.WriteLine("  vela add step <PluginName> --entity <ModelType> [--message <Message>] [--handler <Method>] [--project <csproj>]");
        Console.WriteLine("  vela add --interactive            # guided wizard");
        Console.WriteLine();
        Console.WriteLine("Generates a single file into an existing project, inferring namespaces from its RootNamespace.");
        Console.WriteLine("A service scaffolds a table-scoped interface + implementation; omit --entity on a Custom API for a global (unbound) one.");
        Console.WriteLine("Run 'vela add test' inside your test project (RootNamespace ending in .Tests); <Name> is the class under test.");
        Console.WriteLine("'vela add step' edits an existing handler-based (multi-step) plugin, adding a step + handler method.");
    }
}
