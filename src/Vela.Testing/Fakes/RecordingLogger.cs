using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

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
    /// An <see cref="ILogger"/> that records every entry (level, message, exception, and active scope dimensions)
    /// so tests can assert what a plugin logged through <c>ctx.LogInformation</c> / <c>ctx.LogError</c> / etc.
    /// Mirrors <see cref="RecordingTracingService"/> for the structured logging channel.
    /// </summary>
    public sealed class RecordingLogger : ILogger
    {
        private readonly List<object> _scopes = new List<object>();
        private readonly List<LogEntry> _entries = new List<LogEntry>();

        /// <summary>All recorded entries, in order.</summary>
        public IReadOnlyList<LogEntry> Entries => _entries;

        /// <summary>Removes all recorded entries.</summary>
        public void Clear() => _entries.Clear();

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            _scopes.Add(state);
            return new ScopePop(_scopes);
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var dimensions = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (object scope in _scopes)
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (KeyValuePair<string, object?> pair in pairs)
                    {
                        dimensions[pair.Key] = pair.Value;
                    }
                }
            }

            string message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            _entries.Add(new LogEntry(logLevel, message, exception, dimensions));
        }

        private sealed class ScopePop : IDisposable
        {
            private readonly List<object> _scopes;
            private bool _disposed;

            public ScopePop(List<object> scopes) => _scopes = scopes;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_scopes.Count > 0)
                {
                    _scopes.RemoveAt(_scopes.Count - 1);
                }
            }
        }
    }
}
