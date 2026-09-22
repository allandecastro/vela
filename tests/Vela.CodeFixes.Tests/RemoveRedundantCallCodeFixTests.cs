using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyDup = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.DuplicateModifierAnalyzer,
    Vela.CodeFixes.RemoveRedundantCallCodeFix>;
using VerifyImage = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.ImageStageAnalyzer,
    Vela.CodeFixes.RemoveRedundantCallCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class RemoveRedundantCallCodeFixTests
{
    private const string DupHarness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep WithExecutionOrder(int order);
        void Handle();
    }
}
namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected IFakeStep RegisterStep() => null!; }
    public class Use : Plugin { public void M() { BODY } }
}";

    private const string ImageHarness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnMessage(string message); }
    public interface IStages : IFluentStep { IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); }
    public interface IConfig : IFluentStep
    {
        IConfig WithPreImage(params string[] columns);
        void Handle();
    }
}
namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }
    public class Use : Plugin { public void M() { BODY } }
}";

    [Fact]
    public async Task Removes_a_duplicate_modifier_VELA003()
    {
        string before = DupHarness.Replace("BODY", "RegisterStep().WithExecutionOrder(1).{|#0:WithExecutionOrder|}(2).Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().WithExecutionOrder(1).Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("WithExecutionOrder");
        await VerifyDup.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Removes_an_invalid_image_VELA007()
    {
        string before = ImageHarness.Replace("BODY", "RegisterStep().OnMessage(\"create\").PostOperation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        string after = ImageHarness.Replace("BODY", "RegisterStep().OnMessage(\"create\").PostOperation().Sync().Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("A pre-image", "Create", "there is no prior row state before a create");
        await VerifyImage.VerifyCodeFixAsync(before, expected, after);
    }
}
