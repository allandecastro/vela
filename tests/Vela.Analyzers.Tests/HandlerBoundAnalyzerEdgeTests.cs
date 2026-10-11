using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyAsync = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.AsyncTargetWriteAnalyzer>;
using VerifyHasChanged = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.HasChangedPreImageAnalyzer>;
using VerifyUncaptured = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.UncapturedImageAnalyzer>;

namespace Vela.Analyzers.Tests;

// The analyzers that follow a handler back to the step that binds it (VELA009, VELA017, VELA018) only reason about a
// context used inside a named method: code in a field initializer has no handler to bind, so it is never reported.
// Plus VELA009's capture-all pre-image: WithPreImage() with no column captures every column, so no column check.
public sealed class HandlerBoundAnalyzerEdgeTests
{
    private static string InClass(System.Type testClass, string body) => (string)testClass
        .GetMethod("InClass", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { body })!;

    [Fact]
    public async Task VELA018_ignores_a_Target_write_outside_a_method()
    {
        string code = InClass(typeof(AsyncTargetWriteAnalyzerTests),
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(ctx => { }); }\n" +
            "        private readonly System.Action<ILocalPluginContext> write = ctx => { ctx.Request.Target[\"name\"] = \"x\"; };");

        await VerifyAsync.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task VELA009_ignores_a_HasChanged_outside_a_method()
    {
        string code = InClass(typeof(HasChangedPreImageAnalyzerTests),
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(ctx => { }); }\n" +
            "        private readonly System.Func<ILocalPluginContext, bool> check = ctx => ctx.HasChanged(x => x);");

        await VerifyHasChanged.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task VELA009_accepts_any_column_of_a_capture_all_pre_image()
    {
        // Drop the string-column overload so WithPreImage() binds the selector overload unambiguously.
        string code = InClass(typeof(HasChangedPreImageAnalyzerTests),
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.HasChanged<Account>(a => a.Name); ctx.HasAnyChanged<Account>(a => a.Email); }")
            .Replace("IConfig WithPreImage(params string[] columns);", string.Empty);

        await VerifyHasChanged.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task VELA017_ignores_an_image_read_outside_a_method()
    {
        string code = InClass(typeof(UncapturedImageAnalyzerTests),
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle(ctx => { }); }\n" +
            "        private readonly System.Func<ILocalPluginContext, object?> read = ctx => ctx.PreImage;");

        await VerifyUncaptured.VerifyAnalyzerAsync(code);
    }
}
