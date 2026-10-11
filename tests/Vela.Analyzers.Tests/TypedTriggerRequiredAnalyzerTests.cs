using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.TypedTriggerRequiredAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA023: one way per message. On<TRequest>() / OnAsyncOnly<TRequest>() are for messages without a typed trigger.
public sealed class TypedTriggerRequiredAnalyzerTests
{
    internal const string Harness = @"
namespace Microsoft.Xrm.Sdk { public class OrganizationRequest { } }
namespace Microsoft.Xrm.Sdk.Messages
{
    public sealed class UpdateRequest : Microsoft.Xrm.Sdk.OrganizationRequest { }
    public sealed class CreateMultipleRequest : Microsoft.Xrm.Sdk.OrganizationRequest { }
}
namespace Microsoft.Crm.Sdk.Messages
{
    public sealed class AssignRequest : Microsoft.Xrm.Sdk.OrganizationRequest { }
    public sealed class SetStateRequest : Microsoft.Xrm.Sdk.OrganizationRequest { }
}
namespace Vela.Messages
{
    public sealed class CreateRequest<TEntity> : Microsoft.Xrm.Sdk.OrganizationRequest { }
}
namespace Demo.Messages
{
    public sealed class UpdateRequest : Microsoft.Xrm.Sdk.OrganizationRequest { }
    public sealed class ScoreRequest : Microsoft.Xrm.Sdk.OrganizationRequest { }
}
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStepTrigger : IFluentStep
    {
        IConfig OnCreate();
        IConfig OnUpdate();
        IConfig OnCreateMultiple();
        IConfig OnAssign();
        IConfig OnMessage<TMessage>() where TMessage : class;
        IConfig On<TRequest>() where TRequest : Microsoft.Xrm.Sdk.OrganizationRequest, new();
        IConfig OnAsyncOnly<TRequest>() where TRequest : Microsoft.Xrm.Sdk.OrganizationRequest, new();
    }
    public interface IConfig : IFluentStep { void Handle(); }
}
namespace Demo
{
    using Vela.Steps;
    public class Account { }
    public class Plugin { protected IStepTrigger RegisterStep() => null!; }
}
";

    internal static string InMethod(string body) => Harness + @"
namespace Demo
{
    public class Use : Plugin { public void M() { " + body + @" } }
}
";

    private static DiagnosticResult Expected(string message, string trigger, string method, string request) =>
        new DiagnosticResult(TypedTriggerRequiredAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(message, trigger, method, request);

    [Fact]
    public async Task Flags_an_sdk_request_whose_message_has_a_typed_trigger()
    {
        string code = InMethod("RegisterStep().{|#0:On<Microsoft.Xrm.Sdk.Messages.UpdateRequest>|}().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Update", "OnUpdate", "On", "UpdateRequest"));
    }

    [Fact]
    public async Task Flags_a_crm_sdk_request_and_the_async_only_trigger()
    {
        string code = InMethod("RegisterStep().{|#0:OnAsyncOnly<Microsoft.Crm.Sdk.Messages.AssignRequest>|}().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Assign", "OnAssign", "OnAsyncOnly", "AssignRequest"));
    }

    [Fact]
    public async Task Flags_a_vela_typed_request_and_a_bulk_request()
    {
        string code = InMethod(
            "RegisterStep().{|#0:On<Vela.Messages.CreateRequest<Account>>|}().Handle();\n" +
            "RegisterStep().{|#1:On<Microsoft.Xrm.Sdk.Messages.CreateMultipleRequest>|}().Handle();");
        await Verify.VerifyAnalyzerAsync(code,
            Expected("Create", "OnCreate", "On", "CreateRequest"),
            new DiagnosticResult(TypedTriggerRequiredAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                .WithLocation(1).WithArguments("CreateMultiple", "OnCreateMultiple", "On", "CreateMultipleRequest"));
    }

    // The permissive marker/request trigger is the last other path, so it follows the same rule.
    [Fact]
    public async Task Flags_the_permissive_request_trigger()
    {
        string code = InMethod("RegisterStep().{|#0:OnMessage<Microsoft.Xrm.Sdk.Messages.UpdateRequest>|}().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Update", "OnUpdate", "OnMessage", "UpdateRequest"));
    }

    [Fact]
    public async Task Does_not_flag_a_message_without_a_typed_trigger()
    {
        // SetState has no OnSetState(); a custom API request and a same-named request outside the SDK namespaces
        // are custom messages.
        string code = InMethod(
            "RegisterStep().On<Microsoft.Crm.Sdk.Messages.SetStateRequest>().Handle();\n" +
            "RegisterStep().On<Demo.Messages.ScoreRequest>().Handle();\n" +
            "RegisterStep().On<Demo.Messages.UpdateRequest>().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_the_typed_trigger_itself()
    {
        string code = InMethod("RegisterStep().OnUpdate().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
