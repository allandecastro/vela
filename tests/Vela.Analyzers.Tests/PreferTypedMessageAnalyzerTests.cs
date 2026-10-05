using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.PreferTypedMessageAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class PreferTypedMessageAnalyzerTests
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

    private static DiagnosticResult Expected(string suggestion, string message) =>
        new DiagnosticResult(Vela.Analyzers.PreferTypedMessageAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithArguments(suggestion, message);

    [Fact]
    public async Task Warns_on_OnMessage_create()
    {
        string code = InMethod("RegisterStep().{|#0:OnMessage|}(\"create\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("OnCreate", "create"));
    }

    [Fact]
    public async Task Warns_on_OnMessage_update_case_insensitively()
    {
        string code = InMethod("RegisterStep().{|#0:OnMessage|}(\"Update\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("OnUpdate", "Update"));
    }

    [Fact]
    public async Task Warns_on_OnMessage_assign()
    {
        string code = InMethod("RegisterStep().{|#0:OnMessage|}(\"Assign\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("OnAssign", "Assign"));
    }

    [Fact]
    public async Task Does_not_warn_for_an_unmodelled_message()
    {
        string code = InMethod("RegisterStep().OnMessage(\"adc_DoThing\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_warn_when_the_message_is_not_a_literal()
    {
        string code = InMethod("string m = \"create\"; RegisterStep().OnMessage(m).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
