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
    public class MyPlugin : IPlugin { public Exception Last; public void Execute(IServiceProvider sp) { BODY } }
    public class WrapException : Exception { public WrapException(Exception inner) : base(""wrapped"", inner) { } }
    public class DomainException : Exception { public DomainException(string message, Exception innerException) : base(message, innerException) { } }
    public class StateException : Exception { public Exception Cause = null; public StateException(Exception inner) : base(""state"", inner) { } }
    public class PairException : Exception { public PairException(Exception first, Exception second) : base(""pair"", first) { } }
    public class FormatException : Exception { public FormatException(string message, params object[] args) : base(string.Format(message, args)) { } }
    public class ForeignException : Exception { public ForeignException(Demo.Other.Exception inner) : base(""foreign"", inner) { } }
}
namespace Demo.Other
{
    public class Exception : System.Exception { }
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

    [Fact]
    public async Task An_object_initializer_is_dropped_with_the_old_type()
        // No argument list at all: the replacement gets an empty one, and the initializer (whose members need not
        // exist on InvalidPluginExecutionException) goes.
        => await VerifyFix("InvalidOperationException",
            "throw new {|#0:InvalidOperationException|} { Source = \"x\" };",
            "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException();");

    [Fact]
    public async Task Only_the_first_inner_exception_is_kept()
        => await VerifyFix("PairException",
            "var a = new Exception(\"a\"); var b = new Exception(\"b\"); throw new {|#0:PairException|}(a, b);",
            "var a = new Exception(\"a\"); var b = new Exception(\"b\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(a.Message, a);");

    [Fact]
    public async Task Params_arguments_are_dropped_and_the_message_kept()
        // Arguments past the last parameter bind to the params array: they are neither message nor inner exception.
        => await VerifyFix("FormatException",
            "throw new {|#0:FormatException|}(\"bad {0} {1}\", 1, 2);",
            "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(\"bad {0} {1}\");");

    [Fact]
    public async Task A_parenthesized_inner_exception_alone_brings_its_message()
        => await VerifyFix("WrapException",
            "var e = new Exception(\"inner\"); throw new {|#0:WrapException|}((e));",
            "var e = new Exception(\"inner\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException((e).Message, (e));");

    [Fact]
    public async Task An_inner_exception_read_through_this_brings_its_message()
        => await VerifyFix("WrapException",
            "throw new {|#0:WrapException|}(this.Last);",
            "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(this.Last.Message, this.Last);");

    [Fact]
    public async Task An_inner_exception_of_a_foreign_type_named_Exception_is_recognized_by_its_base()
        // Demo.Other.Exception is named Exception but is not System.Exception; it still derives from it.
        => await VerifyFix("ForeignException",
            "var e = new Demo.Other.Exception(); throw new {|#0:ForeignException|}(e);",
            "var e = new Demo.Other.Exception(); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(e.Message, e);");

    [Fact]
    public async Task A_call_that_fails_overload_resolution_binds_to_the_single_candidate()
    {
        // WrapException has one constructor, so its argument still binds to `inner`; the extra argument has no
        // parameter and is dropped. The fix also clears the compiler error.
        string before = Harness.Replace("BODY", "var e = new Exception(\"inner\"); throw new {|#0:WrapException|}(e, e);");
        string after = Harness.Replace("BODY", "var e = new Exception(\"inner\"); throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException(e.Message, e);");
        DiagnosticResult[] expected =
        {
            new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0).WithArguments("WrapException"),
            DiagnosticResult.CompilerError("CS1729").WithLocation(0).WithArguments("Demo.WrapException", "2"),
        };
        await VerifyErrorClearingFix(before, expected, after);
    }

    [Fact]
    public async Task A_call_with_several_failed_candidates_becomes_the_parameterless_constructor()
    {
        // ArgumentException has several constructors and none takes four arguments: nothing is known to be a message.
        string before = Harness.Replace("BODY", "throw new {|#0:ArgumentException|}(1, 2, 3, 4);");
        string after = Harness.Replace("BODY", "throw new global::Microsoft.Xrm.Sdk.InvalidPluginExecutionException();");
        DiagnosticResult[] expected =
        {
            new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0).WithArguments("ArgumentException"),
            DiagnosticResult.CompilerError("CS1729").WithLocation(0).WithArguments("System.ArgumentException", "4"),
        };
        await VerifyErrorClearingFix(before, expected, after);
    }

    // The source carries a compiler error that the fix removes: the fixed state expects no diagnostic at all.
    private static async Task VerifyErrorClearingFix(string before, DiagnosticResult[] expected, string after)
    {
        var test = new Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixTest<
            Vela.Analyzers.PluginExceptionTypeAnalyzer, ThrowInvalidPluginExecutionExceptionCodeFix, Microsoft.CodeAnalysis.Testing.DefaultVerifier>
        {
            TestCode = before,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        test.FixedState.InheritanceMode = StateInheritanceMode.Explicit;
        test.FixedState.Sources.Add(after);
        await test.RunAsync();
    }
}
