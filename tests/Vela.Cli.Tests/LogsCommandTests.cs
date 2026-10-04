using System;
using System.Linq;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public sealed class LogsCommandTests
{
    private static LogsCommand.LogFilters Filters(
        int top = 20, DateTime? since = null, string? type = null, string? message = null,
        string? entity = null, Guid? correlation = null, bool errorsOnly = false, bool allStatuses = false)
        => new LogsCommand.LogFilters(top, since, type, message, entity, correlation, errorsOnly, allStatuses);

    private static bool Has(QueryExpression q, string attribute, ConditionOperator op)
        => q.Criteria.Conditions.Any(c => c.AttributeName == attribute && c.Operator == op);

    // --- TryParseSince -------------------------------------------------------------------------

    [Theory]
    [InlineData("30m", 0, -30, 0)]
    [InlineData("2h", -2, 0, 0)]
    [InlineData("3d", 0, 0, -3)]
    public void TryParseSince_reads_a_relative_duration(string raw, int hours, int minutes, int days)
    {
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LogsCommand.TryParseSince(raw, now, out DateTime? since, out _));
        Assert.Equal(now.AddHours(hours).AddMinutes(minutes).AddDays(days), since);
    }

    [Fact]
    public void TryParseSince_reads_a_date()
    {
        Assert.True(LogsCommand.TryParseSince("2026-01-02", DateTime.UtcNow, out DateTime? since, out _));
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), since!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("0m")]
    public void TryParseSince_rejects_an_unparseable_value(string raw)
    {
        Assert.False(LogsCommand.TryParseSince(raw, DateTime.UtcNow, out DateTime? since, out string? error));
        Assert.Null(since);
        Assert.NotNull(error);
    }

    // --- BuildTraceLogQuery --------------------------------------------------------------------

    [Fact]
    public void Trace_query_is_newest_first_capped_and_targets_the_trace_log()
    {
        QueryExpression q = LogsCommand.BuildTraceLogQuery(Filters(top: 7));

        Assert.Equal("plugintracelog", q.EntityName);
        Assert.Equal(7, q.TopCount);
        Assert.Equal("createdon", q.Orders.Single().AttributeName);
        Assert.Equal(OrderType.Descending, q.Orders.Single().OrderType);
    }

    [Fact]
    public void Trace_query_applies_the_filters()
    {
        QueryExpression q = LogsCommand.BuildTraceLogQuery(Filters(
            since: DateTime.UtcNow, type: "AccountName", message: "Update", entity: "account",
            correlation: Guid.NewGuid(), errorsOnly: true));

        Assert.True(Has(q, "createdon", ConditionOperator.OnOrAfter));
        Assert.True(Has(q, "exceptiondetails", ConditionOperator.NotNull));   // --errors
        Assert.True(Has(q, "typename", ConditionOperator.Like));
        Assert.True(Has(q, "messagename", ConditionOperator.Like));
        Assert.True(Has(q, "primaryentity", ConditionOperator.Like));
        Assert.True(Has(q, "correlationid", ConditionOperator.Equal));
    }

    [Fact]
    public void Trace_query_without_errors_flag_does_not_require_an_exception()
    {
        QueryExpression q = LogsCommand.BuildTraceLogQuery(Filters(errorsOnly: false));

        Assert.False(Has(q, "exceptiondetails", ConditionOperator.NotNull));
    }

    // --- BuildSystemJobQuery -------------------------------------------------------------------

    [Fact]
    public void Jobs_query_defaults_to_failed_jobs()
    {
        QueryExpression q = LogsCommand.BuildSystemJobQuery(Filters());

        Assert.Equal("asyncoperation", q.EntityName);
        ConditionExpression status = q.Criteria.Conditions.Single(c => c.AttributeName == "statuscode");
        Assert.Equal(ConditionOperator.Equal, status.Operator);
        Assert.Equal(31, status.Values.Single());   // 31 = Failed
    }

    [Fact]
    public void Jobs_query_with_all_statuses_does_not_filter_on_status()
    {
        QueryExpression q = LogsCommand.BuildSystemJobQuery(Filters(allStatuses: true));

        Assert.DoesNotContain(q.Criteria.Conditions, c => c.AttributeName == "statuscode");
    }

    // --- Run early validation (before connecting) ----------------------------------------------

    [Theory]
    [InlineData("--top", "nope")]
    [InlineData("--top", "0")]
    [InlineData("--since", "bananas")]
    [InlineData("--correlation", "not-a-guid")]
    public void Run_rejects_bad_arguments_before_connecting(params string[] args)
    {
        Assert.Equal(ExitInvalidArguments, LogsCommand.Run(args));
    }

    [Fact]
    public void Run_help_succeeds()
    {
        Assert.Equal(ExitSuccess, LogsCommand.Run(new[] { "--help" }));
    }
}
