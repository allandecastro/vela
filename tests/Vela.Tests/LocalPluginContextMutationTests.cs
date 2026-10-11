using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Moq;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Pins the LocalPluginContext behaviours the mutation run (#448) left unchecked: the shared-variable guard on a null
// key, a template without arguments logged verbatim, a dimension whose value renders to null, the trace lines written
// when the Application Insights scope fails to open or close, and the message when no execution context is served.
public sealed class LocalPluginContextMutationTests
{
    private static PluginContextBuilder Builder()
    {
        PluginContextBuilder builder = new();
        builder.OnMessage("Update");
        builder.Context.PrimaryEntityName = "account";
        return builder;
    }

    // ToString returning null makes Convert.ToString return null, so the dimension falls back to an empty string.
    private sealed class NullText
    {
        public override string ToString() => null!;
    }

    [Fact]
    public void A_null_shared_variable_key_reads_nothing()
    {
        PluginContextBuilder builder = Builder();
        builder.WithSharedVariable("tag", "t");
        LocalPluginContext ctx = builder.Build();

        Assert.Null(ctx.GetSharedVariable(null!));
        Assert.False(ctx.TryGetSharedVariable(null!, out string value));
        Assert.Null(value);
        Assert.Equal("t", ctx.GetSharedVariable("tag"));
    }

    [Fact]
    public void A_message_without_arguments_is_not_run_through_string_format()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.Trace("literal {{braces}}");
        ctx.Log(LogLevel.Warning, null, "warn {{x}}", null!);

        Assert.Equal(new[] { "literal {{braces}}", "[Warning] warn {{x}}" }, builder.TraceCapture.Traces.ToArray());
        Assert.Equal(new[] { "literal {{braces}}", "warn {{x}}" }, builder.LogCapture.Entries.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void A_metric_property_that_renders_to_null_becomes_an_empty_dimension()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.LogMetric("Count", 4, new Dictionary<string, object?> { ["Odd"] = new NullText(), ["Missing"] = null });

        MetricEntry metric = Assert.Single(builder.LogCapture.Metrics);
        Assert.Equal(string.Empty, metric.Dimensions["Odd"]);
        Assert.Equal(string.Empty, metric.Dimensions["Missing"]);
        Assert.Equal("account", metric.Dimensions["PrimaryEntityName"]);
    }

    [Fact]
    public void A_scope_the_logger_cannot_open_is_traced_and_still_indents()
    {
        PluginContextBuilder builder = Builder();
        Mock<ILogger> logger = new();
        logger.Setup(l => l.BeginScope(It.IsAny<It.IsAnyType>())).Throws(new InvalidOperationException("no scope"));
        builder.WithLogger(logger.Object);
        LocalPluginContext ctx = builder.Build();

        IDisposable scope = ctx.BeginLogScope(new Dictionary<string, object?> { ["Batch"] = 1 });

        ctx.Trace("inside");
        scope.Dispose();
        ctx.Trace("root");

        // Each Log also fails to open its own dimension scope on this logger, which is traced after the line.
        Assert.Equal(
            new[]
            {
                "Application Insights scope failed: no scope",
                "  inside",
                "Application Insights logging failed: no scope",
                "root",
                "Application Insights logging failed: no scope",
            },
            builder.TraceCapture.Traces.ToArray());
    }

    [Fact]
    public void A_scope_the_logger_cannot_close_is_traced_and_still_dedents()
    {
        PluginContextBuilder builder = Builder();
        Mock<IDisposable> telemetryScope = new();
        telemetryScope.Setup(s => s.Dispose()).Throws(new InvalidOperationException("no close"));
        Mock<ILogger> logger = new();
        logger.Setup(l => l.BeginScope(It.IsAny<It.IsAnyType>())).Returns(telemetryScope.Object);
        builder.WithLogger(logger.Object);
        LocalPluginContext ctx = builder.Build();

        IDisposable scope = ctx.BeginLogScope(new Dictionary<string, object?>());
        scope.Dispose();
        scope.Dispose();

        Assert.Equal(new[] { "Application Insights scope failed: no close" }, builder.TraceCapture.Traces.ToArray());
        telemetryScope.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public void A_provider_without_an_execution_context_is_refused()
    {
        Mock<IServiceProvider> provider = new();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => new LocalPluginContext(provider.Object));

        Assert.Equal("IPluginExecutionContext is unavailable.", ex.Message);
    }
}
