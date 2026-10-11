using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace Vela.Testing
{
    /// <summary>
    /// A single captured log entry: severity, formatted message, optional exception, and the merged scope
    /// dimensions active at the time (the framework defaults plus any <c>BeginLogScope</c> properties).
    /// </summary>
    public sealed class LogEntry
    {
        internal LogEntry(LogLevel level, string message, Exception? exception, IReadOnlyDictionary<string, object?> scope)
        {
            Level = level;
            Message = message;
            Exception = exception;
            Scope = scope;
        }

        /// <summary>The log severity.</summary>
        public LogLevel Level { get; }

        /// <summary>The formatted message.</summary>
        public string Message { get; }

        /// <summary>The exception attached to the entry, if any.</summary>
        public Exception? Exception { get; }

        /// <summary>The custom dimensions (default + scope properties) attached to the entry.</summary>
        public IReadOnlyDictionary<string, object?> Scope { get; }
    }

    /// <summary>
    /// A single captured custom metric: name, value, and the string dimensions passed with it.
    /// </summary>
    public sealed class MetricEntry
    {
        internal MetricEntry(string name, long value, IReadOnlyDictionary<string, string> dimensions)
        {
            Name = name;
            Value = value;
            Dimensions = dimensions;
        }

        /// <summary>The metric name.</summary>
        public string Name { get; }

        /// <summary>The metric value.</summary>
        public long Value { get; }

        /// <summary>The custom dimensions sent with the metric (default + caller properties).</summary>
        public IReadOnlyDictionary<string, string> Dimensions { get; }
    }

    /// <summary>
    /// A <see cref="ILogger">Microsoft.Xrm.Sdk.PluginTelemetry.ILogger</see> (the Application Insights logger the
    /// Dataverse platform serves to plug-ins) that records every entry (level, message, exception, and active scope
    /// dimensions) and every metric, so tests can assert what a plugin logged through <c>ctx.LogInformation</c> /
    /// <c>ctx.LogError</c> / <c>ctx.LogMetric</c> / etc. Mirrors <see cref="RecordingTracingService"/> for the
    /// Application Insights channel.
    /// </summary>
    public sealed class RecordingLogger : ILogger
    {
        private readonly List<ScopePop> _scopes = new List<ScopePop>();
        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private readonly List<MetricEntry> _metrics = new List<MetricEntry>();
        private readonly Dictionary<string, string> _customProperties = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>All recorded log entries, in order.</summary>
        public IReadOnlyList<LogEntry> Entries => _entries;

        /// <summary>All recorded metrics, in order.</summary>
        public IReadOnlyList<MetricEntry> Metrics => _metrics;

        /// <summary>Properties set through <see cref="AddCustomProperty"/>.</summary>
        public IReadOnlyDictionary<string, string> CustomProperties => _customProperties;

        /// <summary>Removes all recorded entries, metrics and custom properties.</summary>
        public void Clear()
        {
            _entries.Clear();
            _metrics.Clear();
            _customProperties.Clear();
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
        {
            var scope = new ScopePop(_scopes, state);
            _scopes.Add(scope);
            return scope;
        }

        /// <inheritdoc />
        public IDisposable BeginScope(string messageFormat, params object[] args) => BeginScope<object?>(null);

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Record(logLevel, exception, formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty);

        /// <inheritdoc />
        public void Log(LogLevel logLevel, EventId eventId, Exception exception, string message, params object[] args) => Record(logLevel, exception, Format(message, args));

        /// <inheritdoc />
        public void Log(LogLevel logLevel, EventId eventId, string message, params object[] args) => Record(logLevel, null, Format(message, args));

        /// <inheritdoc />
        public void Log(LogLevel logLevel, Exception exception, string message, params object[] args) => Record(logLevel, exception, Format(message, args));

        /// <inheritdoc />
        public void Log(LogLevel logLevel, string message, params object[] args) => Record(logLevel, null, Format(message, args));

        /// <inheritdoc />
        public void LogTrace(EventId eventId, Exception exception, string message, params object[] args) => Log(LogLevel.Trace, exception, message, args);

        /// <inheritdoc />
        public void LogTrace(EventId eventId, string message, params object[] args) => Log(LogLevel.Trace, message, args);

        /// <inheritdoc />
        public void LogTrace(Exception exception, string message, params object[] args) => Log(LogLevel.Trace, exception, message, args);

        /// <inheritdoc />
        public void LogTrace(string message, params object[] args) => Log(LogLevel.Trace, message, args);

        /// <inheritdoc />
        public void LogDebug(EventId eventId, Exception exception, string message, params object[] args) => Log(LogLevel.Debug, exception, message, args);

        /// <inheritdoc />
        public void LogDebug(EventId eventId, string message, params object[] args) => Log(LogLevel.Debug, message, args);

        /// <inheritdoc />
        public void LogDebug(Exception exception, string message, params object[] args) => Log(LogLevel.Debug, exception, message, args);

        /// <inheritdoc />
        public void LogDebug(string message, params object[] args) => Log(LogLevel.Debug, message, args);

        /// <inheritdoc />
        public void LogInformation(EventId eventId, Exception exception, string message, params object[] args) => Log(LogLevel.Information, exception, message, args);

        /// <inheritdoc />
        public void LogInformation(EventId eventId, string message, params object[] args) => Log(LogLevel.Information, message, args);

        /// <inheritdoc />
        public void LogInformation(Exception exception, string message, params object[] args) => Log(LogLevel.Information, exception, message, args);

        /// <inheritdoc />
        public void LogInformation(string message, params object[] args) => Log(LogLevel.Information, message, args);

        /// <inheritdoc />
        public void LogWarning(EventId eventId, Exception exception, string message, params object[] args) => Log(LogLevel.Warning, exception, message, args);

        /// <inheritdoc />
        public void LogWarning(EventId eventId, string message, params object[] args) => Log(LogLevel.Warning, message, args);

        /// <inheritdoc />
        public void LogWarning(Exception exception, string message, params object[] args) => Log(LogLevel.Warning, exception, message, args);

        /// <inheritdoc />
        public void LogWarning(string message, params object[] args) => Log(LogLevel.Warning, message, args);

        /// <inheritdoc />
        public void LogError(EventId eventId, Exception exception, string message, params object[] args) => Log(LogLevel.Error, exception, message, args);

        /// <inheritdoc />
        public void LogError(EventId eventId, string message, params object[] args) => Log(LogLevel.Error, message, args);

        /// <inheritdoc />
        public void LogError(Exception exception, string message, params object[] args) => Log(LogLevel.Error, exception, message, args);

        /// <inheritdoc />
        public void LogError(string message, params object[] args) => Log(LogLevel.Error, message, args);

        /// <inheritdoc />
        public void LogCritical(EventId eventId, Exception exception, string message, params object[] args) => Log(LogLevel.Critical, exception, message, args);

        /// <inheritdoc />
        public void LogCritical(EventId eventId, string message, params object[] args) => Log(LogLevel.Critical, message, args);

        /// <inheritdoc />
        public void LogCritical(Exception exception, string message, params object[] args) => Log(LogLevel.Critical, exception, message, args);

        /// <inheritdoc />
        public void LogCritical(string message, params object[] args) => Log(LogLevel.Critical, message, args);

        /// <inheritdoc />
        public void LogMetric(string metricName, long value) => LogMetric(metricName, new Dictionary<string, string>(), value);

        /// <inheritdoc />
        public void LogMetric(string metricName, IDictionary<string, string> metricDimensions, long value)
        {
            var dimensions = metricDimensions == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metricDimensions, StringComparer.Ordinal);
            _metrics.Add(new MetricEntry(metricName, value, dimensions));
        }

        /// <inheritdoc />
        public void AddCustomProperty(string propertyName, string propertyValue) => _customProperties[propertyName] = propertyValue;

        /// <inheritdoc />
        public void Execute(string activityName, Action action, IEnumerable<KeyValuePair<string, string>> additionalCustomProperties) => action?.Invoke();

        /// <inheritdoc />
        public Task ExecuteAsync(string activityName, Func<Task> action, IEnumerable<KeyValuePair<string, string>> additionalCustomProperties)
            => action?.Invoke() ?? Task.FromResult(0);

        private void Record(LogLevel level, Exception? exception, string message)
        {
            var dimensions = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (ScopePop scope in _scopes)
            {
                if (scope.State is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (KeyValuePair<string, object?> pair in pairs)
                    {
                        dimensions[pair.Key] = pair.Value;
                    }
                }
            }

            _entries.Add(new LogEntry(level, message, exception, dimensions));
        }

        // Positional ("{0}") templates are formatted like the platform; anything string.Format cannot parse (named
        // message-template holes) is recorded verbatim.
        private static string Format(string message, object[] args)
        {
            if (message == null)
            {
                return string.Empty;
            }

            if (args == null || args.Length == 0)
            {
                return message;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, message, args);
            }
            catch (FormatException)
            {
                return message;
            }
        }

        // Disposing a scope removes that scope, not the innermost one, so scopes disposed out of order keep the
        // others' dimensions.
        private sealed class ScopePop : IDisposable
        {
            private readonly List<ScopePop> _scopes;

            public ScopePop(List<ScopePop> scopes, object? state)
            {
                _scopes = scopes;
                State = state;
            }

            public object? State { get; }

            public void Dispose() => _scopes.Remove(this);
        }
    }
}
