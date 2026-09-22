using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela models</c>: reads table metadata over the same Dataverse connection as plan/apply and
/// generates early-bound classes via <see cref="ModelGenerator"/>. Scope by <c>--solution</c> (derived from the
/// solution's components — what <c>pac modelbuilder</c> can't do) and/or an explicit <c>--entities</c> list.
/// </summary>
internal static class ModelsCommand
{
    // The models allowlist: its own options plus the shared connection options (models authenticates to
    // Dataverse via TryResolveConnection). Value options consume the next token; flags stand alone.
    private static readonly HashSet<string> ValueOptions = new(CommandLineOptions.ConnectionValueOptions, StringComparer.OrdinalIgnoreCase)
    {
        "--solution", "--entities", "--custom-apis", "--messages", "--roles", "--out", "--lang",
        "--namespace", "--project",
        // Optional plugin package inputs (same shape as plan/apply): when provided, ALL-mode --custom-apis excludes
        // the Custom APIs authored in these package(s) so the developer's own classes are not regenerated. `-p` is
        // the shared short alias for --package (as in plan/apply), NOT --project.
        "--package", "-p", "--packages-dir", "--package-assembly",
    };

    private static readonly HashSet<string> FlagOptions = new(CommandLineOptions.ConnectionFlagOptions, StringComparer.OrdinalIgnoreCase)
    {
        "--solution-roles", "--forms", "--tables", "--no-annotations", "--overwrite", "--verbose", "-v", "--json",
        "--interactive", "-i", "--help", "-h", "--verify",
    };

    // Value options that may appear bare (present with no value = "all"): --messages / --custom-apis. Every other
    // value option requires a value.
    private static readonly HashSet<string> OptionalValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--messages", "--custom-apis",
    };

    public static int Run(string[] args)
    {
        if ((args.Length > 0 && args[0] is "help") || CommandLineOptions.IsHelpRequested(args))
        {
            PrintModelsUsage();
            return ExitSuccess;
        }

        // Tokenize: expand --opt=value, reject unknown options and required value options missing their value, and
        // normalize the inline form so the readers below see a consistent shape.
        if (!CommandLineOptions.TryTokenize(args, ValueOptions, FlagOptions, OptionalValueOptions, out args, out int optExit))
        {
            return optExit;
        }

        string? solution = TryReadOption(args, "--solution");
        string[] entities = (TryReadOption(args, "--entities") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            // Dataverse entity logical names are lower-case; normalize so `--entities Account` resolves the same as
            // `account` (B7 - matches how add form-script/ribbon-script already lower-case the entity name).
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .ToArray();

        // --custom-apis mirrors --messages: absent, targeted (a list), or bare (present with no value = all).
        bool customApisRequested = HasFlag(args, "--custom-apis");
        string[] customApis = (TryReadOptionValue(args, "--custom-apis") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Bare `--custom-apis` (present, no explicit list) = generate every Custom API in the environment.
        bool allCustomApis = customApisRequested && customApis.Length == 0;

        // --messages has three states: absent, present-with-a-list (targeted), and present-with-no-value (all).
        // HasFlag detects presence; TryReadOptionValue reads the list only when the next token is a real value
        // (not another option), so a bare `--messages` (all mode) doesn't swallow the following flag as its value.
        bool messagesRequested = HasFlag(args, "--messages");
        string[] messages = (TryReadOptionValue(args, "--messages") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Bare `--messages` (present, but no explicit list) = generate all consumable messages, filtered.
        bool allMessages = messagesRequested && messages.Length == 0;

        string[] roles = (TryReadOption(args, "--roles") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // --solution-roles pulls every security role in --solution (like --solution does for tables).
        bool solutionRoles = HasFlag(args, "--solution-roles");

        if (string.IsNullOrWhiteSpace(solution) && entities.Length == 0 && customApis.Length == 0 && !allCustomApis && messages.Length == 0 && !allMessages && roles.Length == 0 && !solutionRoles)
        {
            return Fail("Specify a scope: --solution <uniquename>, --entities <a,b,c>, --custom-apis [<a,b,c>], --messages [<a,b,c>], --roles <a,b,c> and/or --solution-roles. (Refusing to generate every table.)", ExitInvalidArguments, includeUsage: false);
        }

        if (solutionRoles && string.IsNullOrWhiteSpace(solution))
        {
            return Fail("--solution-roles requires --solution <uniquename>.", ExitInvalidArguments, includeUsage: false);
        }

        // Bare `--custom-apis` generates EVERY Custom API in the environment, which includes the platform's own
        // (hundreds of files). That surprises callers who expected only their own, so warn up front and point at
        // the ways to narrow it. (`--package`/`--packages-dir` already excludes the Custom APIs authored in code.)
        bool excludesAuthored = HasFlag(args, "--package") || HasFlag(args, "--packages-dir");
        if (allCustomApis && !excludesAuthored)
        {
            Console.Error.WriteLine("Warning: --custom-apis with no value generates every Custom API in the environment, including system ones (this can be hundreds of files). Pass --custom-apis <a,b,c> to target specific ones, or --package/--packages-dir to exclude the Custom APIs you authored in code.");
        }

        string outDir = Path.GetFullPath(TryReadOption(args, "--out") ?? ".");
        bool overwrite = HasFlag(args, "--overwrite");
        bool interactive = HasFlag(args, "--interactive") || HasFlag(args, "-i");
        bool json = HasFlag(args, "--json");

        // --forms emits TypeScript FormModel files (parsed FormXML + table metadata) and --tables emits TypeScript
        // Web API record interfaces, instead of C# early-bound classes. Their output is TypeScript only, so --lang
        // is optional there and defaults to ts (the C# path keeps csharp as its default). All three share the
        // scope/connection/write plumbing.
        bool forms = HasFlag(args, "--forms");
        bool tables = HasFlag(args, "--tables");
        string? langOption = TryReadOption(args, "--lang");

        if (forms || tables)
        {
            // --forms and --tables may be combined: a single run then emits form models, table types, the shared
            // option-sets/ folder and one complete barrel (index.ts) covering both.
            string scope = forms && tables ? "--forms --tables" : forms ? "--forms" : "--tables";

            // --verify is a C#-model-only drift check; combining it with the write-only TS scopes would otherwise
            // silently write files, so reject it rather than surprising a CI check.
            if (HasFlag(args, "--verify"))
            {
                return Fail($"--verify checks the C# early-bound model only; it cannot be combined with {scope}.", ExitInvalidArguments, includeUsage: false);
            }

            // TypeScript is the only output for the client-side scopes, so --lang defaults to ts and only an
            // explicit non-ts value is rejected.
            string tsLang = (langOption ?? "ts").Trim().ToLowerInvariant();
            if (tsLang != "ts")
            {
                return Fail($"vela models {scope} only supports TypeScript output.", ExitInvalidArguments, includeUsage: false);
            }

            if (string.IsNullOrWhiteSpace(solution) && entities.Length == 0)
            {
                return Fail($"vela models {scope} needs a table scope: --solution <uniquename> and/or --entities <a,b,c>.", ExitInvalidArguments, includeUsage: false);
            }

            // These scopes only read tables; anything else is ignored, so say so rather than dropping it silently.
            if (customApis.Length > 0 || allCustomApis || messages.Length > 0 || allMessages || roles.Length > 0 || solutionRoles)
            {
                Console.Error.WriteLine($"Warning: {scope} generates TypeScript output only; --custom-apis / --messages / --roles / --solution-roles are ignored.");
            }

            return RunTsModels(args, forms, tables, solution, entities, outDir, overwrite, interactive, json);
        }

        string lang = (langOption ?? "csharp").Trim().ToLowerInvariant();
        if (lang != "csharp")
        {
            return Fail("--lang applies to --forms / --tables (TypeScript output) only.", ExitInvalidArguments, includeUsage: false);
        }

        string? ns = ResolveNamespace(args);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return Fail("Could not infer a namespace. Pass --namespace <ns> (or run inside a project so it can be derived).", ExitInvalidArguments, includeUsage: false);
        }

        // With no explicit --out, write into the inferred project's Models/Generated folder so the files land where
        // the namespace (<Prefix>.Shared.Models.Generated) says, instead of the project root.
        outDir = ResolveCSharpOutputDir(args, outDir);

        bool dataAnnotations = !HasFlag(args, "--no-annotations");

        // --verify does not write: it regenerates the scoped entity models from live metadata in memory and
        // compares them, column by column, against the files already on disk, then reports drift. It verifies the
        // C# early-bound entity model only (the typed-image blind spot); custom-api/message/role scopes are ignored.
        if (HasFlag(args, "--verify"))
        {
            bool ignoredScopes = customApis.Length > 0 || allCustomApis || messages.Length > 0 || allMessages || roles.Length > 0 || solutionRoles;
            return RunModelVerify(args, interactive, solution, entities, ns!, outDir, dataAnnotations, json, ignoredScopes);
        }

        // Optional plugin package inputs (offline scan): when provided, collect the unique names of the Custom APIs
        // the developer authored in code so ALL-mode --custom-apis can exclude them (they already have those classes).
        // Without a package, behavior is unchanged (ALL = every Custom API in the environment). Resolved before we
        // connect so a bad --package/--packages-dir fails fast.
        if (!TryResolvePackagePaths(args, out List<string> packagePaths, out int pkgExit))
        {
            return pkgExit;
        }

        // Only scan when ALL-mode --custom-apis could actually use the result: the scan instantiates and runs code
        // from the package (see PackageScanner's SECURITY note), so don't run it when nothing would consume the names
        // (targeted --custom-apis is never filtered; --messages excludes Custom-API-backed messages by itself).
        HashSet<string> authoredCustomApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allCustomApis && packagePaths.Count > 0)
        {
            List<string> tempDirectories = new List<string>();
            try
            {
                SharedOptions scanOptions = new SharedOptions(
                    PackagePaths: packagePaths.ToArray(),
                    PackageAssembly: TryReadOption(args, "--package-assembly"),
                    ConnectionString: null,
                    OAuthUrl: null,
                    OAuthToken: null,
                    SolutionUniqueName: null,
                    Environment: null,
                    OutPath: null,
                    AutoApprove: false,
                    Interactive: false,
                    Strict: false,
                    Verbose: false);

                // Web defaults (camelCase) so the normalized definition exposes "uniqueName" — matching how
                // NormalizeCustomApiDefinition emits it and how PackageScannerBuildStateTests reads it.
                JsonSerializerOptions scanJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                RegistrationState scanned = PackageScanner.BuildLocalStateFromPackages(scanOptions, scanJson, tempDirectories);
                authoredCustomApis = AuthoredCustomApiUniqueNames(scanned);
            }
            catch (Exception ex)
            {
                // A bad/unreadable package must fail with a clean exit code, not a raw stack trace (mirrors how
                // plan/apply surface a scan failure).
                return Fail($"Could not scan the plugin package(s) to exclude authored Custom APIs: {ex.Message}", ExitError, includeUsage: false);
            }
            finally
            {
                CleanupTempDirectories(tempDirectories);
            }
        }

        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: solution,
            Environment: url,
            OutPath: outDir,
            AutoApprove: false,
            Interactive: interactive,
            Strict: false,
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"));

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<GeneratedModel> models = new List<GeneratedModel>();

            // Lazily resolved logical-name -> early-bound type-name (SchemaName) map, so the generated
            // On<Message>() step extensions reference the exact model type (e.g. new_CustomEntity), not a naive
            // PascalCase of the logical name. Built once, only when a message/custom-api generation needs it.
            IReadOnlyDictionary<string, string>? entityTypeNamesCache = null;
            IReadOnlyDictionary<string, string> EntityTypeNames() => entityTypeNamesCache ??= BuildEntityTypeNameMap(service);

            List<string> logicalNames = ResolveLogicalNames(service, solution, entities);
            if (logicalNames.Count > 0)
            {
                EntityMetadata[] metadata = logicalNames.Select(name => ReadEntity(service, name)).ToArray();
                models.AddRange(ModelGenerator.Generate(metadata, new ModelGenOptions(ns!, dataAnnotations)));
            }

            if (customApis.Length > 0 || allCustomApis)
            {
                CustomApiMessage[] customApiMessages;
                if (allCustomApis)
                {
                    // Bare --custom-apis = every Custom API in the environment. Read them all in a few paged queries
                    // (one per table) and join in memory, instead of one-plus-two round-trips per API, which times
                    // out on large environments (#211). Then drop any the developer authored in the provided
                    // package(s) (they already have those classes). No package = nothing excluded.
                    IReadOnlyList<CustomApiMessage> all = ReadAllCustomApis(service);
                    ISet<string> kept = new HashSet<string>(
                        ExcludeAuthoredCustomApis(all.Select(m => m.UniqueName), authoredCustomApis, out int excludedCount),
                        StringComparer.OrdinalIgnoreCase);
                    customApiMessages = all.Where(m => kept.Contains(m.UniqueName)).ToArray();
                    if (excludedCount > 0)
                    {
                        Console.Error.WriteLine($"Excluding {excludedCount} Custom API(s) authored in the provided package(s).");
                    }
                }
                else
                {
                    // Targeted list: the user asked for these by name, so generate exactly them — never filtered.
                    customApiMessages = customApis.Select(name => ReadCustomApi(service, name)).ToArray();
                }

                models.AddRange(CustomApiMessageGenerator.Generate(customApiMessages, ns!, EntityTypeNames()));
            }

            if (allMessages)
            {
                // ALL mode: every non-private, non-Custom-API-backed message (filtered by the reader), minus any
                // that already has an SDK request class (skipped here to avoid a duplicate/collision with the SDK
                // type). Because the reader already drops Custom-API-backed messages, the developer's own authored
                // Custom APIs never reach --messages (they surface only under --custom-apis, filtered above); no
                // package-based exclusion is needed here. A single unreadable message is skipped with a warning
                // rather than aborting the batch.
                List<SdkMessage> sdkMessages = new List<SdkMessage>();
                foreach (GeneratableMessage candidate in SdkMessageReader.ListGeneratableMessages(service))
                {
                    string name = candidate.Name;
                    if (SdkMessageClassResolver.HasSdkRequestClass(name))
                    {
                        continue;
                    }

                    try
                    {
                        sdkMessages.Add(SdkMessageReader.ReadSdkMessage(service, name));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: skipping message '{name}' (could not read its contract): {ex.Message}");
                    }
                }

                models.AddRange(SdkMessageGenerator.Generate(sdkMessages, ns!, EntityTypeNames()));
            }
            else if (messages.Length > 0)
            {
                // Targeted: the user asked for these by name, so generate exactly them — never silently skipped. If
                // one already has an SDK request class, warn (suggest the SDK type) but still generate the wrapper.
                List<SdkMessage> sdkMessages = new List<SdkMessage>();
                foreach (string name in messages)
                {
                    if (SdkMessageClassResolver.HasSdkRequestClass(name))
                    {
                        Console.Error.WriteLine($"Warning: '{name}' already has an SDK request class (Microsoft.Xrm.Sdk.Messages.{name}Request); generating a duplicate. Prefer the SDK type via OnMessage<{name}Request>().");
                    }

                    sdkMessages.Add(SdkMessageReader.ReadSdkMessage(service, name));
                }

                models.AddRange(SdkMessageGenerator.Generate(sdkMessages, ns!, EntityTypeNames()));
            }

            if (roles.Length > 0 || solutionRoles)
            {
                IReadOnlyList<RoleModel> roleModels = ReadRoles(service, roles, solutionRoles ? solution : null);
                if (roleModels.Count > 0)
                {
                    models.Add(RoleGenerator.Generate(roleModels, new RoleGenOptions(ns!)));
                }
            }

            if (models.Count == 0)
            {
                return Fail("Nothing resolved from the given scope.", ExitInvalidArguments, includeUsage: false);
            }

            return WriteModels(models, outDir, overwrite, json, $" (namespace {ns})");
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
    }

    // The --verify path: regenerate the scoped entity models from live metadata in memory and compare them,
    // column by column, against the files already on disk. Reports drift (a column the live schema no longer has,
    // a retyped column, or a column added live) and returns ExitDriftDetected when anything is out of sync, so CI
    // can fail a stale model. Never writes. Verifies the C# early-bound entity model only.
    private static int RunModelVerify(string[] args, bool interactive, string? solution, string[] entities, string ns, string outDir, bool dataAnnotations, bool json, bool ignoredScopes)
    {
        if (ignoredScopes)
        {
            Console.Error.WriteLine("Warning: --verify checks the C# early-bound entity model only; --custom-apis / --messages / --roles / --solution-roles are ignored.");
        }

        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: solution,
            Environment: url,
            OutPath: outDir,
            AutoApprove: false,
            Interactive: interactive,
            Strict: false,
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"));

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<string> logicalNames = ResolveLogicalNames(service, solution, entities);
            if (logicalNames.Count == 0)
            {
                return Fail("No tables resolved from the given scope.", ExitInvalidArguments, includeUsage: false);
            }

            EntityMetadata[] metadata = logicalNames.Select(name => ReadEntity(service, name)).ToArray();
            IReadOnlyList<GeneratedModel> live = ModelGenerator.Generate(metadata, new ModelGenOptions(ns, dataAnnotations));

            List<string> liveSources = live.Select(m => m.Code).ToList();
            List<string> localSources = ReadLocalModelSources(outDir);

            ModelDriftChecker.ModelDriftReport report = ModelDriftChecker.Compare(liveSources, localSources);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(BuildDriftReport(url, ns, report), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            }
            else
            {
                WriteDriftReport(url, ns, report);
            }

            return report.HasDrift ? ExitDriftDetected : ExitSuccess;
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
    }

    // Reads every C# source under the model output directory. The drift extractor keeps only entity classes
    // (files carrying the EntityLogicalNameValue constant) and ignores option-set / support files, so reading all
    // of them is safe; a file we cannot read is warned about and skipped rather than aborting the check.
    internal static List<string> ReadLocalModelSources(string outDir)
    {
        List<string> sources = new List<string>();
        if (!Directory.Exists(outDir))
        {
            return sources;
        }

        foreach (string file in Directory.EnumerateFiles(outDir, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                sources.Add(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: could not read '{file}': {ex.Message}");
            }
        }

        return sources;
    }

    // The --verify --json report: one entry per scoped table with its drift, plus a top-level hasDrift flag.
    internal static object BuildDriftReport(string? environment, string ns, ModelDriftChecker.ModelDriftReport report) =>
        new
        {
            mode = "verify",
            environment,
            @namespace = ns,
            hasDrift = report.HasDrift,
            tables = report.Entities.Select(e => new
            {
                entity = e.EntityLogicalName,
                inSync = !e.HasDrift,
                missingLocalFile = e.MissingLocalFile,
                removedInDataverse = e.RemovedInDataverse,
                typeChanged = e.TypeChanged.Select(t => new { t.LogicalName, t.LocalType, t.LiveType }).ToArray(),
                addedInDataverse = e.AddedInDataverse,
            }).ToArray(),
        };

    // The default human --verify report: one line per table, with the drifted columns indented under it.
    private static void WriteDriftReport(string? environment, string ns, ModelDriftChecker.ModelDriftReport report)
    {
        bool color = ConsoleTheme.StdoutColor;
        Console.WriteLine(ConsoleTheme.Heading($"verify {environment} (namespace {ns})", color));

        foreach (ModelDriftChecker.EntityDrift drift in report.Entities)
        {
            if (!drift.HasDrift)
            {
                Console.WriteLine($"  {drift.EntityLogicalName}: {ConsoleTheme.Added("in sync", color)}");
                continue;
            }

            if (drift.MissingLocalFile)
            {
                Console.WriteLine($"  {drift.EntityLogicalName}: {ConsoleTheme.Changed("no generated file on disk (run vela models to generate it)", color)}");
                continue;
            }

            Console.WriteLine($"  {ConsoleTheme.Changed(drift.EntityLogicalName + ": drift", color)}");

            if (drift.RemovedInDataverse.Count > 0)
            {
                Console.WriteLine(ConsoleTheme.Removed("    removed in Dataverse (local model has columns the schema no longer has; typed reads return null):", color));
                foreach (string column in drift.RemovedInDataverse)
                {
                    Console.WriteLine(ConsoleTheme.Removed($"      - {column}", color));
                }
            }

            if (drift.TypeChanged.Count > 0)
            {
                Console.WriteLine(ConsoleTheme.Changed("    type changed in Dataverse (regenerate):", color));
                foreach (ModelDriftChecker.AttributeTypeChange change in drift.TypeChanged)
                {
                    Console.WriteLine(ConsoleTheme.Changed($"      - {change.LogicalName}: local {change.LocalType} {ConsoleTheme.Arrow} live {change.LiveType}", color));
                }
            }

            if (drift.AddedInDataverse.Count > 0)
            {
                Console.WriteLine(ConsoleTheme.Added("    added in Dataverse (regenerate to include):", color));
                foreach (string column in drift.AddedInDataverse)
                {
                    Console.WriteLine(ConsoleTheme.Added($"      + {column}", color));
                }
            }
        }

        int driftCount = report.Entities.Count(e => e.HasDrift);
        if (driftCount == 0)
        {
            ConsoleTheme.WriteSuccessLine($"All {report.Entities.Count} table(s) are in sync with the live schema.");
        }
        else
        {
            Console.WriteLine(ConsoleTheme.Changed($"Drift detected in {driftCount} of {report.Entities.Count} table(s). Regenerate the model to resync.", color));
        }
    }

    // The --forms / --tables / --forms --tables path: read the scoped tables' metadata (and, for --forms, their
    // published Main/QuickCreate forms), and emit the vela-client TypeScript output. When both scopes run, the form
    // models and table types share one option-sets registry (so the enum files are the union) and one index.ts
    // barrel covers everything. Shares the connection/write plumbing with the C# path; needs no namespace.
    private static int RunTsModels(string[] args, bool forms, bool tables, string? solution, string[] entities, string outDir, bool overwrite, bool interactive, bool json)
    {
        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: solution,
            Environment: url,
            OutPath: outDir,
            AutoApprove: false,
            Interactive: interactive,
            Strict: false,
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"));

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<string> logicalNames = ResolveLogicalNames(service, solution, entities);
            if (logicalNames.Count == 0)
            {
                return Fail("No tables resolved from the given scope.", ExitInvalidArguments, includeUsage: false);
            }

            // Metadata is read once per table and cached, shared between the forms and tables scopes; a single
            // unreadable table is skipped with a warning rather than aborting the whole batch.
            Dictionary<string, EntityMetadata> metaCache = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            EntityMetadata? ReadMeta(string logicalName)
            {
                if (unreadable.Contains(logicalName))
                {
                    return null;
                }

                if (metaCache.TryGetValue(logicalName, out EntityMetadata? cached))
                {
                    return cached;
                }

                try
                {
                    EntityMetadata read = ReadEntity(service, logicalName);
                    metaCache[logicalName] = read;
                    return read;
                }
                catch (Exception ex)
                {
                    unreadable.Add(logicalName);
                    Console.Error.WriteLine($"Warning: skipping table '{logicalName}' (metadata read failed): {ex.Message}");
                    return null;
                }
            }

            // Shared across both scopes so option sets are registered once and the enum files are the union.
            OptionSetsTs.Registry optionSets = new OptionSetsTs.Registry();
            List<GeneratedModel> files = new List<GeneratedModel>();

            if (forms)
            {
                List<FormSource> sources = new List<FormSource>();
                foreach (SystemForm form in FormReader.ReadForms(service, logicalNames))
                {
                    EntityMetadata? entity = ReadMeta(form.ObjectTypeCode);
                    if (entity == null)
                    {
                        continue;
                    }

                    try
                    {
                        sources.Add(new FormSource(form.FormId.ToString("D"), form.FormType, form.Name, FormXmlParser.Parse(form.FormXml), entity));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: skipping form '{form.ObjectTypeCode}/{form.Name}' (invalid FormXML): {ex.Message}");
                    }
                }

                files.AddRange(FormModelTsGenerator.GenerateForms(sources, optionSets));
            }

            if (tables)
            {
                List<EntityMetadata> metadata = new List<EntityMetadata>();
                foreach (string name in logicalNames)
                {
                    EntityMetadata? entity = ReadMeta(name);
                    if (entity != null)
                    {
                        metadata.Add(entity);
                    }
                }

                files.AddRange(TableTypeTsGenerator.GenerateTables(metadata, optionSets));
            }

            files.AddRange(OptionSetsTs.GenerateFiles(optionSets));

            GeneratedModel? barrel = BarrelTs.Build(files);
            if (barrel != null)
            {
                files.Add(barrel);
            }

            if (files.Count == 0)
            {
                // Distinguish the common forms-only "no published forms" case from a genuinely empty scope.
                return Fail(
                    forms && !tables
                        ? "No published Main or Quick Create forms were found for the scoped tables."
                        : "No TypeScript models were generated from the scoped tables.",
                    ExitInvalidArguments,
                    includeUsage: false);
            }

            return WriteModels(files, outDir, overwrite, json, string.Empty);
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
    }

    // Writes generated files all-or-nothing: fails up front on any pre-existing file (unless --overwrite) so a
    // partial write can never leave a half-generated output directory. On success prints a human summary, or a
    // machine-readable JSON report when --json is set (for scripts and agents).
    internal static int WriteModels(IReadOnlyList<GeneratedModel> models, string outDir, bool overwrite, bool json, string summarySuffix)
    {
        // Guard against two generated files sharing a name: writing them in a loop would silently overwrite the
        // first, and the reported count would exceed what lands on disk. Generators disambiguate, so this is a
        // belt-and-braces check that turns any regression into a clear failure instead of data loss.
        string[] duplicates = models
            .GroupBy(model => model.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            return Fail($"Generated file name collision: {string.Join(", ", duplicates)}. Narrow the scope or rename to disambiguate.", ExitInvalidArguments, includeUsage: false);
        }

        Directory.CreateDirectory(outDir);

        if (!overwrite)
        {
            List<string> existing = models
                .Select(model => Path.Combine(outDir, model.FileName))
                .Where(File.Exists)
                .ToList();
            if (existing.Count > 0)
            {
                return Fail($"Refusing to overwrite existing file(s): {string.Join(", ", existing)}. Use --overwrite to regenerate.", ExitInvalidArguments, includeUsage: false);
            }
        }

        int written = 0;
        foreach (GeneratedModel model in models)
        {
            // A model's file name can carry a subfolder (e.g. tables/account.ts), so ensure its directory exists.
            string target = Path.Combine(outDir, model.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, model.Code);
            written++;
        }

        if (json)
        {
            Console.WriteLine(BuildGenerationReport(outDir, models));
        }
        else
        {
            ConsoleTheme.WriteSuccessLine($"Generated {written} file(s) into {outDir}{summarySuffix}.");
        }
        return ExitSuccess;
    }

    // The --json success report: the output directory and the generated file names, in generation order.
    internal static string BuildGenerationReport(string outDir, IReadOnlyList<GeneratedModel> models) =>
        JsonSerializer.Serialize(
            new
            {
                outDir,
                count = models.Count,
                files = models.Select(m => m.FileName).ToArray(),
            },
            new JsonSerializerOptions { WriteIndented = true });

    internal static List<string> ResolveLogicalNames(IOrganizationService service, string? solution, string[] entities)
    {
        SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entity in entities)
        {
            names.Add(entity);
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            foreach (string name in EntitiesInSolution(service, solution!))
            {
                names.Add(name);
            }
        }

        return names.ToList();
    }

    // Derives the table list from a solution's components (componenttype 1 = Entity), mapping the component
    // object ids to logical names via a lightweight RetrieveAllEntities (entity filter only).
    private static IEnumerable<string> EntitiesInSolution(IOrganizationService service, string uniqueName)
    {
        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity? solution = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 1);
        HashSet<Guid> objectIds = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .ToHashSet();

        if (objectIds.Count == 0)
        {
            return Enumerable.Empty<string>();
        }

        RetrieveAllEntitiesResponse all = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });
        return all.EntityMetadata
            .Where(m => m.MetadataId.HasValue && objectIds.Contains(m.MetadataId.Value) && !string.IsNullOrWhiteSpace(m.LogicalName))
            .Select(m => m.LogicalName)
            .ToList();
    }

    private static EntityMetadata ReadEntity(IOrganizationService service, string logicalName)
    {
        RetrieveEntityResponse response = (RetrieveEntityResponse)service.Execute(
            new RetrieveEntityRequest { LogicalName = logicalName, EntityFilters = EntityFilters.Attributes | EntityFilters.Entity | EntityFilters.Relationships });
        return response.EntityMetadata;
    }

    // Maps every entity's logical name to the early-bound C# type name the model generator emits for it (its
    // SchemaName, escaped the same way as ModelGenerator.ClassName). One lightweight RetrieveAllEntities (Entity
    // filter only). Keys are case-insensitive because sdkmessagefilter.primaryobjecttypecode /
    // customapi.boundentitylogicalname casing is not guaranteed. Used to wire generated On<Message>() step
    // extensions to the exact model type (e.g. new_CustomEntity, not NewCustomentity).
    private static IReadOnlyDictionary<string, string> BuildEntityTypeNameMap(IOrganizationService service)
    {
        RetrieveAllEntitiesResponse all = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });

        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (EntityMetadata entity in all.EntityMetadata)
        {
            if (string.IsNullOrWhiteSpace(entity.LogicalName))
            {
                continue;
            }

            string typeName = !string.IsNullOrWhiteSpace(entity.SchemaName)
                ? entity.SchemaName
                : char.ToUpperInvariant(entity.LogicalName[0]) + entity.LogicalName.Substring(1);
            map[entity.LogicalName] = CSharpIdentifier.EscapeKeyword(typeName);
        }

        return map;
    }

    // Resolves the optional plugin package inputs (--package, repeatable; --packages-dir, a folder of .nupkg) to full
    // paths, mirroring how plan/apply parse them. Returns true with a (possibly empty) list; false plus an exit code
    // on a bad path. No package option given = an empty list, and Custom API ALL mode stays unfiltered.
    private static bool TryResolvePackagePaths(string[] args, out List<string> packagePaths, out int exitCode)
    {
        exitCode = ExitSuccess;
        List<string> paths = ReadMultiOption(args, "--package");
        paths.AddRange(ReadMultiOption(args, "-p")); // -p is the shared short alias for --package (as in plan/apply)

        string? packagesDir = TryReadOption(args, "--packages-dir");
        if (!string.IsNullOrWhiteSpace(packagesDir))
        {
            string fullDir = Path.GetFullPath(packagesDir);
            if (!Directory.Exists(fullDir))
            {
                packagePaths = new List<string>();
                exitCode = Fail($"Packages directory not found: {fullDir}", ExitFileNotFound, includeUsage: false);
                return false;
            }

            paths.AddRange(Directory.GetFiles(fullDir, "*.nupkg", SearchOption.TopDirectoryOnly));
        }

        packagePaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string packagePath in packagePaths)
        {
            if (!File.Exists(packagePath))
            {
                exitCode = Fail($"Package not found: {packagePath}", ExitFileNotFound, includeUsage: false);
                return false;
            }

            if (!packagePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = Fail($"Package must be a .nupkg file: {packagePath}", ExitInvalidArguments, includeUsage: false);
                return false;
            }
        }

        return true;
    }

    // The unique names of every Custom API authored in the scanned package(s), from the normalized "custom-api" items.
    // Case-insensitive so it matches the environment's uniquename regardless of casing.
    internal static HashSet<string> AuthoredCustomApiUniqueNames(RegistrationState state)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistrationItem item in state.Items)
        {
            if (!string.Equals(item.Kind, "custom-api", StringComparison.Ordinal))
            {
                continue;
            }

            if (item.Definition.TryGetProperty("uniqueName", out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? uniqueName = el.GetString();
                if (!string.IsNullOrWhiteSpace(uniqueName))
                {
                    names.Add(uniqueName!);
                }
            }
        }

        return names;
    }

    // Drops the developer's own authored Custom APIs from the ALL-mode name list (they already have those classes).
    // Returns the kept names in the original order; excludedCount reports how many were removed. Factored out so the
    // exclusion is unit-testable without a live connection.
    internal static IReadOnlyList<string> ExcludeAuthoredCustomApis(IEnumerable<string> allNames, ISet<string> authoredUniqueNames, out int excludedCount)
    {
        List<string> all = allNames.ToList();
        List<string> kept = all.Where(n => !authoredUniqueNames.Contains(n)).ToList();
        excludedCount = all.Count - kept.Count;
        return kept;
    }

    // Best-effort removal of the temp folders the package scan extracted into (mirrors Program.CleanupTempDirectories,
    // which is a Program-scoped local function and so not reachable here).
    private static void CleanupTempDirectories(List<string> tempDirectories)
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

    // Reads every Custom API (with its request parameters and response properties) in a fixed number of paged
    // queries - one for `customapi`, one for `customapirequestparameter`, one for `customapiresponseproperty` -
    // joined in memory by customapiid. Replaces the one-plus-two round-trips per API that the ALL-mode loop used to
    // do (which times out on large environments, #211).
    internal static IReadOnlyList<CustomApiMessage> ReadAllCustomApis(IOrganizationService service)
    {
        Dictionary<Guid, List<CustomApiParameter>> requestParameters = GroupCustomApiParameters(service, "customapirequestparameter", isRequest: true);
        Dictionary<Guid, List<CustomApiParameter>> responseProperties = GroupCustomApiParameters(service, "customapiresponseproperty", isRequest: false);

        QueryExpression query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet("customapiid", "uniquename", "bindingtype", "boundentitylogicalname", "allowedcustomprocessingsteptype", "isprivate"),
        };

        List<CustomApiMessage> result = new List<CustomApiMessage>();
        foreach (Entity api in service.RetrieveAll(query))
        {
            string? uniqueName = api.GetAttributeValue<string>("uniquename");
            if (string.IsNullOrWhiteSpace(uniqueName))
            {
                continue;
            }

            int bindingType = api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0;
            string? boundEntity = api.GetAttributeValue<string>("boundentitylogicalname");
            result.Add(new CustomApiMessage(
                uniqueName!,
                IsBoundToEntity: bindingType == 1,
                requestParameters.TryGetValue(api.Id, out List<CustomApiParameter>? rp) ? rp : new List<CustomApiParameter>(),
                responseProperties.TryGetValue(api.Id, out List<CustomApiParameter>? sp) ? sp : new List<CustomApiParameter>(),
                AllowedStepType: api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 2,
                IsPrivate: api.GetAttributeValue<bool>("isprivate"),
                BoundEntityLogicalName: string.IsNullOrWhiteSpace(boundEntity) ? null : boundEntity));
        }

        return result.OrderBy(m => m.UniqueName, StringComparer.Ordinal).ToList();
    }

    // Reads all rows of a Custom API parameter table in one paged query and groups them by their parent Custom API.
    // Mirrors the per-API reader's column/shape choices (see ReadCustomApiParameters), including the by-name order.
    private static Dictionary<Guid, List<CustomApiParameter>> GroupCustomApiParameters(IOrganizationService service, string entity, bool isRequest)
    {
        ColumnSet columns = isRequest
            ? new ColumnSet("name", "type", "isoptional", "customapiid")
            : new ColumnSet("name", "type", "customapiid");
        QueryExpression query = new QueryExpression(entity) { ColumnSet = columns };

        Dictionary<Guid, List<CustomApiParameter>> byApi = new Dictionary<Guid, List<CustomApiParameter>>();
        foreach (Entity e in service.RetrieveAll(query))
        {
            Guid apiId = e.GetAttributeValue<EntityReference>("customapiid")?.Id ?? Guid.Empty;
            string name = e.GetAttributeValue<string>("name") ?? string.Empty;
            if (apiId == Guid.Empty || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            CustomApiParameter parameter = new CustomApiParameter(
                name,
                e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10,
                isRequest && (e.GetAttributeValue<bool?>("isoptional") ?? false));

            if (!byApi.TryGetValue(apiId, out List<CustomApiParameter>? list))
            {
                list = new List<CustomApiParameter>();
                byApi[apiId] = list;
            }

            list.Add(parameter);
        }

        foreach (List<CustomApiParameter> list in byApi.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        return byApi;
    }

    private static CustomApiMessage ReadCustomApi(IOrganizationService service, string uniqueName)
    {
        QueryExpression query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet("customapiid", "bindingtype", "boundentitylogicalname", "allowedcustomprocessingsteptype", "isprivate"),
            TopCount = 1,
        };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity api = service.RetrieveMultiple(query).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Custom API '{uniqueName}' was not found.");

        // bindingtype: 0 Global, 1 Entity, 2 EntityCollection. Only type 1 auto-adds a single-record Target input,
        // so IsBoundToEntity (which drives that Target property) stays == 1; the extension constraint keys off
        // boundEntity being set instead, so a collection-bound (type 2) API is still entity-constrained (Phase 3c).
        int bindingType = api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0;
        string? boundEntity = api.GetAttributeValue<string>("boundentitylogicalname");
        return new CustomApiMessage(
            uniqueName,
            IsBoundToEntity: bindingType == 1,
            ReadCustomApiParameters(service, "customapirequestparameter", api.Id),
            ReadCustomApiParameters(service, "customapiresponseproperty", api.Id),
            AllowedStepType: api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 2,
            IsPrivate: api.GetAttributeValue<bool>("isprivate"),
            BoundEntityLogicalName: string.IsNullOrWhiteSpace(boundEntity) ? null : boundEntity);
    }

    internal static List<CustomApiParameter> ReadCustomApiParameters(IOrganizationService service, string entity, Guid customApiId)
    {
        // Use the parameter's `name` (e.g. "ContactId"), NOT its `uniquename` (e.g. "zava_ContactValidate.
        // ContactId"): the name is both the C# property identifier and the runtime Parameters/Results key.
        // The dotted uniquename would emit `public Guid zava_ContactValidate.ContactId` (parsed as an explicit
        // interface member, CS0538) and read the wrong Parameters key at runtime.
        // isoptional exists on customapirequestparameter only; customapiresponseproperty has no such column
        // (response properties are always present), so it is requested and read for request parameters alone.
        bool isRequest = entity == "customapirequestparameter";
        ColumnSet columns = isRequest ? new ColumnSet("name", "type", "isoptional") : new ColumnSet("name", "type");
        QueryExpression query = new QueryExpression(entity) { ColumnSet = columns };
        query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, customApiId);
        return service.RetrieveAll(query)
            .Select(e => new CustomApiParameter(
                e.GetAttributeValue<string>("name") ?? string.Empty,
                e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10,
                isRequest && (e.GetAttributeValue<bool?>("isoptional") ?? false)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<RoleModel> ReadRoles(IOrganizationService service, string[] roleNames, string? solution)
    {
        // Resolve to unique root roles (id -> name), merging explicit names and the solution's role components so a
        // role listed both ways is generated once.
        Dictionary<Guid, string> roots = new Dictionary<Guid, string>();

        List<string> missing = new List<string>();
        foreach (string name in roleNames)
        {
            Entity? root = ReadRootRole(service, name);
            if (root == null)
            {
                missing.Add(name);
                continue;
            }

            roots[root.Id] = root.GetAttributeValue<string>("name") ?? name;
        }

        // Generate whatever resolved rather than aborting the whole run on the first missing role; a typo in one
        // name should not throw away the others. Only a completely empty result (no explicit role resolved and no
        // solution roles) is an error.
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"Warning: security role(s) not found and skipped: {string.Join(", ", missing)}.");
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            foreach (Entity role in RolesInSolution(service, solution!))
            {
                roots[role.Id] = role.GetAttributeValue<string>("name") ?? role.Id.ToString();
            }
        }

        if (roots.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the requested security role(s) were found in the connected environment: {string.Join(", ", roleNames)}.");
        }

        IReadOnlyDictionary<Guid, (string Entity, PrivilegeType Type)> privileges = BuildPrivilegeMap(service);
        return roots
            .OrderBy(r => r.Value, StringComparer.Ordinal)
            .Select(r => new RoleModel(r.Value, r.Key, RolePrivilegeMapper.ToGrants(ReadRolePrivileges(service, r.Key), privileges)))
            .ToList();
    }

    // Every security role in a solution (solutioncomponent componenttype 20). Solution components reference the
    // root role, so the object ids are the stable root ids.
    private static IEnumerable<Entity> RolesInSolution(IOrganizationService service, string uniqueName)
    {
        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity solutionRecord = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionRecord.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 20); // 20 = Role
        List<Guid> roleIds = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .Distinct()
            .ToList();

        foreach (Guid roleId in roleIds)
        {
            yield return service.Retrieve("role", roleId, new ColumnSet("roleid", "name", "parentrootroleid"));
        }
    }

    // The root (template) role for a name. Roles are duplicated per business unit; the root is the one whose
    // parentrootroleid points at itself, and its id is the stable id plugin logic uses.
    internal static Entity? ReadRootRole(IOrganizationService service, string name)
    {
        QueryExpression query = new QueryExpression("role") { ColumnSet = new ColumnSet("roleid", "name", "parentrootroleid") };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        List<Entity> matches = service.RetrieveAll(query).ToList();

        // Null (not found) instead of throwing: the caller collects and reports the missing names but still
        // generates the roles that did resolve.
        return matches.FirstOrDefault(r => r.GetAttributeValue<EntityReference>("parentrootroleid")?.Id == r.Id)
            ?? matches.FirstOrDefault();
    }

    private static IEnumerable<(Guid PrivilegeId, int DepthMask)> ReadRolePrivileges(IOrganizationService service, Guid roleId)
    {
        QueryExpression query = new QueryExpression("roleprivileges") { ColumnSet = new ColumnSet("privilegeid", "privilegedepthmask") };
        query.Criteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
        return service.RetrieveAll(query)
            .Select(e => (e.GetAttributeValue<Guid>("privilegeid"), e.GetAttributeValue<int>("privilegedepthmask")))
            .ToList();
    }

    // privilegeId -> (owning table, privilege type), from every table's Privileges metadata. Miscellaneous
    // (non-table) privileges aren't in any table's collection, so they're naturally excluded from generation.
    private static IReadOnlyDictionary<Guid, (string Entity, PrivilegeType Type)> BuildPrivilegeMap(IOrganizationService service)
    {
        RetrieveAllEntitiesResponse response = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity | EntityFilters.Privileges, RetrieveAsIfPublished = true });

        Dictionary<Guid, (string, PrivilegeType)> map = new Dictionary<Guid, (string, PrivilegeType)>();
        foreach (EntityMetadata entity in response.EntityMetadata)
        {
            if (entity.Privileges == null || string.IsNullOrWhiteSpace(entity.LogicalName))
            {
                continue;
            }

            foreach (SecurityPrivilegeMetadata privilege in entity.Privileges)
            {
                map[privilege.PrivilegeId] = (entity.LogicalName, privilege.PrivilegeType);
            }
        }

        return map;
    }

    // Default output directory for C# models: an explicit --out wins; otherwise, when the namespace is inferred from
    // a project (no --namespace) and a project is found, write into that project's Models/Generated folder so the
    // files land where the inferred namespace says. Falls back to the caller's default (the current directory).
    internal static string ResolveCSharpOutputDir(string[] args, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(TryReadOption(args, "--out"))
            || !string.IsNullOrWhiteSpace(TryReadOption(args, "--namespace")))
        {
            return fallback;
        }

        string? project = ResolveProjectFile(TryReadOption(args, "--project"));
        return project != null
            ? Path.Combine(Path.GetDirectoryName(project)!, "Models", "Generated")
            : fallback;
    }

    internal static string? ResolveNamespace(string[] args)
    {
        string? explicitNs = TryReadOption(args, "--namespace");
        if (!string.IsNullOrWhiteSpace(explicitNs))
        {
            return explicitNs;
        }

        string? projectFile = ResolveProjectFile(TryReadOption(args, "--project"));
        if (projectFile == null)
        {
            return null;
        }

        string prefix = StripRoleSuffix(ReadRootNamespace(projectFile));
        return $"{prefix}.Shared.Models.Generated";
    }

    private static string? ResolveProjectFile(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (File.Exists(requested) && requested.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return requested;
            }

            if (Directory.Exists(requested))
            {
                return FindProjectInDirectory(requested);
            }

            return null;
        }

        return FindProjectInDirectory(Directory.GetCurrentDirectory());
    }

    private static string? FindProjectInDirectory(string directory)
    {
        // A single top-level .csproj is the unambiguous project (running from a project folder).
        string? single = SingleCsproj(directory);
        if (single != null)
        {
            return single;
        }

        // Otherwise, when run from a solution root, derive the namespace from a representative project listed
        // in the .sln so `vela models` "just works" from the root like plan/apply do.
        return ProjectFromSolution(directory);
    }

    private static string? SingleCsproj(string directory)
    {
        string[] candidates = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        return candidates.Length == 1 ? candidates[0] : null;
    }

    // Picks a representative project from a single .sln in the directory. Any role-suffixed project yields the
    // same base namespace via StripRoleSuffix, but prefer .Shared/.Plugins/.Integrations (and avoid the test
    // project) so the derived shared-models namespace is the expected one.
    private static string? ProjectFromSolution(string directory)
    {
        string[] solutions = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
        if (solutions.Length != 1)
        {
            return null;
        }

        string[] projects = ReadSolutionProjectPaths(solutions[0]);
        if (projects.Length == 0)
        {
            return null;
        }

        foreach (string preferred in new[] { ".Shared.csproj", ".Plugins.csproj", ".Integrations.csproj" })
        {
            string? match = projects.FirstOrDefault(p => p.EndsWith(preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return projects.FirstOrDefault(p => !p.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
            ?? projects[0];
    }

    // Parses the project .csproj paths out of a .sln. Project lines look like:
    //   Project("{TypeGuid}") = "Name", "relative\Path.csproj", "{ProjectGuid}"
    private static string[] ReadSolutionProjectPaths(string solutionFile)
    {
        string solutionDir = Path.GetDirectoryName(solutionFile) ?? Directory.GetCurrentDirectory();
        List<string> paths = new List<string>();
        foreach (string line in File.ReadLines(solutionFile))
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            string rawPath = parts[1].Trim().Trim('"');
            if (!rawPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // .sln paths always use backslashes; normalize for non-Windows hosts.
            string relativePath = rawPath.Replace('\\', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
            if (File.Exists(full))
            {
                paths.Add(full);
            }
        }

        return paths.ToArray();
    }

    private static void PrintModelsUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  vela models --solution <uniquename> [connection options]");
        Console.WriteLine("  vela models --entities account,contact [connection options]");
        Console.WriteLine("  vela models --custom-apis zava_DoThing [connection options]");
        Console.WriteLine("  vela models --custom-apis [connection options]   (no value: every Custom API in the environment)");
        Console.WriteLine("  vela models --custom-apis --package bin/Zava.Plugins.nupkg [connection options]   (no value, minus your authored Custom APIs)");
        Console.WriteLine("  vela models --messages QualifyLead,AddToQueue [connection options]");
        Console.WriteLine("  vela models --messages [connection options]   (no value: all consumable messages, filtered)");
        Console.WriteLine("  vela models --roles \"Salesperson,Sales Manager\" [connection options]");
        Console.WriteLine("  vela models --forms --entities account,contact [connection options]   (preview)");
        Console.WriteLine("  vela models --tables --entities account,contact [connection options]  (preview)");
        Console.WriteLine("  vela models --forms --tables --entities account,contact [connection options]  (preview, both + one barrel)");
        Console.WriteLine();
        Console.WriteLine("Generates early-bound classes for the scoped tables/Custom APIs over your Dataverse connection.");
        Console.WriteLine("  --solution <uniquename>   tables taken from the solution's components");
        Console.WriteLine("  --entities <a,b,c>        explicit table logical names (combinable with --solution)");
        Console.WriteLine("  --custom-apis <a,b,c>     Custom API unique names → typed Request/Response classes (into Messages/)");
        Console.WriteLine("  --messages [a,b,c]        SDK message names → typed Request/Response classes into Messages/ (any sdkmessage; pairs with OnMessage<TRequest>())");
        Console.WriteLine("                            with no value: generate ALL consumable messages, skipping internal/private, Custom-API-backed, and messages that already have an SDK request class");
        Console.WriteLine("  --package <file.nupkg>    (optional, repeatable) built plugin package(s); with no-value --custom-apis, excludes the Custom APIs you authored in code");
        Console.WriteLine("  --packages-dir <folder>   (optional) a folder of .nupkg packages, used the same way as --package");
        Console.WriteLine("  --package-assembly <name> (optional) the primary assembly name inside the package(s)");
        Console.WriteLine("  --roles <a,b,c>           security role names → a dependency-light Roles class (root role id + privileges)");
        Console.WriteLine("  --solution-roles          also generate every security role in --solution");
        Console.WriteLine("  --forms                   (preview) emit vela-client TypeScript FormModel files for the scoped tables' Main/QuickCreate forms");
        Console.WriteLine("  --tables                  (preview) emit vela-client TypeScript Web API record interfaces (tables/<name>.ts) for the scoped tables");
        Console.WriteLine("                            (--forms and --tables can be combined: one run emits both plus a shared option-sets/ folder and one index.ts barrel)");
        Console.WriteLine("  --namespace <ns>          output namespace (default: <prefix>.Shared.Models.Generated, inferred from the project)");
        Console.WriteLine("  --out <dir>               output directory (default: current directory)");
        Console.WriteLine("  --verify                  do not write: check the generated entity model on disk against the live schema and report drift (exit 5 on drift; ideal for CI)");
        Console.WriteLine("  --json                    print a JSON report of the generated files (for scripts/agents)");
        Console.WriteLine("  --no-annotations          omit DataAnnotations ([StringLength]/[Range])");
        Console.WriteLine("  --overwrite               overwrite existing files");
        Console.WriteLine("  connection: --connection-string | --url + --tenant-id + --client-id + --client-secret | --interactive");
    }
}
