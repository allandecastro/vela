using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Guards the logger/tracer against the two ways a plug-in can crash the pipeline through diagnostics:
//   1. Tracing an ALREADY-COMPOSED string that contains a literal '{' or '}' (e.g. a serialized snapshot).
//      Dataverse's ITracingService.Trace runs string.Format even with no args, so such a string throws
//      FormatException unless it is passed as the {0} argument.
//   2. Calling Log with fewer args than the message has placeholders, which makes string.Format throw.
// Both must be swallowed so a logging call never faults the plug-in.
public sealed class BraceSafeTracingTests
{
    // A tracer that mirrors real Dataverse: it ALWAYS runs string.Format(format, args), so an un-escaped
    // brace in the format string throws exactly as it would online (unlike the no-args fast path some fakes use).
    private sealed class DataverseFaithfulTracer : ITracingService
    {
        public List<string> Traces { get; } = new List<string>();

        public void Trace(string format, params object[] args)
        {
            if (string.IsNullOrEmpty(format))
            {
                return;
            }

            Traces.Add(string.Format(format, args ?? Array.Empty<object>()));
        }
    }

    [Fact]
    public void Tracing_a_composed_string_with_braces_does_not_throw_and_is_emitted()
    {
        DataverseFaithfulTracer tracer = new DataverseFaithfulTracer();
        LocalPluginContext ctx = new PluginContextBuilder().WithTracingService(tracer).Build();

        string snapshot = "{ \"name\": \"Acme\", \"tags\": { \"a\": 1 } }";

        ctx.Trace(snapshot);                            // Information -> unprefixed line
        ctx.Log(LogLevel.Error, null, snapshot);        // tagged line

        Assert.Equal(2, tracer.Traces.Count);
        Assert.Contains(snapshot, tracer.Traces[0]);
        Assert.Contains(snapshot, tracer.Traces[1]);
    }

    [Fact]
    public void Logging_an_exception_whose_text_contains_braces_does_not_throw()
    {
        DataverseFaithfulTracer tracer = new DataverseFaithfulTracer();
        LocalPluginContext ctx = new PluginContextBuilder().WithTracingService(tracer).Build();

        Exception ex = new InvalidOperationException("boom {0} {not-a-placeholder}");

        ctx.Log(LogLevel.Error, ex, "failed");

        // One line for the message, one for exception.ToString() - neither throws.
        Assert.Equal(2, tracer.Traces.Count);
        Assert.Contains("boom {0} {not-a-placeholder}", tracer.Traces[1]);
    }

    [Fact]
    public void Log_with_fewer_args_than_placeholders_falls_back_to_the_literal_message()
    {
        DataverseFaithfulTracer tracer = new DataverseFaithfulTracer();
        LocalPluginContext ctx = new PluginContextBuilder().WithTracingService(tracer).Build();

        // Two placeholders, one arg: string.Format would throw FormatException.
        ctx.Log(LogLevel.Information, null, "value={0} other={1}", 5);

        string line = Assert.Single(tracer.Traces);
        Assert.Equal("value={0} other={1}", line);
    }
}
