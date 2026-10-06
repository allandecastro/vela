# Telemetry & Application Insights

Emit leveled, structured telemetry from a plugin handler. When the environment has
[Application Insights configured](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/application-insights-integration-overview),
entries flow there at the right severity with custom dimensions; when it is not, nothing is lost - every entry
still goes to the plug-in trace log (subject to the environment's trace-log setting). You never instantiate a
`TelemetryClient`; the platform owns ingestion through the logger it provides.

```csharp
ctx.LogInformation("Processing order {0}", orderId);
ctx.LogWarning("Credit limit near threshold");
ctx.LogError(ex, "Order sync failed");
```

Levels: `LogTrace`, `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`. `LogError` and
`LogCritical` have an overload that takes an `Exception` first.

## Custom dimensions (scopes)

Vela attaches a default set of dimensions to every entry, from the execution context:
`CorrelationId`, `MessageName`, `Stage`, `Mode`, `Depth`, `PrimaryEntityName`, `PrimaryEntityId`, `PluginType`
(the plugin/Custom API class), and `Method` (the handler method currently running). Query and correlate on these
in Application Insights.

Add your own with a scope - they apply to every log written until the handle is disposed, and nest:

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
```

Sends an Application Insights custom metric when the environment supports it, and always emits a structured log
line so the value is visible via the logger / trace log too.

## Trace vs Log

`ctx.Trace(...)` still works and is equivalent to `ctx.LogInformation(...)`: it writes to the plug-in trace log and,
at Information level, to Application Insights. Use the leveled helpers when you want a severity other than
Information, an attached exception, or metrics. Execution snapshots (the automatic ones and
`ctx.TraceExecutionSnapshot(...)`) are the exception: they go to the plug-in trace log only, never to Application
Insights (see [Write a plugin](plugins.md)).

## Whether Application Insights is set up or not

The integration is safe either way and never throws for telemetry reasons:

- **Configured** - logs flow to Application Insights at their level, with the default + scope dimensions; metrics
  become custom metrics.
- **Not configured** - the Application Insights channel is a no-op; entries still land in the plug-in trace log
  (when the environment's trace-log setting is on), so debugging via the Plugin Trace Viewer keeps working.

## Testing

The context logs through an `ILogger`, so tests can assert what a handler emitted with the recording logger:

```csharp
var builder = PluginContext.For<Account>().OnCreate();
ILocalPluginContext ctx = builder.Build();

ctx.LogWarning("careful");

LogEntry entry = Assert.Single(builder.LogCapture.Entries);
Assert.Equal(LogLevel.Warning, entry.Level);
Assert.Equal("careful", entry.Message);
```

`builder.LogCapture` records level, message, exception, and scope dimensions; `builder.TraceCapture` records the
trace-log channel. Override the logger with `WithLogger(...)`.

## See also

- [Caching](caching.md), [Environment variables](environment-variables.md), [Managed identity](managed-identity.md) - the other built-in context services.
- [Write a plugin](plugins.md) - the context helpers available in a handler.
