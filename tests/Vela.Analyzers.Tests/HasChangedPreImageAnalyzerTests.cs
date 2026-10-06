using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.HasChangedPreImageAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class HasChangedPreImageAnalyzerTests
{
    private const string Harness = @"
using System;

namespace Vela.Context
{
    public interface ILocalPluginContext { }
    public interface IGeneratedContext : ILocalPluginContext { }
    public static class Changes
    {
        public static bool HasChanged(this ILocalPluginContext ctx, Func<object, object> attribute, string preImageAlias = ""PreImage"") => false;
        public static bool HasAnyChanged(this ILocalPluginContext ctx, params Func<object, object>[] attributes) => false;
    }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnUpdate(); }
    public interface IStages : IFluentStep { IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); }
    public interface IConfig : IFluentStep
    {
        IConfig WithPreImage(params string[] columns);
        void Handle(Action<Vela.Context.ILocalPluginContext> handler);
        void Handle(Delegate handler);
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; protected ITrigger Step => RegisterStep(); }
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

    private static DiagnosticResult Expected(string method) =>
        new DiagnosticResult(Vela.Analyzers.HasChangedPreImageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(method);

    [Fact]
    public async Task Flags_HasChanged_when_the_owning_step_has_no_pre_image()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.{|#0:HasChanged|}(x => x); }");
        await Verify.VerifyAnalyzerAsync(code, Expected("HasChanged"));
    }

    [Fact]
    public async Task Flags_HasAnyChanged_too()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.{|#0:HasAnyChanged|}(x => x); }");
        await Verify.VerifyAnalyzerAsync(code, Expected("HasAnyChanged"));
    }

    [Fact]
    public async Task Does_not_flag_when_the_step_registers_a_pre_image()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.HasChanged(x => x); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_custom_alias()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.HasChanged(x => x, \"custom\"); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_when_no_owning_chain_binds_the_handler()
    {
        // Handler is never registered by a chain in this class -> cannot correlate -> skip.
        string code = InClass(
            "private void OnUpd(ILocalPluginContext ctx) { ctx.HasChanged(x => x); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_HasAnyChanged_called_in_static_form()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { if (Changes.{|#0:HasAnyChanged|}(ctx, x => x)) { } }");
        await Verify.VerifyAnalyzerAsync(code, Expected("HasAnyChanged"));
    }

    [Fact]
    public async Task Flags_HasChanged_when_the_step_is_started_from_a_property()
    {
        string code = InClass(
            "public void Reg() { this.Step.OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.{|#0:HasChanged|}(x => x); }");
        await Verify.VerifyAnalyzerAsync(code, Expected("HasChanged"));
    }

    [Fact]
    public async Task Does_not_flag_a_handler_shared_by_two_steps()
    {
        // Two chains own the handler: which one runs at the call site is ambiguous, so stay silent.
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(\"name\").Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.HasChanged(x => x); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_HasChanged_methods_on_non_context_types()
    {
        string code = Harness + @"
namespace Demo
{
    using Vela.Context;
    public sealed class AuditDiff { public bool HasChanged(string column) => false; }
    public static class ChangeFeed { public static bool HasChanged() => false; }
    public static class AuditDiffExtensions { public static bool HasAnyChanged(this AuditDiff diff, params string[] columns) => false; }

    public class Use : Plugin
    {
        private readonly AuditDiff _diff = new AuditDiff();
        public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }
        private void OnUpd(ILocalPluginContext ctx)
        {
            bool a = _diff.HasChanged(""name"");
            bool b = ChangeFeed.HasChanged();
            bool c = _diff.HasAnyChanged(""name"", ""revenue"");
        }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_HasChanged_inside_a_lambda_handler_registered_from_a_constructor()
    {
        // Conservative: lambda handlers outside a method cannot be bound to a chain by name.
        string code = InClass(
            "public Use() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(ctx => { ctx.HasChanged(x => x); }); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_Vela_is_not_referenced()
    {
        string code = @"
namespace Demo
{
    public sealed class Tracker { public bool HasChanged(string column) => false; }
    public sealed class Use { public bool Run(Tracker t) => t.HasChanged(""name""); }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_HasChanged_in_a_handler_bound_through_Handle_Delegate()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(IGeneratedContext ctx) { ctx.{|#0:HasChanged|}(x => x); }");
        await Verify.VerifyAnalyzerAsync(code, Expected("HasChanged"));
    }

    [Fact]
    public async Task Flags_the_static_call_form_of_HasChanged_without_an_alias()
    {
        // Changes.HasChanged(ctx, selector): the context is the first argument, not an explicit alias.
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { Changes.{|#0:HasChanged|}(ctx, x => x); }");
        await Verify.VerifyAnalyzerAsync(code, Expected("HasChanged"));
    }

    [Fact]
    public async Task Skips_the_static_call_form_with_an_explicit_alias()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { Changes.HasChanged(ctx, x => x, \"custom\"); }");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
