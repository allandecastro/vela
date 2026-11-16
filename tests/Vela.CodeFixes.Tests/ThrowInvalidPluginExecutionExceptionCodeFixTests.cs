using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.PluginExceptionTypeAnalyzer,
    Vela.CodeFixes.ThrowInvalidPluginExecutionExceptionCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class ThrowInvalidPluginExecutionExceptionCodeFixTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk
{
    public interface IPlugin { }
    public class InvalidPluginExecutionException : System.Exception
    {
        public InvalidPluginExecutionException() { }
        public InvalidPluginExecutionException(string message) : base(message) { }
        public InvalidPluginExecutionException(string message, System.Exception inner) : base(message, inner) { }
    }
}
namespace Demo
{
    using System;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin { public void Execute(IServiceProvider sp) { BODY } }
    public class WrapException : Exception { public WrapException(Exception inner) : base(""wrapped"", inner) { } }
    public class DomainException : Exception { public DomainException(string message, Exception innerException) : base(message, innerException) { } }
    public class StateException : Exception { public Exception Cause = null; public StateException(Exception inner) : base(""state"", inner) { } }
}";

    [Fact]
    public async Task Replaces_the_thrown_exception_type()
    {
        string before = Harness.Replace("BODY", "throw new {|#0:InvalidOperationException|}(\"bad\");");
        string after = Harness.Replace("BODY", "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad\");");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("InvalidOperationException");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Drops_arguments_that_have_no_matching_ctor()
    {
        // ArgumentException(message, paramName) has no InvalidPluginExecutionException(string, string) ctor, so the
        // fix keeps only the message and still compiles.
        string before = Harness.Replace("BODY", "throw new {|#0:ArgumentException|}(\"bad\", \"param\");");
        string after = Harness.Replace("BODY", "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad\");");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("ArgumentException");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Preserves_message_and_inner_exception()
    {
        string before = Harness.Replace("BODY", "var e = new System.Exception(\"inner\"); throw new {|#0:InvalidOperationException|}(\"bad\", e);");
        string after = Harness.Replace("BODY", "var e = new System.Exception(\"inner\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad\", e);");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("InvalidOperationException");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    private static async Task VerifyFix(string thrown, string body, string fixedBody)
    {
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments(thrown);
        await Verify.VerifyCodeFixAsync(Harness.Replace("BODY", body), expected, Harness.Replace("BODY", fixedBody));
    }

    [Fact]
    public async Task A_paramName_is_not_taken_as_the_message()
        // ArgumentNullException(string paramName): nameof(x) names the parameter, it is no message.
        => await VerifyFix("ArgumentNullException",
            "string x = null; throw new {|#0:ArgumentNullException|}(nameof(x));",
            "string x = null; throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException();");

    [Fact]
    public async Task The_message_is_bound_to_its_parameter_not_to_the_first_string()
        // ArgumentOutOfRangeException(string paramName, string message): the second string is the message.
        => await VerifyFix("ArgumentOutOfRangeException",
            "throw new {|#0:ArgumentOutOfRangeException|}(\"count\", \"Count must be positive.\");",
            "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"Count must be positive.\");");

    [Fact]
    public async Task Named_arguments_are_bound_by_name_and_emitted_positionally()
        => await VerifyFix("ArgumentException",
            "var e = new Exception(\"inner\"); throw new {|#0:ArgumentException|}(paramName: \"p\", innerException: e, message: \"bad\");",
            "var e = new Exception(\"inner\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad\", e);");

    [Fact]
    public async Task An_inner_exception_alone_brings_its_message()
        => await VerifyFix("WrapException",
            "var e = new Exception(\"inner\"); throw new {|#0:WrapException|}(e);",
            "var e = new Exception(\"inner\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(e.Message, e);");

    [Fact]
    public async Task An_inner_exception_read_from_a_member_brings_its_message()
        => await VerifyFix("StateException",
            "var s = new StateException(null); throw new {|#0:StateException|}(s.Cause);",
            "var s = new StateException(null); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(s.Cause.Message, s.Cause);");

    [Fact]
    public async Task A_custom_exception_keeps_its_message_and_inner_exception()
        => await VerifyFix("DomainException",
            "var e = new Exception(\"inner\"); throw new {|#0:DomainException|}(\"bad\", e);",
            "var e = new Exception(\"inner\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad\", e);");

    [Fact]
    public async Task No_fix_when_an_inner_exception_alone_would_be_evaluated_twice()
    {
        // (inner.Message, inner) over `new Exception(...)` would build two exceptions: no fix is offered.
        string source = Harness.Replace("BODY", "throw new {|#0:WrapException|}(new Exception(\"inner\"));");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("WrapException");
        await Verify.VerifyCodeFixAsync(source, expected, source);
    }
}
