using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.StepCompletionAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class StepCompletionAnalyzerTests
{
    // Minimal stand-in for the real builder surface: a fluent type (implements Vela.Steps.IFluentStep) whose
    // Handle() returns void (the terminal). RegisterStep() starts a chain.
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep { IFakeStep WithSomething(); void Handle(); }
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

    [Fact]
    public async Task Flags_a_chain_that_never_calls_Handle()
    {
        // The statement's value is still IFakeStep (a fluent builder) -> Handle was never bound.
        string code = InMethod("{|#0:RegisterStep().WithSomething()|};");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.StepCompletionAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0));
    }

    [Fact]
    public async Task Does_not_flag_a_chain_that_ends_with_Handle()
    {
        // Handle() returns void -> the statement's value is not a fluent builder -> complete.
        string code = InMethod("RegisterStep().WithSomething().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    private static string InClass(string members) => Harness + @"
namespace Demo
{
    public class Use : Plugin
    {
        " + members + @"
    }
}
";

    [Fact]
    public async Task Flags_an_expression_bodied_void_registration_that_never_calls_Handle()
    {
        // The form most samples use: RegisterPluginSteps() => RegisterStep()...; discards its value like a statement.
        string code = InClass("public void M() => {|#0:RegisterStep().WithSomething()|};");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.StepCompletionAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0));
    }

    [Fact]
    public async Task Does_not_flag_an_expression_body_that_returns_the_builder()
    {
        // A method or getter returning the builder hands the chain on, to be completed by the caller.
        string code = InClass(@"public Vela.Steps.IFakeStep Start() => RegisterStep().WithSomething();
        public Vela.Steps.IFakeStep Current => RegisterStep();
        public void Done() => RegisterStep().WithSomething().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_setter_constructor_and_local_function_expression_body_that_never_calls_Handle()
    {
        string code = InClass(@"public int Value { get => 0; set => {|#0:RegisterStep()|}; }
        public Use() => {|#1:RegisterStep()|};
        public void M() { void Local() => {|#2:RegisterStep()|}; Local(); }");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.StepCompletionAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0),
            new DiagnosticResult(Vela.Analyzers.StepCompletionAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(1),
            new DiagnosticResult(Vela.Analyzers.StepCompletionAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(2));
    }
}
