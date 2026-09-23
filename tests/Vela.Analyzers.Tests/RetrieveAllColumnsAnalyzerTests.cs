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
        public bool AllColumns { get; set; }
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

    [Fact]
    public async Task Flags_named_argument_allColumns_true()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public object M() => {|#0:new ColumnSet(allColumns: true)|}; }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_target_typed_new_true()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public ColumnSet M() => {|#0:new(true)|}; }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_object_initializer_AllColumns_true()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public ColumnSet M() => {|#0:new ColumnSet { AllColumns = true }|}; }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_target_typed_new_with_AllColumns_initializer()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public ColumnSet M() => {|#0:new() { AllColumns = true }|}; }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Does_not_flag_object_initializer_AllColumns_false()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public ColumnSet M() => new ColumnSet { AllColumns = false }; }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_same_named_type_in_another_namespace()
    {
        string code = @"
namespace Other { public class ColumnSet { public ColumnSet(bool all) { } } }
namespace Demo
{
    using Other;
    public class Use { public object M() => new ColumnSet(true); }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
