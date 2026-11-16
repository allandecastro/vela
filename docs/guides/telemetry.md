# Telemetry and Application Insights

Emit leveled, structured telemetry from a plugin handler. This page covers the log levels, custom dimensions,
metrics, and how to assert on telemetry in tests.

When the environment has
[Application Insights configured](https://learn.microsoft.com/power-platform/admin/overview-integration-application-insights),
entries go there at their severity, with custom dimensions. When it does not, nothing is lost: every entry still goes
to the plug-in trace log (subject to the environment's trace-log setting). You never instantiate a
`TelemetryClient`; the platform owns ingestion through the logger it provides,
[`Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`](https://learn.microsoft.com/power-apps/developer/data-platform/application-insights-ilogger).
Vela calls it for you and exposes it as `ctx.Logger` (and as the injected `ILogger`) when you need its other
members. Don't reference `Microsoft.Extensions.Logging`: the platform never serves its `ILogger`.

```csharp
ctx.LogInformation("Processing order {0}", orderId);
ctx.LogWarning("Credit limit near threshold");
ctx.LogError(ex, "Order sync failed");
```

Levels: `LogTrace`, `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`. `LogError` and
`LogCritical` have an overload that takes an `Exception` first. `ctx.Log(level, exception, message, args)` takes a
`Microsoft.Xrm.Sdk.PluginTelemetry.LogLevel`.

The message is formatted with `string.Format` first (`{0}`, `{1}`, ...) and sent to Application Insights as plain
text, so braces in the text (JSON, for example) are safe. Structured values belong in dimensions (below), not in
the message.

## Custom dimensions (scopes)

Vela attaches a default set of dimensions to every entry, from the execution context:
`CorrelationId`, `MessageName`, `Stage`, `Mode`, `Depth`, `PrimaryEntityName`, `PrimaryEntityId`, `PluginType`
(the plugin/Custom API class), and `Method` (the handler method currently running; present when Vela dispatches to
a handler method, absent for a typed Custom API's `Execute(ctx, request)` and for an override of
`ExecuteDataversePlugin`). Query and correlate on these in Application Insights.

Add your own with a scope. Its dimensions apply to every entry written until the handle is disposed, and scopes nest:

```csharp
using (ctx.BeginLogScope(("OrderId", orderId), ("Region", "EU")))
{
    ctx.LogInformation("Processing order");   // carries OrderId, Region + the defaults
    ctx.LogError(ex, "Order failed");         // same dimensions
}
```

A scope also **indents** the plug-in trace log lines written inside it (nested scopes indent further), so the trace
log reads as a tree. Application Insights entries carry the scope as dimensions rather than indentation.

## Metrics

```csharp
ctx.LogMetric("OrdersProcessed", 1);
ctx.LogMetric("LinesImported", lines.Count, new Dictionary<string, object?> { ["Source"] = "ERP" });
```

Writes a `[Metric] name=value` line to the plug-in trace log and passes the metric to the platform logger's
`LogMetric`. The value is a `long`: the platform's metric API only takes integers. The metric carries the default
dimensions plus the properties you pass, converted to invariant strings. Scope properties from `BeginLogScope` are
not copied onto metrics; pass them explicitly.

## Trace vs Log

`ctx.Trace(...)` still works and is equivalent to `ctx.LogInformation(...)`: it writes to the plug-in trace log and,
at Information level, to Application Insights. Use the leveled helpers when you want a severity other than
Information, an attached exception, or metrics. Execution snapshots (the automatic ones and
`ctx.TraceExecutionSnapshot(...)`) are the exception: they go to the plug-in trace log only, never to Application
Insights, and the automatic ones list only metadata (message, stage, ids, column names) unless the plugin overrides
`AutoSnapshotDetail` (see [Write a plugin](plugins.md)).

## Whether Application Insights is set up or not

The integration is safe either way and never throws for telemetry reasons:

- **Configured:** Vela hands logs and metrics to the platform logger. What reaches Application Insights from
  there is up to the platform; see [Microsoft's documentation](https://learn.microsoft.com/power-apps/developer/data-platform/application-insights-ilogger).
- **Not configured:** the Application Insights channel is a no-op; entries still land in the plug-in trace log
  (when the environment's trace-log setting is on), so debugging via the Plugin Trace Viewer keeps working.

## Testing

The test harness serves a recording `Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`, the same type the platform serves,
so a test can assert what a handler emitted. In a [`PluginPipeline`](testing.md) test, `pipeline.LogCapture` records
the entries of the whole cascade. With `PluginContext`, read `builder.LogCapture`:

[!code-csharp[](../samples/Vela.DocSamples/TestingSample.cs#telemetry)]

`LogCapture` (a `RecordingLogger`, namespace `Vela.Testing`) records the level, message, exception and scope
dimensions of each entry in `Entries`, and the name, value and dimensions of each metric in `Metrics`.
`TraceCapture` (or `pipeline.Trace`) records the trace-log channel. To use another logger (any
`PluginTelemetry.ILogger`, a mock included), pass it to `PluginContext` with `WithLogger(...)`.

## See also

- [Caching](caching.md), [Environment variables](environment-variables.md), [Managed identity](managed-identity.md): the other built-in context services.
- [Write a plugin](plugins.md): the context helpers available in a handler.
