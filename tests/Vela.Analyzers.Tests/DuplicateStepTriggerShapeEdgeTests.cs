using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateStepTriggerAnalyzer>;

namespace Vela.Analyzers.Tests;

// Edges of VELA008's step shape: what does and does not tell two steps on one trigger apart (image kind, alias case,
// column order and casing, subordinate images, non-literal aliases, and messages declared by type).
public sealed class DuplicateStepTriggerShapeEdgeTests
{
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnUpdate(); IStages OnMerge(); IStages OnMessage<TMessage>(); }
    public interface IStages : IFluentStep { IMode PreOperation(); IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); }
    public interface IConfig : IFluentStep
    {
        IConfig FilterOn(Expression<Func<Demo.Account, object>> attribute, params Expression<Func<Demo.Account, object>>[] more);
        IConfig WithPreImage(params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithPreImage(string alias, params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithPostImage(params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithPostImage(string alias, params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithSubordinatePreImage(params Expression<Func<Demo.Account, object>>[] attributes);
        IConfig WithSubordinatePreImage(string alias, params Expression<Func<Demo.Account, object>>[] attributes);
        void Handle();
    }
}

namespace Vela.Constants
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CustomMessageAttribute : Attribute { public CustomMessageAttribute(string messageName) { } }
}

namespace Demo
{
    using Vela.Steps;
    [Vela.Constants.CustomMessage(""contract_Ping"")] public sealed class Ping { }
    [Vela.Constants.CustomMessage(""CONTRACT_PING"")] public sealed class PingUpper { }
    public class Account { public string Name { get; set; } public string Phone { get; set; } }
    public class Plugin { protected ITrigger RegisterStep() => null!; }
}
";

    private static string InRegister(string first, string second) => Harness + @"
namespace Demo
{
    public class Use : Plugin
    {
        private const string Alias = ""Snap"";
        public void RegisterPluginSteps()
        {
            RegisterStep()." + first + @".Handle();
            RegisterStep()." + second + @".Handle();
        }
    }
}
";

    private static DiagnosticResult Expected(string message = "update") =>
        new DiagnosticResult(DuplicateStepTriggerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments($"message '{message}' at postoperation (sync)");

    [Fact]
    public async Task A_pre_image_and_a_post_image_with_the_same_alias_and_columns_tell_steps_apart()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage(\"Snap\", a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"Snap\", a => a.Name)"));
    }

    [Fact]
    public async Task Aliases_compare_case_insensitively_and_columns_in_any_order()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"Snap\", a => a.Name, a => a.Phone)",
            "{|#0:OnUpdate|}().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"SNAP\", a => a.Phone, a => a.Name)"), Expected());
    }

    [Fact]
    public async Task The_default_post_image_alias_is_PostImage()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name)",
            "{|#0:OnUpdate|}().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"PostImage\", a => a.Name)"), Expected());
    }

    [Fact]
    public async Task Different_image_columns_or_aliases_tell_steps_apart()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"Snap\", a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"Snap\", a => a.Phone)"));
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"One\", a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(\"Two\", a => a.Name)"));
    }

    [Fact]
    public async Task A_subordinate_image_is_not_the_masters_image()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnMerge().PostOperation().Sync().WithPreImage(\"Img\", a => a.Name)",
            "OnMerge().PostOperation().Sync().WithSubordinatePreImage(\"Img\", a => a.Name)"));
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnMerge().PostOperation().Sync().WithSubordinatePreImage(a => a.Name)",
            "{|#0:OnMerge|}().PostOperation().Sync().WithSubordinatePreImage(\"SubordinatePreImage\", a => a.Name)"), Expected("merge"));
    }

    [Fact]
    public async Task A_step_whose_alias_is_not_a_literal_is_not_compared()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(Alias, a => a.Name)",
            "OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(Alias, a => a.Name)"));
    }

    [Fact]
    public async Task Declared_messages_match_whatever_their_case()
    {
        await Verify.VerifyAnalyzerAsync(InRegister(
            "OnMessage<Ping>().PostOperation().Sync()",
            "{|#0:OnMessage<PingUpper>|}().PostOperation().Sync()"), Expected("contract_ping"));
    }
}
