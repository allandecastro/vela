using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// An <see cref="ITracingService"/> that records every trace message so tests can assert on what a
    /// plugin emitted. Formatting follows the SDK contract (<c>string.Format</c> when args are supplied).
    /// </summary>
    public sealed class RecordingTracingService : ITracingService
    {
        private readonly List<string> _traces = new List<string>();

        /// <summary>All recorded trace lines, in order.</summary>
        public IReadOnlyList<string> Traces => _traces;

        /// <summary>All recorded trace lines joined by newlines.</summary>
        public string Output => string.Join(Environment.NewLine, _traces);

        /// <summary>Removes all recorded traces.</summary>
        public void Clear()
        {
            _traces.Clear();
        }

        public void Trace(string format, params object[] args)
        {
            if (string.IsNullOrEmpty(format))
            {
                return;
            }

            _traces.Add(args == null || args.Length == 0 ? format : string.Format(format, args));
        }
    }
}
