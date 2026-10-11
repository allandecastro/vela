using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyDuplicate = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateStepTriggerAnalyzer>;
using VerifyPreferTyped = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.PreferTypedMessageAnalyzer>;

namespace Vela.Analyzers.Tests;

// The trigger vocabulary the analyzers share with the runtime: every typed On{Message}() maps to its Dataverse
// message (VELA008 compares a typed trigger with the permissive OnMessage("...") form), every modelled message
// named through OnMessage("...") is pointed at its typed trigger (VELA006), and every stage method is read.
// Several steps may share a trigger when something tells them apart (filters, images, relationship or step key;
// see DuplicateStepTriggerShapeTests); VELA008 flags only steps that nothing tells apart, and the rank is not one.
public sealed class StepTriggerVocabularyTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStep : IFluentStep
    {
        IStep OnMessage(string message);
        IStep OnCreate();
        IStep OnUpdate();
        IStep OnDelete();
        IStep OnCreateMultiple();
        IStep OnUpdateMultiple();
        IStep OnRetrieve();
        IStep OnRetrieveMultiple();
        IStep OnAssociate();
        IStep OnDisassociate();
        IStep OnAssign();
        IStep OnMerge();
        IStep Named(string name);
        IStep WithStepKey(string key);
        IStep WithExecutionOrder(int rank);
        IStep PreValidation();
        IStep PreOperation();
        IStep PostOperation();
        IStep Sync();
        IStep Async();
        void Handle();
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected IStep RegisterStep() => null!; }
    public class Use : Plugin
    {
        public void RegisterPluginSteps()
        {
            BODY
        }
    }
}
";

    private static string InRegister(string body) => Harness.Replace("BODY", body);

    private static DiagnosticResult Duplicate(string display) =>
        new DiagnosticResult(DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0).WithArguments(display);

    [Theory]
    [InlineData("OnCreate", "create")]
    [InlineData("OnUpdate", "update")]
    [InlineData("OnDelete", "delete")]
    [InlineData("OnCreateMultiple", "createmultiple")]
    [InlineData("OnUpdateMultiple", "updatemultiple")]
    [InlineData("OnRetrieve", "retrieve")]
    [InlineData("OnRetrieveMultiple", "retrievemultiple")]
    [InlineData("OnAssociate", "associate")]
    [InlineData("OnDisassociate", "disassociate")]
    [InlineData("OnAssign", "assign")]
    [InlineData("OnMerge", "merge")]
    public async Task A_typed_trigger_collides_with_the_permissive_form_of_its_message(string typed, string message)
    {
        string code = InRegister(
            "RegisterStep()." + typed + "().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().{|#0:OnMessage|}(\"" + message.ToUpperInvariant() + "\").PreOperation().Sync().Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code, Duplicate($"message '{message}' at preoperation (sync)"));
    }

    [Theory]
    [InlineData("OnCreate", "create")]
    [InlineData("OnUpdate", "update")]
    [InlineData("OnDelete", "delete")]
    [InlineData("OnAssociate", "associate")]
    [InlineData("OnMerge", "merge")]
    public async Task A_step_key_tells_two_steps_on_the_same_trigger_apart(string typed, string message)
    {
        string code = InRegister(
            "RegisterStep()." + typed + "().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().OnMessage(\"" + message + "\").PreOperation().Sync().WithStepKey(\"second\").Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task A_different_rank_does_not_tell_two_steps_apart()
    {
        string code = InRegister(
            "RegisterStep().OnUpdate().PostOperation().Sync().WithExecutionOrder(1).Handle();\n" +
            "            RegisterStep().{|#0:OnUpdate|}().PostOperation().Sync().WithExecutionOrder(2).Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code, Duplicate("message 'update' at postoperation (sync)"));
    }

    [Theory]
    [InlineData("PreValidation", "prevalidation")]
    [InlineData("PreOperation", "preoperation")]
    [InlineData("PostOperation", "postoperation")]
    public async Task Each_stage_is_part_of_the_trigger(string stageMethod, string stage)
    {
        string code = InRegister(
            "RegisterStep().OnUpdate()." + stageMethod + "().Async().Handle();\n" +
            "            RegisterStep().{|#0:OnUpdate|}()." + stageMethod + "().Async().Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code, Duplicate($"message 'update' at {stage} (async)"));
    }

    [Fact]
    public async Task Steps_that_differ_only_by_stage_do_not_collide()
    {
        string code = InRegister(
            "RegisterStep().OnUpdate().PreValidation().Sync().Handle();\n" +
            "            RegisterStep().OnUpdate().PreOperation().Sync().Handle();\n" +
            "            RegisterStep().OnUpdate().PostOperation().Sync().Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task A_chain_without_a_stage_is_never_compared()
    {
        string code = InRegister(
            "RegisterStep().OnCreate().Sync().Handle();\n" +
            "            RegisterStep().OnCreate().Sync().Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task A_string_argument_of_another_call_is_not_read_as_the_message()
    {
        // Named("same") is not a trigger: these chains have no readable message, so they are skipped, not compared.
        string code = InRegister(
            "RegisterStep().Named(\"same\").PreOperation().Sync().Handle();\n" +
            "            RegisterStep().Named(\"same\").PreOperation().Sync().Handle();");

        await VerifyDuplicate.VerifyAnalyzerAsync(code);
    }

    [Theory]
    [InlineData("create", "OnCreate")]
    [InlineData("update", "OnUpdate")]
    [InlineData("delete", "OnDelete")]
    [InlineData("createmultiple", "OnCreateMultiple")]
    [InlineData("updatemultiple", "OnUpdateMultiple")]
    [InlineData("retrieve", "OnRetrieve")]
    [InlineData("retrievemultiple", "OnRetrieveMultiple")]
    [InlineData("associate", "OnAssociate")]
    [InlineData("disassociate", "OnDisassociate")]
    [InlineData("assign", "OnAssign")]
    [InlineData("Merge", "OnMerge")]
    public async Task OnMessage_of_a_modelled_message_suggests_its_typed_trigger(string message, string suggestion)
    {
        // The analyzer finds the typed trigger on the trigger type itself (#482), here the harness's full IStep.
        string code = InRegister("RegisterStep().{|#0:OnMessage|}(\"" + message + "\").Handle();");

        await VerifyPreferTyped.VerifyAnalyzerAsync(code,
            new DiagnosticResult(PreferTypedMessageAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments(suggestion, message)
                .WithMessage($"{message} has a typed trigger: register the step with {suggestion}() instead of OnMessage(\"{message}\"); OnMessage(...) is only for messages without one"));
    }
}
