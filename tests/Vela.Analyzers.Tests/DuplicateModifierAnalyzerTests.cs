using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateModifierAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class DuplicateModifierAnalyzerTests
{
    // Minimal stand-in for the builder surface: a fluent type (implements Vela.Steps.IFluentStep) exposing the
    // set-once modifiers, each returning the same builder so they can be chained in any order before Handle().
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep FilterOn(string a);
        IFakeStep WithExecutionOrder(int order);
        IFakeStep WithDescription(string description);
        IFakeStep WithStepKey(string key);
        void Handle();
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    public class Use : Plugin
    {
        public void M()
        {
            " + body + @"
        }
    }
}
";

    private static DiagnosticResult Expected(string method) =>
        new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(method);

    [Fact]
    public async Task Flags_a_second_WithExecutionOrder()
    {
        string code = InMethod("RegisterStep().WithExecutionOrder(1).{|#0:WithExecutionOrder|}(2).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("WithExecutionOrder"));
    }

    [Fact]
    public async Task Flags_a_second_FilterOn()
    {
        string code = InMethod("RegisterStep().FilterOn(\"a\").{|#0:FilterOn|}(\"b\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("FilterOn"));
    }

    [Fact]
    public async Task Flags_the_third_call_too()
    {
        // Both the second and third occurrence have an earlier call in their receiver spine.
        string code = InMethod("RegisterStep().WithDescription(\"a\").{|#0:WithDescription|}(\"b\").{|#1:WithDescription|}(\"c\").Handle();");
        await Verify.VerifyAnalyzerAsync(
            code,
            Expected("WithDescription"),
            new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(1).WithArguments("WithDescription"));
    }

    [Fact]
    public async Task Does_not_flag_a_single_call()
    {
        string code = InMethod("RegisterStep().WithExecutionOrder(1).WithDescription(\"a\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_two_different_modifiers()
    {
        string code = InMethod("RegisterStep().FilterOn(\"a\").WithExecutionOrder(1).WithStepKey(\"k\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_the_same_modifier_on_two_separate_steps()
    {
        string code = InMethod(
            "RegisterStep().WithExecutionOrder(1).Handle();\n" +
            "            RegisterStep().WithExecutionOrder(2).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
