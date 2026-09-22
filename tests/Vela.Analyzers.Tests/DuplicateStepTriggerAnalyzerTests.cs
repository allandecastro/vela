using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateStepTriggerAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class DuplicateStepTriggerAnalyzerTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep
    {
        IStages OnMessage(string message);
        IStages OnCreate();
        IStages OnUpdate();
    }
    public interface IStages : IFluentStep { IMode PreOperation(); IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); }
    public interface IConfig : IFluentStep { void Handle(); }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }
}
";

    // Wraps the chains in a RegisterPluginSteps() method (the analyzer only looks there).
    private static string InRegister(string body) => Harness + @"
namespace Demo
{
    public class Use : Plugin { public void RegisterPluginSteps() { " + body + @" } }
}
";

    private static DiagnosticResult Expected(string display) =>
        new DiagnosticResult(Vela.Analyzers.DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(display);

    [Fact]
    public async Task Flags_two_identical_triggers()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnCreate|}().PreOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'create' at preoperation (sync)"));
    }

    [Fact]
    public async Task Flags_typed_and_permissive_forms_of_the_same_trigger()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnMessage|}(\"Create\").PreOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'create' at preoperation (sync)"));
    }

    [Fact]
    public async Task Does_not_flag_different_stages()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().OnCreate().PostOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_different_modes()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().OnCreate().PostOperation().Async().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_different_messages()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().OnUpdate().PreOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
