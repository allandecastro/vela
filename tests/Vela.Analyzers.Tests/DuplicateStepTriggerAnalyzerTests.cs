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
        IStages OnMessage<TMessage>() where TMessage : class;
        IStages On<TRequest>();
        IStages OnCreate();
        IStages OnUpdate();
        IStages OnAssign();
        IStages OnSendEmail();
        IStages OnSendFax();
        IStages OnWinOpportunity();
        IStages OnRouteTo();
        IStages OnFoo();
        IAsyncStages OnAsyncOnly<TRequest>();
    }
    public interface IStages : IFluentStep { IMode PreOperation(); IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); }
    public interface IAsyncStages : IFluentStep { IConfig PostOperation(); }
    public interface IConfig : IFluentStep { void Handle(); IConfig WithDescription(string d); }
}

namespace Microsoft.Xrm.Sdk.Messages { public class CreateRequest { } }
namespace Microsoft.Crm.Sdk.Messages
{
    public class AssignRequest { }
    public class SendEmailRequest { }
    public class SendFaxRequest { }
    public class SetStateRequest { }
    public class WinOpportunityRequest { }
}

namespace Vela.Messages
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class SdkMessageAttribute : System.Attribute { public SdkMessageAttribute(string messageName) { } }
}

namespace Demo
{
    [Vela.Messages.SdkMessage(""SetState"")] public class PinnedSetState { }
    [Vela.Messages.SdkMessage(""SetStateDynamicEntity"")] public class StateChange { }
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
    public async Task Flags_guided_and_request_typed_forms_of_the_same_trigger()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:On<Microsoft.Xrm.Sdk.Messages.CreateRequest>|}().PreOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'create' at preoperation (sync)"));
    }

    [Fact]
    public async Task Flags_guided_and_marker_forms_of_assign()
    {
        string code = InRegister(
            "RegisterStep().OnAssign().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnMessage<Microsoft.Crm.Sdk.Messages.AssignRequest>|}().PostOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'assign' at postoperation (sync)"));
    }

    [Fact]
    public async Task Flags_two_requests_that_register_on_the_same_sdkmessage()
    {
        // SendEmail and SendFax both run as the Send sdkmessage, so both steps land on one trigger.
        string code = InRegister(
            "RegisterStep().On<Microsoft.Crm.Sdk.Messages.SendEmailRequest>().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:On<Microsoft.Crm.Sdk.Messages.SendFaxRequest>|}().PostOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'send' at postoperation (sync)"));
    }

    [Fact]
    public async Task Keys_SetStateRequest_on_SetStateDynamicEntity_unless_a_type_pins_its_sdkmessage()
    {
        string code = InRegister(
            "RegisterStep().On<Microsoft.Crm.Sdk.Messages.SetStateRequest>().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().On<PinnedSetState>().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnMessage<StateChange>|}().PostOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'setstatedynamicentity' at postoperation (sync)"));
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

    [Fact]
    public async Task Flags_every_typed_trigger_not_only_the_crud_ones()
    {
        // The message is derived from the method name against the shared TypedTriggers list.
        string code = InRegister(
            "RegisterStep().OnRouteTo().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnRouteTo|}().PostOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'routeto' at postoperation (sync)"));
    }

    [Fact]
    public async Task Keys_a_typed_trigger_by_the_sdkmessage_it_registers_on()
    {
        // OnWinOpportunity() registers on Win; the request-typed form of the same message collides with it.
        string code = InRegister(
            "RegisterStep().OnSendEmail().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnSendFax|}().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().OnWinOpportunity().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#1:On<Microsoft.Crm.Sdk.Messages.WinOpportunityRequest>|}().PreOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code,
            Expected("message 'send' at postoperation (sync)"),
            new DiagnosticResult(Vela.Analyzers.DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                .WithLocation(1).WithArguments("message 'win' at preoperation (sync)"));
    }

    [Fact]
    public async Task Does_not_read_an_On_method_that_is_not_a_typed_trigger()
    {
        // OnFoo() is no typed trigger (a generated extension for a message Vela does not model): never guessed.
        string code = InRegister(
            "RegisterStep().OnFoo().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().OnFoo().PostOperation().Sync().Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Treats_an_OnAsyncOnly_step_as_asynchronous()
    {
        // An OnAsyncOnly<TRequest>() step runs asynchronously: it collides with an async step on the same message, not
        // with a sync one, even when its chain names no mode.
        string code = InRegister(
            "RegisterStep().On<Microsoft.Crm.Sdk.Messages.SendEmailRequest>().PostOperation().Sync().Handle();\n" +
            "            RegisterStep().OnAsyncOnly<Microsoft.Crm.Sdk.Messages.SendEmailRequest>().PostOperation().WithDescription(\"x\").Handle();\n" +
            "            RegisterStep().{|#0:OnAsyncOnly<Microsoft.Crm.Sdk.Messages.SendEmailRequest>|}().PostOperation().WithDescription(\"y\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("message 'send' at postoperation (async)"));
    }
}
