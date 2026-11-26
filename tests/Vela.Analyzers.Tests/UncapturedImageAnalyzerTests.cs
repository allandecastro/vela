using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.UncapturedImageAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class UncapturedImageAnalyzerTests
{
    private const string Harness = @"
using System;

namespace Vela.Context
{
    public interface ILocalPluginContext
    {
        object? PreImage { get; }
        object? PostImage { get; }
        object? SubordinatePreImage { get; }
        object? SubordinatePostImage { get; }
    }
    public interface IUpdatePostContext<TEntity> : ILocalPluginContext { }
    // Stands in for a generated typed step context: the handler binds through Handle(Delegate).
    public interface IGeneratedContext : ILocalPluginContext { }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnCreate(); IStages OnUpdate(); IStages OnMerge(); }
    public interface IStages : IFluentStep { IMode PreValidation(); IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); }
    public interface IConfig : IFluentStep
    {
        IConfig WithPreImage(params string[] columns);
        IConfig WithPostImage(params string[] columns);
        IConfig WithSubordinatePreImage(params string[] columns);
        IConfig WithSubordinatePostImage(params string[] columns);
        void Handle(Action<Vela.Context.ILocalPluginContext> handler);
        void Handle(Delegate handler);
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; protected ITrigger Step => RegisterStep(); }
    public sealed class Account { }
}
";

    private static string InClass(string body) => Harness + @"
namespace Demo
{
    using Vela.Context;
    public class Use : Plugin
    {
        " + body + @"
    }
}
";

    private static DiagnosticResult Expected(string imageKind, string property, string withCall) =>
        new DiagnosticResult(Vela.Analyzers.UncapturedImageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(imageKind, property, withCall);

    [Fact]
    public async Task Flags_PreImage_when_the_step_captures_no_pre_image()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.{|#0:PreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("pre-image", "PreImage", "WithPreImage"));
    }

    [Fact]
    public async Task Flags_a_subordinate_image_the_merge_step_does_not_capture()   // #462
    {
        // The master's pre-image does not populate the subordinate's.
        string code = InClass(
            "public void Reg() { RegisterStep().OnMerge().PostOperation().Sync().WithPreImage(\"name\").WithSubordinatePostImage(\"name\").Handle(OnMerged); }\n" +
            "        private void OnMerged(ILocalPluginContext ctx) { object? a = ctx.PreImage; object? b = ctx.SubordinatePostImage; object? c = ctx.{|#0:SubordinatePreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("subordinate pre-image", "SubordinatePreImage", "WithSubordinatePreImage"));
    }

    [Fact]
    public async Task Flags_PostImage_when_the_step_captures_no_post_image()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.{|#0:PostImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("post-image", "PostImage", "WithPostImage"));
    }

    [Fact]
    public async Task Does_not_flag_PreImage_when_captured()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PreImage; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_PostImage_when_captured()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PostImage; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_PreImage_captured_at_prevalidation()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PreValidation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PreImage; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_PreImage_not_captured_at_prevalidation()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PreValidation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.{|#0:PreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("pre-image", "PreImage", "WithPreImage"));
    }

    // Create never has a pre-image (no capture is possible), so reading one at PreValidation is still flagged.
    [Fact]
    public async Task Flags_PreImage_on_a_create_prevalidation_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnCreate().PreValidation().Sync().Handle(OnCreate); }\n" +
            "        private void OnCreate(ILocalPluginContext ctx) { object? x = ctx.{|#0:PreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("pre-image", "PreImage", "WithPreImage"));
    }

    [Fact]
    public async Task Flags_PreImage_through_a_null_conditional_access()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { var x = ctx?.{|#0:PreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("pre-image", "PreImage", "WithPreImage"));
    }

    [Fact]
    public async Task Does_not_flag_when_no_owning_chain_binds_the_handler()
    {
        string code = InClass(
            "private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PreImage; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_PreImage_read_through_a_typed_message_context_cast()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { var typed = (IUpdatePostContext<Account>)ctx; object? x = typed.{|#0:PreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("pre-image", "PreImage", "WithPreImage"));
    }

    [Fact]
    public async Task Flags_PostImage_when_the_step_is_started_from_a_property()
    {
        string code = InClass(
            "public void Reg() { this.Step.OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.{|#0:PostImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("post-image", "PostImage", "WithPostImage"));
    }

    [Fact]
    public async Task Does_not_flag_a_handler_shared_by_two_steps()
    {
        // Only one of the two chains captures the pre-image: ambiguous at the read site, so stay silent.
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PreImage; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_PreImage_members_on_non_context_types()
    {
        string code = Harness + @"
namespace Demo
{
    using Vela.Context;
    public sealed class AuditRecord { public object? PreImage { get; set; } public object? PostImage; }

    public class Use : Plugin
    {
        public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }
        private void OnUpd(ILocalPluginContext ctx)
        {
            var audit = new AuditRecord();
            object? before = audit.PreImage;
            object? after = audit.PostImage;
        }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_PreImage_inside_a_lambda_handler_registered_from_a_constructor()
    {
        // Conservative: lambda handlers outside a method cannot be bound to a chain by name.
        string code = InClass(
            "public Use() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(ctx => { object? x = ctx.PreImage; }); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_Vela_is_not_referenced()
    {
        string code = @"
namespace Demo
{
    public sealed class Snapshot { public object? PreImage { get; set; } }
    public sealed class Use { public object? Run(Snapshot s) => s.PreImage; }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_an_uncaptured_image_in_a_handler_bound_through_Handle_Delegate()
    {
        // A handler taking the generated typed context binds to Handle(Delegate): Roslyn reports the method only as a
        // candidate, which the analyzer used to ignore.
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(IGeneratedContext ctx) { object? x = ctx.{|#0:PreImage|}; }");
        await Verify.VerifyAnalyzerAsync(code, Expected("pre-image", "PreImage", "WithPreImage"));
    }

    // A plugin split across files: the RegisterStep() chain in one part of the partial class, the handler in another.
    private static async Task VerifyPartsAsync(string registration, string handler, params DiagnosticResult[] expected)
    {
        var test = new Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<Vela.Analyzers.UncapturedImageAnalyzer, DefaultVerifier>();
        test.TestState.Sources.Add(("Registration.cs", Harness + @"
namespace Demo
{
    using Vela.Context;
    public partial class Use : Plugin
    {
        " + registration + @"
    }
}
"));
        test.TestState.Sources.Add(("Handlers.cs", @"
namespace Demo
{
    using Vela.Context;
    public partial class Use
    {
        " + handler + @"
    }
}
"));
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }

    [Fact]
    public async Task Flags_an_uncaptured_image_when_the_handler_is_in_another_part_of_a_partial_class()
    {
        await VerifyPartsAsync(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(OnUpd); }",
            "private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.{|#0:PreImage|}; }",
            Expected("pre-image", "PreImage", "WithPreImage"));
    }

    [Fact]
    public async Task Does_not_flag_an_image_captured_by_a_chain_in_another_part_of_a_partial_class()
    {
        await VerifyPartsAsync(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }",
            "private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PreImage; }");
    }

    [Fact]
    public async Task Does_not_flag_a_handler_shared_by_chains_in_two_parts_of_a_partial_class()
    {
        // One chain per file: neither file alone shows the handler as shared, the type as a whole does.
        await VerifyPartsAsync(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }",
            "public void Reg2() { RegisterStep().OnCreate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.PreImage; }");
    }
}
