using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Moq;
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
    public void LogMetric_sends_a_custom_metric_with_default_and_caller_dimensions()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate();
        ILocalPluginContext ctx = builder.Build();

        ctx.LogMetric("OrdersProcessed", 3, new Dictionary<string, object?> { ["Region"] = "EU", ["Batch"] = 2.5 });

        MetricEntry metric = Assert.Single(builder.LogCapture.Metrics);
        Assert.Equal("OrdersProcessed", metric.Name);
        Assert.Equal(3L, metric.Value);
        Assert.Equal("EU", metric.Dimensions["Region"]);
        Assert.Equal("2.5", metric.Dimensions["Batch"]);
        Assert.Equal("Create", metric.Dimensions["MessageName"]);
        Assert.True(metric.Dimensions.ContainsKey("CorrelationId"));

        // The metric is not duplicated as an Application Insights trace; the trace log still gets a line.
        Assert.Empty(builder.LogCapture.Entries);
        Assert.Contains("[Metric] OrdersProcessed=3", builder.TraceCapture.Traces);
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
    public void Logging_never_throws_when_no_logger_is_registered()
    {
        var builder = PluginContext.For<TestAccount>().OnCreate().WithLogger(null!);
        ILocalPluginContext ctx = builder.Build();

        ctx.LogError(new Exception("x"), "still fine");
        ctx.LogMetric("m", 1);
        using (ctx.BeginLogScope(("k", 1)))
        {
            ctx.LogInformation("scoped");
        }

        // The trace-log channel still records it without an Application Insights logger.
        Assert.Contains("[Error] still fine", builder.TraceCapture.Traces);
        Assert.Contains("  scoped", builder.TraceCapture.Traces);
    }

    // Contract tests against the real SDK type, registered under the service type the platform serves it as.
    // Nothing here goes through the recording fake, so they pin the exact calls production makes.

    [Fact]
    public void Log_calls_the_platform_logger_with_the_text_as_an_argument_not_a_template()
    {
        var logger = new Mock<ILogger>();
        var builder = PluginContext.For<TestAccount>().OnCreate().WithLogger(logger.Object);
        ILocalPluginContext ctx = builder.Build();
        var boom = new InvalidOperationException("boom");

        ctx.LogWarning("total {0} for {{json}}", 5);
        ctx.LogError(boom, "failed");

        logger.Verify(l => l.Log(LogLevel.Warning, (Exception)null!, "{0}", It.Is<object[]>(a => a.Length == 1 && (string)a[0] == "total 5 for {json}")), Times.Once);
        logger.Verify(l => l.Log(LogLevel.Error, boom, "{0}", It.Is<object[]>(a => (string)a[0] == "failed")), Times.Once);
        logger.Verify(l => l.BeginScope(It.Is<Dictionary<string, object?>>(d => (string?)d["MessageName"] == "Create")), Times.Exactly(2));
    }

    [Fact]
    public void BeginLogScope_and_LogMetric_reach_the_platform_logger()
    {
        var scope = new Mock<IDisposable>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.BeginScope(It.IsAny<IReadOnlyDictionary<string, object?>>())).Returns(scope.Object);
        var builder = PluginContext.For<TestAccount>().OnCreate().WithLogger(logger.Object);
        ILocalPluginContext ctx = builder.Build();

        using (ctx.BeginLogScope(("OrderId", 7)))
        {
        }

        ctx.LogMetric("Orders", 4, new Dictionary<string, object?> { ["Region"] = "EU" });

        logger.Verify(l => l.BeginScope(It.Is<IReadOnlyDictionary<string, object?>>(d => (int?)d["OrderId"] == 7)), Times.Once);
        scope.Verify(s => s.Dispose(), Times.Once);
        logger.Verify(l => l.LogMetric("Orders", It.Is<IDictionary<string, string>>(d => d["Region"] == "EU" && d["MessageName"] == "Create"), 4L), Times.Once);
    }

    [Fact]
    public void A_throwing_platform_logger_never_surfaces_to_the_handler()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<object[]>())).Throws(new InvalidOperationException("ai down"));
        logger.Setup(l => l.BeginScope(It.IsAny<IReadOnlyDictionary<string, object?>>())).Throws(new InvalidOperationException("ai down"));
        logger.Setup(l => l.LogMetric(It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<long>())).Throws(new InvalidOperationException("ai down"));
        var builder = PluginContext.For<TestAccount>().OnCreate().WithLogger(logger.Object);
        ILocalPluginContext ctx = builder.Build();

        using (ctx.BeginLogScope(("k", 1)))
        {
            ctx.LogInformation("still traced");
        }

        ctx.LogMetric("m", 1);

        Assert.Contains("  still traced", builder.TraceCapture.Traces);
        Assert.Contains(builder.TraceCapture.Traces, t => t.StartsWith("Application Insights logging failed", StringComparison.Ordinal));
        Assert.Contains(builder.TraceCapture.Traces, t => t.StartsWith("Application Insights metric failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_injection_serves_the_platform_logger()
    {
        var logger = new RecordingLogger();
        LocalPluginContext ctx = PluginContext.For<TestAccount>().OnCreate().WithLogger(logger).Build();

        Assert.Same(logger, ctx.Logger);
        Assert.Same(logger, ctx.Resolver.GetService(typeof(ILogger)));
    }
}
