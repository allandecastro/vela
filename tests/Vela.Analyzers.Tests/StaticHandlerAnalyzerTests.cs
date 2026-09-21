using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.StaticHandlerAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class StaticHandlerAnalyzerTests
{
    // Minimal builder surface: a fluent Handle(Action<int>) terminal, plus a plugin exposing a static and an
    // instance handler candidate, and an unrelated (non-fluent) type that also has a Handle method.
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        void Handle(System.Action<int> handler);
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
        protected void InstanceHandler(int ctx) { }
        protected static void StaticHandler(int ctx) { }
    }

    public static class Handlers
    {
        public static void OnCreate(int ctx) { }
    }

    public class NotAStep
    {
        public void Handle(System.Action<int> handler) { }
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
        new DiagnosticResult(Vela.Analyzers.StaticHandlerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(method);

    [Fact]
    public async Task Flags_a_static_method_handler()
    {
        string code = InMethod("RegisterStep().Handle({|#0:StaticHandler|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("StaticHandler"));
    }

    [Fact]
    public async Task Flags_a_static_handler_from_another_type()
    {
        string code = InMethod("RegisterStep().Handle({|#0:Handlers.OnCreate|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("OnCreate"));
    }

    [Fact]
    public async Task Does_not_flag_an_instance_method_handler()
    {
        string code = InMethod("RegisterStep().Handle(InstanceHandler);");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_lambda_handler()
    {
        string code = InMethod("RegisterStep().Handle(ctx => { });");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_typed_lambda_handler()
    {
        string code = InMethod("RegisterStep().Handle((int ctx) => { });");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_static_handler_on_a_non_fluent_type()
    {
        string code = InMethod("new NotAStep().Handle(StaticHandler);");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
