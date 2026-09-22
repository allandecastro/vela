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
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }
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
}
