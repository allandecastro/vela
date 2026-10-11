using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.TypedTriggerRequiredAnalyzer,
    Vela.CodeFixes.TypedTriggerRequiredCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class TypedTriggerRequiredCodeFixTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public class OrganizationRequest { } }
namespace Microsoft.Xrm.Sdk.Messages { public sealed class UpdateRequest : Microsoft.Xrm.Sdk.OrganizationRequest { } }
namespace Microsoft.Crm.Sdk.Messages { public sealed class AssignRequest : Microsoft.Xrm.Sdk.OrganizationRequest { } }
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStepTrigger : IFluentStep
    {
        IConfig OnUpdate();
        IConfig OnAssign();
        IConfig On<TRequest>() where TRequest : Microsoft.Xrm.Sdk.OrganizationRequest, new();
        IConfig OnAsyncOnly<TRequest>() where TRequest : Microsoft.Xrm.Sdk.OrganizationRequest, new();
    }
    public interface IConfig : IFluentStep { void Handle(); }
}
namespace Demo
{
    using Vela.Steps;
    using Microsoft.Xrm.Sdk.Messages;
    using Microsoft.Crm.Sdk.Messages;
    public class Plugin { protected IStepTrigger RegisterStep() => null!; }
    public class Use : Plugin { public void M() { BODY } }
}";

    [Fact]
    public async Task Rewrites_On_UpdateRequest_to_OnUpdate()
    {
        string before = Harness.Replace("BODY", "RegisterStep().{|#0:On<UpdateRequest>|}().Handle();");
        string after = Harness.Replace("BODY", "RegisterStep().OnUpdate().Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.TypedTriggerRequiredAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("Update", "OnUpdate", "On", "UpdateRequest");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Rewrites_OnAsyncOnly_AssignRequest_to_OnAssign()
    {
        string before = Harness.Replace("BODY", "RegisterStep().{|#0:OnAsyncOnly<AssignRequest>|}().Handle();");
        string after = Harness.Replace("BODY", "RegisterStep().OnAssign().Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.TypedTriggerRequiredAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("Assign", "OnAssign", "OnAsyncOnly", "AssignRequest");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }
}
