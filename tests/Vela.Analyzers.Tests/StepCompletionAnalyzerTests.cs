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
}
