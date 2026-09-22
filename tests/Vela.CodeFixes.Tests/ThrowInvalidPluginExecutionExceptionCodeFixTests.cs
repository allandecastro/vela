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
        public InvalidPluginExecutionException(string message) : base(message) { }
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
        string after = Harness.Replace("BODY", "throw new Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad\");");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("InvalidOperationException");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }
}
