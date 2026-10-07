using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Vela.Querying;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Public API no other test exercised (A-82): TypedFilterBuilder.ConditionNotIn and the Trace/Debug/Critical log helpers.
public sealed class ConditionNotInAndLogLevelTests
{
    [Fact]
    public void ConditionNotIn_builds_a_NotIn_condition_with_every_value()
    {
        QueryExpression query = Query.For<TestAccount>()
            .WhereAll(f => f.ConditionNotIn(a => a.Name, new[] { "Contoso", "Fabrikam" }))
            .Build();

        ConditionExpression condition = Assert.Single(query.Criteria.Filters.Single().Conditions);
        Assert.Equal("name", condition.AttributeName);
        Assert.Equal(ConditionOperator.NotIn, condition.Operator);
        Assert.Equal(new object[] { "Contoso", "Fabrikam" }, condition.Values.ToArray());
    }

    [Fact]
    public void ConditionNotIn_excludes_exactly_the_listed_records()
    {
        InMemoryOrganizationService crm = new InMemoryOrganizationService();
        foreach (string name in new[] { "Contoso", "Fabrikam", "Zava", "Northwind" })
        {
            crm.Create(new TestAccount { Name = name });
        }

        QueryExpression query = Query.For<TestAccount>()
            .Select(a => a.Name)
            .WhereAll(f => f.ConditionNotIn(a => a.Name, new[] { "Contoso", "Fabrikam" }))
            .Build();

        string[] names = crm.RetrieveMultiple(query).Entities.Select(e => e.GetAttributeValue<string>("name")).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Northwind", "Zava" }, names);
    }

    [Fact]
    public void LogTrace_LogDebug_and_LogCritical_reach_the_logger_and_the_trace_log_at_their_level()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.LogTrace("t {0}", 1);
        ctx.LogDebug("d {0}", 2);
        ctx.LogCritical("c {0}", 3);

        Assert.Collection(builder.LogCapture.Entries,
            e => { Assert.Equal(LogLevel.Trace, e.Level); Assert.Equal("t 1", e.Message); Assert.Null(e.Exception); },
            e => { Assert.Equal(LogLevel.Debug, e.Level); Assert.Equal("d 2", e.Message); Assert.Null(e.Exception); },
            e => { Assert.Equal(LogLevel.Critical, e.Level); Assert.Equal("c 3", e.Message); Assert.Null(e.Exception); });
        Assert.Contains("[Trace] t 1", builder.TraceCapture.Traces);
        Assert.Contains("[Debug] d 2", builder.TraceCapture.Traces);
        Assert.Contains("[Critical] c 3", builder.TraceCapture.Traces);
    }

    [Fact]
    public void LogCritical_with_an_exception_attaches_it_and_traces_it()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();
        InvalidOperationException boom = new InvalidOperationException("boom");

        ctx.LogCritical(boom, "down {0}", "hard");

        LogEntry entry = Assert.Single(builder.LogCapture.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Equal("down hard", entry.Message);
        Assert.Same(boom, entry.Exception);
        Assert.Contains("[Critical] down hard", builder.TraceCapture.Traces);
        Assert.Contains(builder.TraceCapture.Traces, t => t.Contains("System.InvalidOperationException: boom"));
    }

    [Fact]
    public void The_level_helpers_reject_a_null_context()
    {
        Assert.Throws<ArgumentNullException>(() => TelemetryExtensions.LogTrace(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => TelemetryExtensions.LogDebug(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => TelemetryExtensions.LogCritical(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => TelemetryExtensions.LogCritical(null!, new Exception(), "x"));
    }
}
