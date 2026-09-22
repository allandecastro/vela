using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.PreferTypedMessageAnalyzer,
    Vela.CodeFixes.PreferTypedTriggerCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class PreferTypedTriggerCodeFixTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep OnMessage(string message);
        IFakeStep OnCreate();
        IFakeStep OnUpdate();
        void Handle();
    }
}
namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected IFakeStep RegisterStep() => null!; }
    public class Use : Plugin { public void M() { BODY } }
}";

    [Fact]
    public async Task Replaces_OnMessage_create_with_OnCreate()
    {
        string before = Harness.Replace("BODY", "RegisterStep().{|#0:OnMessage|}(\"create\").Handle();");
        string after = Harness.Replace("BODY", "RegisterStep().OnCreate().Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PreferTypedMessageAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("OnCreate", "create");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Replaces_OnMessage_update_with_OnUpdate()
    {
        string before = Harness.Replace("BODY", "RegisterStep().{|#0:OnMessage|}(\"update\").Handle();");
        string after = Harness.Replace("BODY", "RegisterStep().OnUpdate().Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.PreferTypedMessageAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0).WithArguments("OnUpdate", "update");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }
}
