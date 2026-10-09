using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace Vela.Context
{
    // Stands in for the platform's Application Insights logger when the service provider has none (unit tests that
    // register no logger, or hosts other than the Dataverse sandbox). Every member is a no-op; Execute/ExecuteAsync
    // still run the operation they wrap.
    internal sealed class NullPluginTelemetryLogger : ILogger
    {
        public static readonly NullPluginTelemetryLogger Instance = new NullPluginTelemetryLogger();

        private NullPluginTelemetryLogger()
        {
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public IDisposable BeginScope(string messageFormat, params object[] args) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
        public void Log(LogLevel logLevel, EventId eventId, Exception exception, string message, params object[] args) { }
        public void Log(LogLevel logLevel, EventId eventId, string message, params object[] args) { }
        public void Log(LogLevel logLevel, Exception exception, string message, params object[] args) { }
        public void Log(LogLevel logLevel, string message, params object[] args) { }
        public void LogCritical(EventId eventId, Exception exception, string message, params object[] args) { }
        public void LogCritical(EventId eventId, string message, params object[] args) { }
        public void LogCritical(Exception exception, string message, params object[] args) { }
        public void LogCritical(string message, params object[] args) { }
        public void LogDebug(EventId eventId, Exception exception, string message, params object[] args) { }
        public void LogDebug(EventId eventId, string message, params object[] args) { }
        public void LogDebug(Exception exception, string message, params object[] args) { }
        public void LogDebug(string message, params object[] args) { }
        public void LogError(EventId eventId, Exception exception, string message, params object[] args) { }
        public void LogError(EventId eventId, string message, params object[] args) { }
        public void LogError(Exception exception, string message, params object[] args) { }
        public void LogError(string message, params object[] args) { }
        public void LogInformation(EventId eventId, Exception exception, string message, params object[] args) { }
        public void LogInformation(EventId eventId, string message, params object[] args) { }
        public void LogInformation(Exception exception, string message, params object[] args) { }
        public void LogInformation(string message, params object[] args) { }
        public void LogTrace(EventId eventId, Exception exception, string message, params object[] args) { }
        public void LogTrace(EventId eventId, string message, params object[] args) { }
        public void LogTrace(Exception exception, string message, params object[] args) { }
        public void LogTrace(string message, params object[] args) { }
        public void LogWarning(EventId eventId, Exception exception, string message, params object[] args) { }
        public void LogWarning(EventId eventId, string message, params object[] args) { }
        public void LogWarning(Exception exception, string message, params object[] args) { }
        public void LogWarning(string message, params object[] args) { }
        public void LogMetric(string metricName, long value) { }
        public void LogMetric(string metricName, IDictionary<string, string> metricDimensions, long value) { }
        public void AddCustomProperty(string propertyName, string propertyValue) { }
        public void Execute(string activityName, Action action, IEnumerable<KeyValuePair<string, string>> additionalCustomProperties) => action?.Invoke();
        public Task ExecuteAsync(string activityName, Func<Task> action, IEnumerable<KeyValuePair<string, string>> additionalCustomProperties) => action?.Invoke() ?? Task.CompletedTask;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();

            public void Dispose()
            {
            }
        }
    }
}
