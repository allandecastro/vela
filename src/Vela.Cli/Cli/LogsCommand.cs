using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela logs</c> (alias <c>vela trace</c>): a read-only reader of a Dataverse environment's execution
/// diagnostics so an agent or developer can see why a plugin failed without asking a human to open the UI. Two
/// sources: the <b>Plug-in Trace Log</b> (<c>plugintracelog</c>, the default) and, with <c>--jobs</c>, failed
/// <b>system jobs</b> (<c>asyncoperation</c>) - where an asynchronous step's failure surfaces even when trace
/// logging is off. Nothing is ever written or deleted.
/// </summary>
internal static class LogsCommand
{
    private const int DefaultTop = 20;
    private const int MaxTop = 200;

    private static readonly HashSet<string> ValueOptions = CliCommands.ValueOptions("logs");

    private static readonly HashSet<string> FlagOptions = CliCommands.FlagOptions("logs");

    public static int Run(string[] args)
    {
        if ((args.Length > 0 && args[0] is "help") || CommandLineOptions.IsHelpRequested(args))
        {
            PrintUsage();
            return ExitSuccess;
        }

        if (!CommandLineOptions.TryTokenize(args, ValueOptions, FlagOptions, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out string[] rest, out int optExit))
        {
            return optExit;
        }

        bool interactive = HasFlag(rest, "--interactive") || HasFlag(rest, "-i");
        bool json = HasFlag(rest, "--json");
        bool verbose = HasFlag(rest, "--verbose") || HasFlag(rest, "-v");
        bool jobs = HasFlag(rest, "--jobs");
        bool full = HasFlag(rest, "--full");

        if (!TryReadTop(rest, out int top, out int topExit))
        {
            return topExit;
        }

        DateTime? since = null;
        string? sinceRaw = TryReadOption(rest, "--since");
        if (sinceRaw != null && !TryParseSince(sinceRaw, DateTime.UtcNow, out since, out string? sinceError))
        {
            return Fail(sinceError!, ExitInvalidArguments, includeUsage: false);
        }

        Guid? correlation = null;
        string? correlationRaw = TryReadOption(rest, "--correlation");
        if (correlationRaw != null)
        {
            if (!Guid.TryParse(correlationRaw, out Guid parsed))
            {
                return Fail($"--correlation must be a GUID, got '{correlationRaw}'.", ExitInvalidArguments, includeUsage: false);
            }

            correlation = parsed;
        }

        LogFilters filters = new LogFilters(
            Top: top,
            Since: since,
            Type: TryReadOption(rest, "--type"),
            Message: TryReadOption(rest, "--message"),
            Entity: TryReadOption(rest, "--entity"),
            Correlation: correlation,
            ErrorsOnly: HasFlag(rest, "--errors"),
            AllStatuses: HasFlag(rest, "--all-statuses"));

        if (!TryResolveConnection(rest, interactive, out string? connectionString, out string? oauthUrl, out DataverseTokenProvider? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        if (!TryReadTimeoutMinutes(rest, out int? timeoutMinutes, out int timeoutExit))
        {
            return timeoutExit;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: null,
            Environment: url,
            OutPath: null,
            AutoApprove: false,
            Interactive: interactive,
            Strict: false,
            Verbose: verbose,
            TimeoutMinutes: timeoutMinutes);

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            QueryExpression query = jobs ? BuildSystemJobQuery(filters) : BuildTraceLogQuery(filters);
            List<Entity> rows = service.RetrieveMultiple(query).Entities.ToList();

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    mode = jobs ? "jobs" : "logs",
                    count = rows.Count,
                    logs = rows.Select(row => jobs ? MapJob(row) : MapTrace(row)).ToList(),
                }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            }
            else
            {
                PrintHuman(rows, jobs, full);
            }

            return ExitSuccess;
        }
        catch (Exception ex) when (DataverseConnectionException.IsConnectionFailure(ex))
        {
            return FailWithException(ex, ExitConnectionFailure, verbose);
        }
        catch (Exception ex)
        {
            return Fail($"Could not read {(jobs ? "system jobs" : "the plug-in trace log")}: {ex.Message}", ExitError, includeUsage: false);
        }
    }

    // The parsed filter set. Pure, so the query builders below are unit-testable at the IOrganizationService seam.
    internal sealed record LogFilters(int Top, DateTime? Since, string? Type, string? Message, string? Entity, Guid? Correlation, bool ErrorsOnly, bool AllStatuses);

    // plugintracelog, newest first. --errors keeps only rows carrying an exception (non-empty exceptiondetails); the other filters narrow by
    // plugin type / message / primary entity / correlation / time.
    internal static QueryExpression BuildTraceLogQuery(LogFilters filters)
    {
        QueryExpression query = new QueryExpression("plugintracelog")
        {
            ColumnSet = new ColumnSet("createdon", "typename", "messagename", "primaryentity", "depth", "mode", "performanceexecutionduration", "exceptiondetails", "messageblock", "correlationid"),
            TopCount = filters.Top,
        };
        query.AddOrder("createdon", OrderType.Descending);

        if (filters.Since is DateTime since)
        {
            query.Criteria.AddCondition("createdon", ConditionOperator.OnOrAfter, since);
        }

        if (filters.ErrorsOnly)
        {
            // Dataverse stores exceptiondetails as an empty string, not null, on a row without an exception: NotNull
            // alone keeps every row. NotEqual "" drops those; NotNull keeps the intent explicit for a null value.
            query.Criteria.AddCondition("exceptiondetails", ConditionOperator.NotNull);
            query.Criteria.AddCondition("exceptiondetails", ConditionOperator.NotEqual, string.Empty);
        }

        AddTextCondition(query, "typename", filters.Type);
        AddTextCondition(query, "messagename", filters.Message);
        AddTextCondition(query, "primaryentity", filters.Entity);
        if (filters.Correlation is Guid correlation)
        {
            query.Criteria.AddCondition("correlationid", ConditionOperator.Equal, correlation);
        }

        return query;
    }

    // asyncoperation (system jobs), newest first. Defaults to failed jobs (statuscode 31) - where an async step's
    // failure is recorded even when trace logging is off - unless --all-statuses is given.
    internal static QueryExpression BuildSystemJobQuery(LogFilters filters)
    {
        const int statusFailed = 31;
        QueryExpression query = new QueryExpression("asyncoperation")
        {
            ColumnSet = new ColumnSet("createdon", "completedon", "name", "regardingobjectid", "messagename", "statuscode", "operationtype", "message", "friendlymessage"),
            TopCount = filters.Top,
        };
        query.AddOrder("createdon", OrderType.Descending);

        if (!filters.AllStatuses)
        {
            query.Criteria.AddCondition("statuscode", ConditionOperator.Equal, statusFailed);
        }

        if (filters.Since is DateTime since)
        {
            query.Criteria.AddCondition("createdon", ConditionOperator.OnOrAfter, since);
        }

        AddTextCondition(query, "name", filters.Type);
        AddTextCondition(query, "messagename", filters.Message);
        if (filters.Correlation is Guid correlation)
        {
            query.Criteria.AddCondition("correlationid", ConditionOperator.Equal, correlation);
        }

        return query;
    }

    // A non-empty filter value matches by substring (contains), so `--type AccountName` finds the full namespaced
    // type; an empty/absent value adds no condition.
    private static void AddTextCondition(QueryExpression query, string attribute, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Criteria.AddCondition(attribute, ConditionOperator.Like, $"%{value}%");
        }
    }

    // Accepts a relative duration (30m / 2h / 3d) or an ISO date, returning the UTC threshold.
    internal static bool TryParseSince(string raw, DateTime nowUtc, out DateTime? since, out string? error)
    {
        since = null;
        error = null;
        raw = raw.Trim();
        if (raw.Length >= 2 && char.IsDigit(raw[0]))
        {
            char unit = char.ToLowerInvariant(raw[^1]);
            if (unit is 'm' or 'h' or 'd' && int.TryParse(raw[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) && amount > 0)
            {
                since = unit switch
                {
                    'm' => nowUtc.AddMinutes(-amount),
                    'h' => nowUtc.AddHours(-amount),
                    _ => nowUtc.AddDays(-amount),
                };
                return true;
            }
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            since = parsed;
            return true;
        }

        error = $"--since must be a duration (e.g. 30m, 2h, 3d) or a date, got '{raw}'.";
        return false;
    }

    private static object MapTrace(Entity row) => new
    {
        createdOn = row.GetAttributeValue<DateTime?>("createdon"),
        type = row.GetAttributeValue<string>("typename"),
        message = row.GetAttributeValue<string>("messagename"),
        entity = row.GetAttributeValue<string>("primaryentity"),
        depth = row.GetAttributeValue<int?>("depth"),
        durationMs = row.GetAttributeValue<int?>("performanceexecutionduration"),
        exception = NullIfEmpty(row.GetAttributeValue<string>("exceptiondetails")),
        trace = NullIfEmpty(row.GetAttributeValue<string>("messageblock")),
        correlationId = row.GetAttributeValue<Guid?>("correlationid"),
    };

    private static object MapJob(Entity row) => new
    {
        createdOn = row.GetAttributeValue<DateTime?>("createdon"),
        completedOn = row.GetAttributeValue<DateTime?>("completedon"),
        name = row.GetAttributeValue<string>("name"),
        message = row.GetAttributeValue<string>("messagename"),
        regarding = row.GetAttributeValue<EntityReference>("regardingobjectid")?.Name,
        status = row.GetAttributeValue<OptionSetValue>("statuscode")?.Value,
        error = NullIfEmpty(row.GetAttributeValue<string>("friendlymessage")) ?? NullIfEmpty(row.GetAttributeValue<string>("message")),
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static void PrintHuman(List<Entity> rows, bool jobs, bool full)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine(jobs
                ? "No matching system jobs."
                : "No plug-in trace log entries matched. If you expected some, plug-in tracing may be disabled (Settings > Administration > System Settings > Customization > Enable logging to plug-in trace log: set to All or Exception).");
            return;
        }

        foreach (Entity row in rows)
        {
            if (jobs)
            {
                string? error = NullIfEmpty(row.GetAttributeValue<string>("friendlymessage")) ?? NullIfEmpty(row.GetAttributeValue<string>("message"));
                Console.WriteLine(OutputWriter.EscapeControl($"{Stamp(row)}  {row.GetAttributeValue<string>("name")}  {row.GetAttributeValue<string>("messagename")}"));
                if (error != null)
                {
                    Console.WriteLine($"  ERROR  {Trim(error, full)}");
                }
            }
            else
            {
                int? ms = row.GetAttributeValue<int?>("performanceexecutionduration");
                Console.WriteLine(OutputWriter.EscapeControl($"{Stamp(row)}  {row.GetAttributeValue<string>("typename")}  {row.GetAttributeValue<string>("messagename")} {row.GetAttributeValue<string>("primaryentity")}  (depth {row.GetAttributeValue<int?>("depth")}, {ms}ms)"));
                string? exception = NullIfEmpty(row.GetAttributeValue<string>("exceptiondetails"));
                if (exception != null)
                {
                    Console.WriteLine($"  EXCEPTION  {Trim(exception, full)}");
                }

                string? trace = NullIfEmpty(row.GetAttributeValue<string>("messageblock"));
                if (trace != null)
                {
                    Console.WriteLine($"  {Trim(trace, full)}");
                }
            }
        }
    }

    private static string Stamp(Entity row) =>
        row.GetAttributeValue<DateTime?>("createdon")?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "(no timestamp)";

    private static string Trim(string value, bool full)
    {
        // Trace text is written by plugin code: escape its control characters so it cannot drive the terminal.
        value = OutputWriter.EscapeControl(value.Replace("\r\n", " ").Replace('\n', ' ').Trim());
        return full || value.Length <= 300 ? value : value[..300] + "… (--full for the rest)";
    }

    private static bool TryReadTop(string[] args, out int top, out int exitCode)
    {
        top = DefaultTop;
        exitCode = ExitSuccess;
        string? raw = TryReadOption(args, "--top");
        if (raw == null)
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1 || value > MaxTop)
        {
            exitCode = Fail($"--top must be between 1 and {MaxTop}, got '{raw}'.", ExitInvalidArguments, includeUsage: false);
            return false;
        }

        top = value;
        return true;
    }

    private static void PrintUsage() => CliCommands.PrintCommandHelp("logs");
}
