using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.SingleStepPerTypedContextHandlerAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA016 counts a step as image-capturing when either image call lists a column selector, also after an alias.
public sealed class SingleStepPerTypedContextHandlerEdgeTests
{
    private static string InPlugin(string body)
    {
        string code = (string)typeof(SingleStepPerTypedContextHandlerAnalyzerTests)
            .GetMethod("InPlugin", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { body })!;
        return code.Replace(
            "IStepConfig<TEntity> WithPostImage(params Expression<Func<TEntity, object>>[] a);",
            "IStepConfig<TEntity> WithPostImage(params Expression<Func<TEntity, object>>[] a);\n" +
            "        IStepConfig<TEntity> WithPostImage(string alias, params Expression<Func<TEntity, object>>[] a);\n" +
            "        IStepConfig<TEntity> WithPreImage(params Expression<Func<TEntity, object>>[] a);");
    }

    private static DiagnosticResult At(int marker) =>
        new DiagnosticResult(SingleStepPerTypedContextHandlerAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(marker).WithArguments("Shared");

    [Fact]
    public async Task An_aliased_capture_counts_as_a_typed_image()
    {
        string code = InPlugin(@"
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(""Snap"", a => a.Name).Handle({|#0:Shared|});
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(""Snap"", a => a.Name).Handle({|#1:Shared|});");

        await Verify.VerifyAnalyzerAsync(code, At(0), At(1));
    }

    [Fact]
    public async Task A_pre_image_capture_counts_as_a_typed_image()
    {
        string code = InPlugin(@"
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage(a => a.Name).Handle({|#0:Shared|});
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage(a => a.Name).Handle({|#1:Shared|});");

        await Verify.VerifyAnalyzerAsync(code, At(0), At(1));
    }
}
