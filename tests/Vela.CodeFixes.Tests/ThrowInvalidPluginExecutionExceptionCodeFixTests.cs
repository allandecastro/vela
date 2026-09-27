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
}
