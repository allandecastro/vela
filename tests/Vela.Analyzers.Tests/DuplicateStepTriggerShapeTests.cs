using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateStepTriggerAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA008 with several steps per trigger: two chains on one trigger are flagged only when nothing tells them apart
// (same FilterOn columns, same images, no WithStepKey / ForRelationship). The rank does not tell them apart.
public sealed class DuplicateStepTriggerShapeTests
{
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnUpdate(); IStages OnAssociate(); }
    public interface IStages : IFluentStep { IMode PreOperation(); IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); IConfig Async(bool deleteJobOnSuccess); }
    public interface IConfig : IFluentStep
    {
        IConfig FilterOn(Expression<Func<Demo.Account, object>> attribute, params Expression<Func<Demo.Account, object>>[] more);
        IConfig WithPreImage(params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithPreImage(string alias, params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithStepKey(string key);
        IConfig WithExecutionOrder(int order);
        IConfig ForRelationship(string schemaName, params string[] more);
        IConfig SkipWhenDepthAbove(int maxDepth);
        void Handle();
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Account { public string Name { get; set; } public string Phone { get; set; } }
    public class Plugin { protected ITrigger RegisterStep() => null!; }
}
";

    private static string InRegister(string first, string second) => Harness + @"
namespace Demo
{
    public class Use : Plugin
    {
        public void RegisterPluginSteps()
        {
            RegisterStep()." + first + @".Handle();
            RegisterStep()." + second + @".Handle();
        }
    }
}
";

    private static DiagnosticResult Expected() =>
        new DiagnosticResult(Vela.Analyzers.DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments("message 'update' at postoperation (sync)");

    [Fact]
    public async Task Flags_two_steps_with_the_same_filter_even_at_different_ranks()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithExecutionOrder(1)",
            "{|#0:OnUpdate|}().PostOperation().Sync().WithExecutionOrder(2).FilterOn(a => (object)a.Name)");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_two_steps_with_the_same_filter_and_the_same_image()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name, a => a.Phone).WithPreImage(a => a.Name)",
            "{|#0:OnUpdate|}().PostOperation().Sync().FilterOn(a => a.Phone, a => a.Name).WithPreImage(\"PreImage\", a => a.Name)");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Does_not_flag_steps_with_different_filters()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Phone)");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_steps_with_different_images()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage(a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage(a => a.Phone)");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_step_with_an_explicit_step_key()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithStepKey(\"second\")");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_relationship_scoped_steps()
    {
        string code = InRegister(
            "OnAssociate().PostOperation().Sync().ForRelationship(\"account_contacts\")",
            "OnAssociate().PostOperation().Sync().ForRelationship(\"account_partners\")");
        await Verify.VerifyAnalyzerAsync(code);
    }

    private static DiagnosticResult DuplicateKey(string key) =>
        new DiagnosticResult(Vela.Analyzers.DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithMessage($"This plugin already registers a step with the step key '{key}'; step keys must be unique within a plugin")
            .WithLocation(0);

    [Fact]
    public async Task Flags_a_step_key_used_twice_on_one_trigger_whatever_the_filters()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithStepKey(\"same\")",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Phone).WithStepKey({|#0:\" SAME \"|})");
        await Verify.VerifyAnalyzerAsync(code, DuplicateKey("SAME"));
    }

    [Fact]
    public async Task Flags_a_constant_step_key_used_twice_on_different_triggers()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(Selector).WithStepKey(Key)",
            "OnAssociate().PreOperation().Async().WithStepKey({|#0:Key|})").Replace(
            "public void RegisterPluginSteps()",
            "private const string Key = \"shared\";\n        private static readonly Expression<Func<Account, object>> Selector = a => a.Name;\n        public void RegisterPluginSteps()");
        await Verify.VerifyAnalyzerAsync(code, DuplicateKey("shared"));
    }

    [Fact]
    public async Task Does_not_flag_different_step_keys_or_a_key_that_is_not_constant()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().WithStepKey(\"first\")",
            "OnUpdate().PostOperation().Sync().WithStepKey(\"second\")"));
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().WithStepKey(\"first\")",
            "OnUpdate().PostOperation().Sync().WithStepKey(Dynamic)").Replace(
            "public void RegisterPluginSteps()",
            "private static readonly string Dynamic = \"first\";\n        public void RegisterPluginSteps()"));
    }

    [Fact]
    public async Task Flags_two_steps_with_the_same_relationship_scope_in_any_order_and_case()
    {
        string code = InRegister(
            "OnAssociate().PostOperation().Sync().ForRelationship(\"account_contacts\", Partners)",
            "{|#0:OnAssociate|}().PostOperation().Sync().ForRelationship(\"Account_Partners\", \"ACCOUNT_CONTACTS\")").Replace(
            "public void RegisterPluginSteps()",
            "private const string Partners = \"account_partners\";\n        public void RegisterPluginSteps()");
        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(Vela.Analyzers.DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("message 'associate' at postoperation (sync)"));
    }

    [Fact]
    public async Task Does_not_compare_a_relationship_scope_that_is_not_constant()
    {
        string code = InRegister(
            "OnAssociate().PostOperation().Sync().ForRelationship(\"account_contacts\")",
            "OnAssociate().PostOperation().Sync().ForRelationship(Dynamic)").Replace(
            "public void RegisterPluginSteps()",
            "private static readonly string Dynamic = \"account_contacts\";\n        public void RegisterPluginSteps()");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_compare_a_step_whose_filter_is_not_a_literal_selector()
    {
        string code = InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(Selector)").Replace(
            "public void RegisterPluginSteps()",
            "private static readonly Expression<Func<Account, object>> Selector = a => a.Name;\n        public void RegisterPluginSteps()");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Reads_a_chain_using_async_delete_relationship_and_depth_modifiers()
    {
        // Async(bool), ForRelationship and SkipWhenDepthAbove sit on the spine like any modifier: the trigger is
        // still read (async mode), and the duplicate without them is flagged.
        string code = InRegister(
            "OnUpdate().PostOperation().Async(deleteJobOnSuccess: true).FilterOn(a => a.Name).SkipWhenDepthAbove(2)",
            "{|#0:OnUpdate|}().PostOperation().Async(true).SkipWhenDepthAbove(3).FilterOn(a => a.Name)");
        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(Vela.Analyzers.DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("message 'update' at postoperation (async)"));
    }
}
