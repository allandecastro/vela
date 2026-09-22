using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.RetrieveAllColumnsAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class RetrieveAllColumnsAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk.Query
{
    public class ColumnSet
    {
        public ColumnSet(bool allColumns) { }
        public ColumnSet(params string[] columns) { }
    }
}
";

    private static DiagnosticResult Expected() =>
        new DiagnosticResult(Vela.Analyzers.RetrieveAllColumnsAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0);

    [Fact]
    public async Task Flags_new_ColumnSet_true()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public object M() => {|#0:new ColumnSet(true)|}; }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Does_not_flag_specific_columns()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public object M() => new ColumnSet(""name""); }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_new_ColumnSet_false()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public object M() => new ColumnSet(false); }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
