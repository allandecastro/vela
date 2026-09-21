using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Vela.Context
{
    /// <summary>
    /// Leveled logging helpers over <see cref="ILocalPluginContext.Log"/>. Each writes to the plug-in trace log
    /// and, when the environment has Application Insights configured, to Application Insights at the given severity
    /// with the framework's default dimensions plus any active <see cref="BeginLogScope(ILocalPluginContext, ValueTuple{string, object}[])"/>
    /// properties. Telemetry never throws, and entries are never lost when Application Insights is not set up (the
    /// trace log is always written, subject to the environment's trace-log setting).
    /// </summary>
    public static class TelemetryExtensions
    {
        /// <summary>Logs at Trace level.</summary>
        public static void LogTrace(this ILocalPluginContext context, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Trace, null, message, args);

        /// <summary>Logs at Debug level.</summary>
        public static void LogDebug(this ILocalPluginContext context, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Debug, null, message, args);

        /// <summary>Logs at Information level.</summary>
        public static void LogInformation(this ILocalPluginContext context, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Information, null, message, args);

        /// <summary>Logs at Warning level.</summary>
        public static void LogWarning(this ILocalPluginContext context, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Warning, null, message, args);

        /// <summary>Logs at Error level.</summary>
        public static void LogError(this ILocalPluginContext context, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Error, null, message, args);

        /// <summary>Logs an exception at Error level.</summary>
        public static void LogError(this ILocalPluginContext context, Exception exception, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Error, exception, message, args);

        /// <summary>Logs at Critical level.</summary>
        public static void LogCritical(this ILocalPluginContext context, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Critical, null, message, args);

        /// <summary>Logs an exception at Critical level.</summary>
        public static void LogCritical(this ILocalPluginContext context, Exception exception, string message, params object[] args)
            => Ensure(context).Log(LogLevel.Critical, exception, message, args);

        /// <summary>
        /// Begins a logging scope from inline key/value pairs: the properties become Application Insights custom
        /// dimensions on every log written until the returned handle is disposed. Nestable.
        /// </summary>
        /// <example><code>using (ctx.BeginLogScope(("OrderId", id), ("Region", "EU"))) { ctx.LogInformation("..."); }</code></example>
        public static IDisposable BeginLogScope(this ILocalPluginContext context, params (string Key, object? Value)[] properties)
        {
            Ensure(context);
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (properties != null)
            {
                foreach ((string key, object? value) in properties)
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        map[key] = value;
                    }
                }
            }

            return context.BeginLogScope(map);
        }

        private static ILocalPluginContext Ensure(ILocalPluginContext context) =>
            context ?? throw new ArgumentNullException(nameof(context));
    }
}
