using System;
using Microsoft.Extensions.Logging;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// Leveled logging / telemetry on the context: severity + message capture, default pipeline dimensions, user
/// scopes, exceptions, metrics, and the always-on plug-in trace log fallback (so entries survive whether or not
/// Application Insights is configured). Captured via the test <see cref="RecordingLogger"/> and
/// <see cref="RecordingTracingService"/>.
/// </summary>
public sealed class TelemetryTests
{
    [Fact]
    public void Leveled_helpers_capture_severity_and_formatted_message()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.LogInformation("hello {0}", 42);
        ctx.LogWarning("careful");
        ctx.LogError("bad");

        Assert.Collection(builder.LogCapture.Entries,
            e => { Assert.Equal(LogLevel.Information, e.Level); Assert.Equal("hello 42", e.Message); },
            e => { Assert.Equal(LogLevel.Warning, e.Level); Assert.Equal("careful", e.Message); },
            e => { Assert.Equal(LogLevel.Error, e.Level); Assert.Equal("bad", e.Message); });
    }

    [Fact]
    public void Default_dimensions_from_the_context_ride_on_every_entry()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.LogInformation("x");

        LogEntry entry = Assert.Single(builder.LogCapture.Entries);
        Assert.Equal("Create", entry.Scope["MessageName"]);
        Assert.True(entry.Scope.ContainsKey("CorrelationId"));
        Assert.True(entry.Scope.ContainsKey("Stage"));
        Assert.True(entry.Scope.ContainsKey("PrimaryEntityName"));
    }

    [Fact]
    public void BeginLogScope_adds_properties_within_the_scope_only()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        using (ctx.BeginLogScope(("OrderId", 7), ("Region", "EU")))
        {
            ctx.LogInformation("inside");
        }

        ctx.LogInformation("outside");

        Assert.Equal(7, builder.LogCapture.Entries[0].Scope["OrderId"]);
        Assert.Equal("EU", builder.LogCapture.Entries[0].Scope["Region"]);
        Assert.False(builder.LogCapture.Entries[1].Scope.ContainsKey("OrderId"));
    }

    [Fact]
    public void LogError_attaches_the_exception()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();
        var boom = new InvalidOperationException("boom");

        ctx.LogError(boom, "it failed");

        LogEntry entry = Assert.Single(builder.LogCapture.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Exception);
    }

    [Fact]
    public void LogMetric_emits_a_structured_line_and_does_not_throw()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.LogMetric("OrdersProcessed", 3);

        LogEntry entry = Assert.Single(builder.LogCapture.Entries);
        Assert.Contains("OrdersProcessed", entry.Message);
        Assert.Contains("3", entry.Message);
    }

    [Fact]
    public void Every_level_also_lands_in_the_plugin_trace_log_so_entries_survive_without_app_insights()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.LogInformation("info line");
        ctx.LogWarning("warn line");

        // Information is unprefixed (back-compat with Trace); other levels carry a tag.
        Assert.Contains("info line", builder.TraceCapture.Traces);
        Assert.Contains("[Warning] warn line", builder.TraceCapture.Traces);
    }

    [Fact]
    public void Trace_still_works_and_flows_through_the_logger_at_information()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.Trace("classic {0}", "trace");

        Assert.Contains("classic trace", builder.TraceCapture.Traces);
        LogEntry entry = Assert.Single(builder.LogCapture.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("classic trace", entry.Message);
    }

    [Fact]
    public void Logging_never_throws_when_a_null_logger_is_used()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate().WithLogger(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        ILocalPluginContext ctx = builder.Build();

        ctx.LogError(new Exception("x"), "still fine");

        // The trace-log channel still records it even with a no-op logger.
        Assert.Contains("[Error] still fine", builder.TraceCapture.Traces);
    }
}
