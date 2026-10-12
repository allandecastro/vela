using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// LocalPluginContext's logging surface as the plug-in trace log and the ILogger see it: level tags, scope
// indentation, the formatting fallback, the default telemetry dimensions, metrics, and the services it resolves (or
// replaces) from the service provider.
public sealed class LocalPluginContextLoggingTests
{
    private static readonly Guid Correlation = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Account = new("11111111-1111-1111-1111-111111111111");

    private static PluginContextBuilder Builder()
    {
        PluginContextBuilder builder = new();
        builder.OnMessage("Update");
        builder.Context.Stage = 40;
        builder.Context.Mode = 1;
        builder.Context.Depth = 3;
        builder.Context.CorrelationId = Correlation;
        builder.Context.PrimaryEntityName = "account";
        builder.Context.PrimaryEntityId = Account;
        return builder;
    }

    [Fact]
    public void Information_is_untagged_and_other_levels_carry_their_level()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.Trace("plain {0}", 1);
        ctx.Log(LogLevel.Warning, null, "careful");
        ctx.Log(LogLevel.Error, new InvalidOperationException("boom"), "failed {0}", "x");

        string[] traces = builder.TraceCapture.Traces.ToArray();
        Assert.Equal("plain 1", traces[0]);
        Assert.Equal("[Warning] careful", traces[1]);
        Assert.Equal("[Error] failed x", traces[2]);
        Assert.StartsWith("System.InvalidOperationException: boom", traces[3], StringComparison.Ordinal);
        Assert.Equal(4, traces.Length);
        Assert.Equal(new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Error }, builder.LogCapture.Entries.Select(e => e.Level).ToArray());
        Assert.Equal("failed x", builder.LogCapture.Entries[2].Message);
        Assert.Equal("boom", builder.LogCapture.Entries[2].Exception!.Message);
    }

    [Fact]
    public void A_blank_message_or_format_logs_nothing()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.Trace(" ");
        ctx.Log(LogLevel.Warning, null, "");

        Assert.Empty(builder.TraceCapture.Traces);
        Assert.Empty(builder.LogCapture.Entries);
    }

    [Fact]
    public void A_template_with_more_placeholders_than_arguments_is_logged_literally()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.Trace("{0} and {1}", "one");
        ctx.Trace("braces {0} stay", Array.Empty<object>());

        Assert.Equal(new[] { "{0} and {1}", "braces {0} stay" }, builder.TraceCapture.Traces.ToArray());
    }

    [Fact]
    public void Nested_scopes_indent_the_trace_log_and_disposing_twice_does_not_dedent_twice()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();
        Dictionary<string, object?> props = new() { ["Batch"] = 7 };

        ctx.Trace("root");
        IDisposable outer = ctx.BeginLogScope(props);
        ctx.Trace("one");
        IDisposable inner = ctx.BeginLogScope(new Dictionary<string, object?>());
        ctx.Log(LogLevel.Debug, null, "two");
        inner.Dispose();
        inner.Dispose();
        ctx.Trace("back to one");
        outer.Dispose();
        outer.Dispose();
        ctx.Trace("root again");

        Assert.Equal(new[] { "root", "  one", "    [Debug] two", "  back to one", "root again" }, builder.TraceCapture.Traces.ToArray());
        Assert.Equal(7, builder.LogCapture.Entries[1].Scope["Batch"]);
        Assert.False(builder.LogCapture.Entries[4].Scope.ContainsKey("Batch"));
    }

    [Fact]
    public void Disposing_a_scope_never_dedents_below_the_root()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        IDisposable first = ctx.BeginLogScope(new Dictionary<string, object?>());
        IDisposable second = ctx.BeginLogScope(new Dictionary<string, object?>());
        first.Dispose();
        second.Dispose();
        ctx.BeginLogScope(new Dictionary<string, object?>()).Dispose();
        ctx.Trace("root");

        Assert.Equal(new[] { "root" }, builder.TraceCapture.Traces.ToArray());
    }

    [Fact]
    public void Every_log_carries_the_execution_dimensions()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = builder.Build();

        ctx.Trace("x");

        IReadOnlyDictionary<string, object?> scope = Assert.Single(builder.LogCapture.Entries).Scope;
        Assert.Equal(Correlation, scope["CorrelationId"]);
        Assert.Equal("Update", scope["MessageName"]);
        Assert.Equal(40, scope["Stage"]);
        Assert.Equal(1, scope["Mode"]);
        Assert.Equal(3, scope["Depth"]);
        Assert.Equal("account", scope["PrimaryEntityName"]);
        Assert.Equal(Account, scope["PrimaryEntityId"]);
        Assert.False(scope.ContainsKey("PluginType"));
        Assert.False(scope.ContainsKey("Method"));
    }

    [Fact]
    public void The_plugin_type_and_current_handler_become_dimensions_once_known()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = new(builder.BuildServiceProvider(), "Zava.Plugins.AccountPlugin");

        ctx.Trace("before");
        ctx.SetCurrentHandler("OnUpdate");
        ctx.Trace("during");
        ctx.SetCurrentHandler("");
        ctx.Trace("blank handler");

        IReadOnlyList<LogEntry> entries = builder.LogCapture.Entries;
        Assert.Equal("Zava.Plugins.AccountPlugin", entries[0].Scope["PluginType"]);
        Assert.False(entries[0].Scope.ContainsKey("Method"));
        Assert.Equal("OnUpdate", entries[1].Scope["Method"]);
        Assert.False(entries[2].Scope.ContainsKey("Method"));
    }

    [Fact]
    public void An_empty_plugin_type_name_adds_no_dimension()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = new(builder.BuildServiceProvider(), "");

        ctx.Trace("x");

        Assert.False(Assert.Single(builder.LogCapture.Entries).Scope.ContainsKey("PluginType"));
    }

    [Fact]
    public void A_metric_reaches_the_trace_log_and_the_platform_metric_with_its_dimensions()
    {
        PluginContextBuilder builder = Builder();
        LocalPluginContext ctx = new(builder.BuildServiceProvider(), "Zava.Plugins.AccountPlugin");

        // Under a comma-decimal culture, so the "1.5" below proves the dimension is formatted invariantly.
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));
            ctx.LogMetric("Latency", 2, new Dictionary<string, object?> { ["Region"] = "eu", ["Stage"] = 99, [""] = "ignored", ["Ratio"] = 1.5 });
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }

        Assert.Equal("[Metric] Latency=2", Assert.Single(builder.TraceCapture.Traces));
        Assert.Empty(builder.LogCapture.Entries);
        MetricEntry metric = Assert.Single(builder.LogCapture.Metrics);
        Assert.Equal(("Latency", 2L), (metric.Name, metric.Value));
        Assert.Equal(new Dictionary<string, string>
        {
            ["CorrelationId"] = Correlation.ToString(),
            ["MessageName"] = "Update",
            ["Stage"] = "99", // the caller's property wins over the default dimension
            ["Mode"] = "1",
            ["Depth"] = "3",
            ["PrimaryEntityName"] = "account",
            ["PrimaryEntityId"] = Account.ToString(),
            ["PluginType"] = "Zava.Plugins.AccountPlugin",
            ["Region"] = "eu",
            ["Ratio"] = "1.5", // invariant culture, whatever the thread culture
        }, metric.Dimensions);
    }

    [Fact]
    public void Invalid_calls_explain_what_is_missing()
    {
        LocalPluginContext ctx = Builder().Build();

        Assert.StartsWith("A metric name is required.", Assert.Throws<ArgumentException>(() => ctx.LogMetric(" ", 1)).Message, StringComparison.Ordinal);
        Assert.StartsWith("User id cannot be empty.", Assert.Throws<ArgumentException>(() => ctx.AsUser(Guid.Empty)).Message, StringComparison.Ordinal);
        Assert.Equal("IManagedIdentityService is unavailable.",
            Assert.Throws<InvalidPluginExecutionException>(() => ctx.AcquireManagedIdentityToken(new[] { "s" })).Message);
        Assert.Equal("IManagedIdentityService is unavailable.",
            Assert.Throws<InvalidPluginExecutionException>(() => ctx.AcquireManagedIdentityToken(Guid.NewGuid(), new[] { "s" })).Message);
    }

    [Fact]
    public void A_provider_without_an_organization_service_factory_is_refused()
    {
        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(new FakePluginExecutionContext());

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => new LocalPluginContext(provider.Object));

        Assert.Equal("IOrganizationServiceFactory is unavailable.", ex.Message);
    }

    [Fact]
    public void Missing_tracing_and_notification_services_are_replaced_by_null_objects()
    {
        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(new FakePluginExecutionContext());
        provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(new Mock<IOrganizationServiceFactory>().Object);

        LocalPluginContext ctx = new(provider.Object);

        Assert.IsType<NullTracingService>(ctx.TracingService);
        Assert.IsType<NullNotificationService>(ctx.NotificationService);
        Assert.Same(ctx.NotificationService, ctx.NotificationService);
        ctx.Trace("ignored");
    }

    [Fact]
    public void A_supplied_notification_service_is_the_one_exposed()
    {
        IServiceEndpointNotificationService notifications = new Mock<IServiceEndpointNotificationService>().Object;
        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(new FakePluginExecutionContext());
        provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(new Mock<IOrganizationServiceFactory>().Object);
        provider.Setup(p => p.GetService(typeof(IServiceEndpointNotificationService))).Returns(notifications);

        Assert.Same(notifications, new LocalPluginContext(provider.Object).NotificationService);
    }

    [Fact]
    public void The_user_and_system_services_trace_their_scope()
    {
        PluginContextBuilder builder = Builder();
        builder.WithOrganizationService(new InMemoryOrganizationService());
        builder.WithSystemService(new InMemoryOrganizationService());
        LocalPluginContext ctx = builder.Build();

        ctx.Service.Create(new Entity("account"));
        ctx.SystemService.Create(new Entity("account"));
        ctx.AsUser(Guid.NewGuid()).Create(new Entity("account"));

        string[] starts = builder.TraceCapture.Traces.Where(t => t.Contains(" start ")).ToArray();
        Assert.Equal(3, starts.Length);
        Assert.StartsWith("Dataverse.Create start scope=User depth=3", starts[0], StringComparison.Ordinal);
        Assert.StartsWith("Dataverse.Create start scope=System depth=3", starts[1], StringComparison.Ordinal);
        Assert.StartsWith("Dataverse.Create start scope=AsUser depth=3", starts[2], StringComparison.Ordinal);
        Assert.Same(ctx.Service, ctx.Service);
        Assert.Same(ctx.SystemService, ctx.SystemService);
    }
}
