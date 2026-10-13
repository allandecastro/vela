using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela models</c>: reads table metadata over the same Dataverse connection as plan/apply and
/// generates early-bound classes via <see cref="ModelGenerator"/>. Scope by <c>--solution</c> (derived from the
/// solution's components — what <c>pac modelbuilder</c> can't do) and/or an explicit <c>--entities</c> list.
/// The scope parsing, Dataverse reads and namespace resolution are delegated to focused collaborators
/// (<see cref="ModelsScope"/>, <see cref="EntityMetadataReader"/>, <see cref="CustomApiReader"/>,
/// <see cref="RoleReader"/>, <see cref="ProjectNamespaceResolver"/>); this class orchestrates them.
/// </summary>
internal static class ModelsCommand
{
    // The models allowlist, from the command table (CliCommands): its own options plus the shared connection options.
    // --messages / --custom-apis may appear bare (present with no value = "all").
    private static readonly HashSet<string> ValueOptions = CliCommands.ValueOptions("models");

    private static readonly HashSet<string> FlagOptions = CliCommands.FlagOptions("models");

    private static readonly HashSet<string> OptionalValueOptions = CliCommands.OptionalValueOptions("models");

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

        // Offline mode: generate C# early-bound models + option-set enums from an exported solution zip, with no
        // Dataverse connection. Branch before scope/connection handling since the source of truth is the file.
        string? fromSolution = TryReadOption(args, "--from-solution");
        if (!string.IsNullOrWhiteSpace(fromSolution))
        {
            return RunFromSolution(args, fromSolution!);
        }

        ModelsScope scope = ModelsScope.Parse(args);

        if (scope.IsEmpty)
        {
            return Fail("Specify a scope: --solution <uniquename>, --entities <a,b,c>, --custom-apis [<a,b,c>], --messages [<a,b,c>], --roles <a,b,c> and/or --solution-roles. (Refusing to generate every table.)", ExitInvalidArguments, includeUsage: false);
        }

        if (scope.SolutionRoles && string.IsNullOrWhiteSpace(scope.Solution))
        {
            return Fail("--solution-roles requires --solution <uniquename>.", ExitInvalidArguments, includeUsage: false);
        }

        // Bare `--custom-apis` generates EVERY Custom API in the environment, which includes the platform's own
        // (hundreds of files). That surprises callers who expected only their own, so warn up front and point at
        // the ways to narrow it. (`--package`/`--packages-dir` already excludes the typed Custom APIs authored in code.)
        bool excludesAuthored = HasFlag(args, "--package") || HasFlag(args, "--packages-dir");
        if (scope.AllCustomApis && !excludesAuthored)
        {
            Console.Error.WriteLine("Warning: --custom-apis with no value generates every Custom API in the environment, including system ones (this can be hundreds of files). Pass --custom-apis <a,b,c> to target specific ones, or --package/--packages-dir to exclude the typed Custom APIs you authored in code.");
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
            string scopeLabel = forms && tables ? "--forms --tables" : forms ? "--forms" : "--tables";

            // --verify is a C#-model-only drift check; combining it with the write-only TS scopes would otherwise
            // silently write files, so reject it rather than surprising a CI check.
            if (HasFlag(args, "--verify"))
            {
                return Fail($"--verify checks the C# early-bound model only; it cannot be combined with {scopeLabel}.", ExitInvalidArguments, includeUsage: false);
            }

            // --intersect generates C# entity classes for N:N join tables; it has no meaning for TypeScript output.
            if (scope.Intersect)
            {
                return Fail($"--intersect generates C# entity models; it cannot be combined with {scopeLabel}.", ExitInvalidArguments, includeUsage: false);
            }

            // TypeScript is the only output for the client-side scopes, so --lang defaults to ts and only an
            // explicit non-ts value is rejected.
            string tsLang = (langOption ?? "ts").Trim().ToLowerInvariant();
            if (tsLang != "ts")
            {
                return Fail($"vela models {scopeLabel} only supports TypeScript output.", ExitInvalidArguments, includeUsage: false);
            }

            if (string.IsNullOrWhiteSpace(scope.Solution) && scope.Entities.Length == 0)
            {
                return Fail($"vela models {scopeLabel} needs a table scope: --solution <uniquename> and/or --entities <a,b,c>.", ExitInvalidArguments, includeUsage: false);
            }

            // These scopes only read tables; anything else is ignored, so say so rather than dropping it silently.
            if (scope.HasCustomApiMessageOrRoleScope)
            {
                Console.Error.WriteLine($"Warning: {scopeLabel} generates TypeScript output only; --custom-apis / --messages / --roles / --solution-roles are ignored.");
            }

            return RunTsModels(args, forms, tables, scope.Solution, scope.Entities, outDir, overwrite, interactive, json);
        }

        string lang = (langOption ?? "csharp").Trim().ToLowerInvariant();
        if (lang != "csharp")
        {
            return Fail("--lang applies to --forms / --tables (TypeScript output) only.", ExitInvalidArguments, includeUsage: false);
        }

        string? ns = ProjectNamespaceResolver.ResolveNamespace(args);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return Fail("Could not infer a namespace. Pass --namespace <ns> (or run inside a project so it can be derived).", ExitInvalidArguments, includeUsage: false);
        }

        // With no explicit --out, write into the inferred project's Models/Generated folder so the files land where
        // the namespace (<Prefix>.Shared.Models.Generated) says, instead of the project root.
        outDir = ProjectNamespaceResolver.ResolveCSharpOutputDir(args, outDir);

        bool dataAnnotations = !HasFlag(args, "--no-annotations");

        // --verify checks only the explicitly scoped tables; it does not expand to the discovered intersect tables,
        // and the drift checker ignores out-of-scope local files, so a stale intersect model would pass unnoticed.
        // Reject the combination rather than give a false all-clear.
        if (scope.Intersect && HasFlag(args, "--verify"))
        {
            return Fail("--intersect cannot be combined with --verify (verify checks the explicitly scoped tables only).", ExitInvalidArguments, includeUsage: false);
        }

        // --verify does not write: it regenerates the scoped entity models from live metadata in memory and
        // compares them, column by column, against the files already on disk, then reports drift. It verifies the
        // C# early-bound entity model only (the typed-image blind spot); custom-api/message/role scopes are ignored.
        if (HasFlag(args, "--verify"))
        {
            return RunModelVerify(args, interactive, scope.Solution, scope.Entities, ns!, outDir, dataAnnotations, json, scope.HasCustomApiMessageOrRoleScope);
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
        HashSet<string> typedAuthoredCustomApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> lightweightCustomApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scope.AllCustomApis && packagePaths.Count > 0)
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
                RegistrationState scanned = IsolatedPackageScan.Scan(scanOptions, scanJson, tempDirectories);
                typedAuthoredCustomApis = CustomApiReader.TypedAuthoredCustomApiUniqueNames(scanned);
                lightweightCustomApis = CustomApiReader.LightweightAuthoredCustomApiUniqueNames(scanned);
            }
            catch (Exception ex)
            {
                // A bad/unreadable package must fail with a clean exit code, not a raw stack trace (mirrors how
                // plan/apply surface a scan failure).
                return Fail($"Could not scan the plugin package(s) to exclude authored Custom APIs: {ex.Message}", ExitError, includeUsage: false);
            }
            finally
            {
                PackageScanner.CleanupTempDirectories(tempDirectories);
            }
        }

        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        if (!TryReadTimeoutMinutes(args, out int? timeoutMinutes, out int timeoutExit))
        {
            return timeoutExit;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: scope.Solution,
            Environment: url,
            OutPath: outDir,
            AutoApprove: false,
            Interactive: interactive,
            Strict: false,
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"),
            TimeoutMinutes: timeoutMinutes);

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<GeneratedModel> models = new List<GeneratedModel>();

            // Lazily resolved logical-name -> early-bound type-name (SchemaName) map, so the generated
            // On<Message>() step extensions reference the exact model type (e.g. new_CustomEntity), not a naive
            // PascalCase of the logical name. Built once, only when a message/custom-api generation needs it.
            IReadOnlyDictionary<string, string>? entityTypeNamesCache = null;
            IReadOnlyDictionary<string, string> EntityTypeNames() => entityTypeNamesCache ??= EntityMetadataReader.BuildEntityTypeNameMap(service);

            List<string> logicalNames = EntityMetadataReader.ResolveLogicalNames(service, scope.Solution, scope.Entities);
            if (logicalNames.Count > 0)
            {
                // Read each entity's metadata independently: a single bad/unreadable name (a typo, a table the caller
                // cannot access) must not discard every other valid model. Skip it with a warning, matching the TS
                // table path and the message path below. (#305)
                List<EntityMetadata> metadata = new List<EntityMetadata>();
                foreach (string name in logicalNames)
                {
                    try
                    {
                        metadata.Add(EntityMetadataReader.ReadEntity(service, name));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: skipping entity '{name}' (could not read its metadata): {RedactSecrets(ex.Message)}");
                    }
                }

                // --intersect: also generate the N:N join tables of the selected entities, so typed queries can
                // target them. One level, opt-in; the intersect names are discovered from the relationship metadata
                // (callers rarely know them) and de-duplicated against what was already read.
                if (scope.Intersect && metadata.Count > 0)
                {
                    int added = EntityMetadataReader.AppendIntersects(
                        metadata,
                        name => EntityMetadataReader.ReadEntity(service, name),
                        skip => Console.Error.WriteLine(skip));
                    Console.Error.WriteLine(added > 0
                        ? $"Including {added} N:N intersect table(s) (--intersect)."
                        : "No N:N intersect tables found for the scoped tables (--intersect).");
                }

                if (metadata.Count > 0)
                {
                    models.AddRange(ModelGenerator.Generate(metadata.ToArray(), new ModelGenOptions(ns!, dataAnnotations)));
                }
            }

            if (scope.CustomApis.Length > 0 || scope.AllCustomApis)
            {
                CustomApiMessage[] customApiMessages;
                if (scope.AllCustomApis)
                {
                    // Bare --custom-apis = every Custom API in the environment. Read them all in a few paged queries
                    // (one per table) and join in memory, instead of one-plus-two round-trips per API, which times
                    // out on large environments (#211). Then drop the typed Custom APIs the developer authored in the
                    // provided package(s) (they already have those request/response classes); lightweight ones have
                    // no request class in code and are kept. No package = nothing excluded.
                    IReadOnlyList<CustomApiMessage> all = CustomApiReader.ReadAllCustomApis(service);
                    ISet<string> kept = new HashSet<string>(
                        CustomApiReader.ExcludeAuthoredCustomApis(all.Select(m => m.UniqueName), typedAuthoredCustomApis, out int excludedCount),
                        StringComparer.OrdinalIgnoreCase);
                    customApiMessages = all.Where(m => kept.Contains(m.UniqueName)).ToArray();
                    if (excludedCount > 0)
                    {
                        Console.Error.WriteLine($"Excluding {excludedCount} typed Custom API(s) authored in the provided package(s).");
                    }

                    int lightweightKept = customApiMessages.Count(m => lightweightCustomApis.Contains(m.UniqueName));
                    if (lightweightKept > 0)
                    {
                        Console.Error.WriteLine($"Keeping {lightweightKept} lightweight Custom API(s) authored in the provided package(s) (no request class in code).");
                    }
                }
                else
                {
                    // Targeted list: the user asked for these by name, so generate exactly them — never filtered.
                    customApiMessages = scope.CustomApis.Select(name => CustomApiReader.ReadCustomApi(service, name)).ToArray();
                }

                models.AddRange(CustomApiMessageGenerator.Generate(customApiMessages, ns!, EntityTypeNames(), logicalNames));
            }

            if (scope.AllMessages)
            {
                // ALL mode: every non-private, non-Custom-API-backed message (filtered by the reader), one contract per
                // request it serves. A request that already has an SDK request class gets no duplicate class, only its
                // entity-constrained On<Message>() over the SDK type (none for a curated trigger, which already
                // exists on the builder). Because the reader already drops Custom-API-backed messages, the
                // developer's own authored Custom APIs never reach --messages (they surface only under --custom-apis,
                // filtered above); no package-based exclusion is needed here. A single unreadable message is skipped
                // with a warning rather than aborting the batch.
                List<SdkMessage> sdkMessages = new List<SdkMessage>();
                foreach (GeneratableMessage candidate in SdkMessageReader.ListGeneratableMessages(service))
                {
                    string name = candidate.Name;
                    try
                    {
                        // The classes are generated per request, under the name Dataverse executes: the Send
                        // sdkmessage is executed as SendEmail, SendFax and SendTemplate. A request the SDK already
                        // ships gets only its step extension over the SDK type (PreferSdkType).
                        foreach (string requestName in SdkMessageReader.ListRequestNames(service, name))
                        {
                            if (PreferSdkType(SdkMessageReader.ReadSdkMessage(service, requestName)) is { } contract)
                            {
                                sdkMessages.Add(contract);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: skipping message '{name}' (could not read its contract): {RedactSecrets(ex.Message)}");
                    }
                }

                models.AddRange(SdkMessageGenerator.Generate(sdkMessages, ns!, EntityTypeNames(), logicalNames));
            }
            else if (scope.Messages.Length > 0)
            {
                // Targeted: the user asked for these requests by their real name (what Execute takes), so generate
                // exactly them — never silently skipped, never expanded. A request the SDK already ships gets only its
                // step extension over the SDK type; a curated trigger gets nothing.
                List<SdkMessage> sdkMessages = new List<SdkMessage>();
                // The names are SDK request names (SendEmail), the ones Dataverse executes; an sdkmessage name whose
                // requests are named otherwise (Send) is rejected by the reader with the names to use.
                foreach (string name in scope.Messages.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    SdkMessage? contract = PreferSdkType(SdkMessageReader.ReadSdkMessage(service, name));
                    if (contract is null)
                    {
                        Console.Error.WriteLine($"Note: '{name}' has a curated trigger (On{name}()) and an SDK request class; nothing to generate.");
                        continue;
                    }

                    if (contract.SdkRequestType is not null)
                    {
                        Console.Error.WriteLine($"Note: '{contract.Name}' ships in the SDK ({contract.SdkRequestType}); generating only its On{contract.Name}() step extension over that type.");
                    }

                    sdkMessages.Add(contract);
                }

                models.AddRange(SdkMessageGenerator.Generate(sdkMessages, ns!, EntityTypeNames(), logicalNames));
            }

            if (scope.Roles.Length > 0 || scope.SolutionRoles)
            {
                IReadOnlyList<RoleModel> roleModels = RoleReader.ReadRoles(service, scope.Roles, scope.SolutionRoles ? scope.Solution : null);
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
    // A request the SDK already ships keeps its SDK class: the generator emits only the step extension over it, and
    // nothing at all for a curated trigger (OnCreate(), OnWinOpportunity(), ...), which the builder already has.
    internal static SdkMessage? PreferSdkType(SdkMessage contract)
    {
        if (!SdkMessageClassResolver.TryGetSdkRequestTypeName(contract.Name, out string sdkType))
        {
            return contract;
        }

        return AddCommand.KnownMessages.Contains(contract.Name, StringComparer.OrdinalIgnoreCase)
            ? null
            : contract with { SdkRequestType = sdkType };
    }

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

        if (!TryReadTimeoutMinutes(args, out int? timeoutMinutes, out int timeoutExit))
        {
            return timeoutExit;
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
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"),
            TimeoutMinutes: timeoutMinutes);

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<string> logicalNames = EntityMetadataReader.ResolveLogicalNames(service, solution, entities);
            if (logicalNames.Count == 0)
            {
                return Fail("No tables resolved from the given scope.", ExitInvalidArguments, includeUsage: false);
            }

            EntityMetadata[] metadata = logicalNames.Select(name => EntityMetadataReader.ReadEntity(service, name)).ToArray();
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

        if (!TryReadTimeoutMinutes(args, out int? timeoutMinutes, out int timeoutExit))
        {
            return timeoutExit;
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
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"),
            TimeoutMinutes: timeoutMinutes);

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<string> logicalNames = EntityMetadataReader.ResolveLogicalNames(service, solution, entities);
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
                    EntityMetadata read = EntityMetadataReader.ReadEntity(service, logicalName);
                    metaCache[logicalName] = read;
                    return read;
                }
                catch (Exception ex)
                {
                    unreadable.Add(logicalName);
                    Console.Error.WriteLine($"Warning: skipping table '{logicalName}' (metadata read failed): {RedactSecrets(ex.Message)}");
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

    // Offline C# early-bound generation from an exported (unmanaged) solution zip: no Dataverse connection. Reads
    // table metadata from the zip's customizations.xml and feeds it to the same ModelGenerator the online path uses.
    // Scope is the zip's content, optionally narrowed by --entities. TypeScript, custom-apis/messages/roles and
    // --verify need live metadata and are rejected here.
    private static int RunFromSolution(string[] args, string zipPath)
    {
        if (HasFlag(args, "--forms") || HasFlag(args, "--tables"))
        {
            return Fail("--from-solution generates C# early-bound models only; it cannot be combined with --forms / --tables.", ExitInvalidArguments, includeUsage: false);
        }

        if (HasFlag(args, "--verify"))
        {
            return Fail("--verify needs live metadata to compare against; it cannot be combined with --from-solution.", ExitInvalidArguments, includeUsage: false);
        }

        if (HasFlag(args, "--intersect"))
        {
            return Fail("--intersect needs live metadata to discover the N:N join tables; it cannot be combined with --from-solution.", ExitInvalidArguments, includeUsage: false);
        }

        string lang = (TryReadOption(args, "--lang") ?? "csharp").Trim().ToLowerInvariant();
        if (lang != "csharp")
        {
            return Fail("--from-solution supports C# output only.", ExitInvalidArguments, includeUsage: false);
        }

        string? ns = ProjectNamespaceResolver.ResolveNamespace(args);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return Fail("Could not infer a namespace. Pass --namespace <ns> (or run inside a project so it can be derived).", ExitInvalidArguments, includeUsage: false);
        }

        string outDir = ProjectNamespaceResolver.ResolveCSharpOutputDir(args, Path.GetFullPath(TryReadOption(args, "--out") ?? "."));
        bool overwrite = HasFlag(args, "--overwrite");
        bool dataAnnotations = !HasFlag(args, "--no-annotations");
        bool json = HasFlag(args, "--json");

        string fullPath = Path.GetFullPath(zipPath);
        if (!File.Exists(fullPath))
        {
            return Fail($"Solution file not found: {fullPath}", ExitInvalidArguments, includeUsage: false);
        }

        // Reuse --entities parsing to optionally narrow the tables generated from the zip.
        string[] entityFilter = ModelsScope.Parse(args).Entities;

        IReadOnlyList<EntityMetadata> entities;
        try
        {
            entities = SolutionMetadataReader.ReadFromSolutionZip(fullPath, entityFilter.Length > 0 ? entityFilter : null);
        }
        catch (Exception ex)
        {
            return Fail($"Could not read models from solution '{fullPath}': {ex.Message}", ExitError, includeUsage: false);
        }

        if (entities.Count == 0)
        {
            return Fail("No table metadata with columns was found in the solution. An exported solution only carries full metadata for the custom tables and columns it contains, so add them to the solution (or check --entities).", ExitError, includeUsage: false);
        }

        List<GeneratedModel> models = ModelGenerator.Generate(entities, new ModelGenOptions(ns!, dataAnnotations)).ToList();
        return WriteModels(models, outDir, overwrite, json, $" (namespace {ns}, offline from {Path.GetFileName(fullPath)})");
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

        // Defence in depth: a generated file name is always a relative path under outDir, but assert every resolved
        // path stays inside so a malformed or hostile file-name segment can never write outside the output directory.
        string outRoot = Path.GetFullPath(outDir);
        foreach (GeneratedModel model in models)
        {
            string resolved = Path.GetFullPath(Path.Combine(outDir, model.FileName));
            if (!string.Equals(resolved, outRoot, StringComparison.Ordinal)
                && !resolved.StartsWith(outRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Fail($"Generated file name '{model.FileName}' resolves outside the output directory. Refusing to write.", ExitInvalidArguments, includeUsage: false);
            }
        }

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
            File.WriteAllText(target, OutputWriter.NormalizeNewlines(model.Code));
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

    private static void PrintModelsUsage() => CliCommands.PrintCommandHelp("models");
}
