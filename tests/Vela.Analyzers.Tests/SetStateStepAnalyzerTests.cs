using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.SetStateStepAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class SetStateStepAnalyzerTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep OnMessage(string message);
        void Handle();
    }
}

namespace Vela.Constants
{
    public static class PluginMessages
    {
        public const string SetState = ""SetState"";
        public const string SetStateDynamicEntity = ""SetStateDynamicEntity"";
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
    }

    public class Other
    {
        public Other OnMessage(string message) => this;
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    using Vela.Constants;
    public class Use : Plugin
    {
        public void M()
        {
            " + body + @"
        }
    }
}
";

    private static DiagnosticResult Expected() =>
        new DiagnosticResult(Vela.Analyzers.SetStateStepAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0);

    [Fact]
    public async Task Warns_on_the_SetState_constant()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().{|#0:OnMessage|}(PluginMessages.SetState).Handle();"), Expected());
    }

    [Fact]
    public async Task Warns_on_a_SetState_literal_case_insensitively()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().{|#0:OnMessage|}(\"setstate\").Handle();"), Expected());
    }

    [Fact]
    public async Task Is_silent_for_SetStateDynamicEntity()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().OnMessage(PluginMessages.SetStateDynamicEntity).Handle();"));
    }

    [Fact]
    public async Task Is_silent_for_a_non_step_OnMessage()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("new Other().OnMessage(\"SetState\");"));
    }

    [Fact]
    public async Task Is_silent_for_a_message_that_is_not_constant()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("string name = \"SetState\"; RegisterStep().OnMessage(name).Handle();"));
    }
}
