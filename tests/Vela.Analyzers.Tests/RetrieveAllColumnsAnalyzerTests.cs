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
namespace Microsoft.Xrm.Sdk { public interface IPlugin { void Execute(System.IServiceProvider sp); } }
namespace Demo
{
    public abstract class PluginShape : Microsoft.Xrm.Sdk.IPlugin { public void Execute(System.IServiceProvider sp) { } }
}
";

    private static DiagnosticResult Expected(int location = 0) =>
        new DiagnosticResult(Vela.Analyzers.RetrieveAllColumnsAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(location);

    [Fact]
    public async Task Flags_new_ColumnSet_true()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use : PluginShape { public object M() => {|#0:new ColumnSet(true)|}; }
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
    public class Use : PluginShape { public object M() => new ColumnSet(""name""); }
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
    public class Use : PluginShape { public object M() => new ColumnSet(false); }
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
    public class Use : PluginShape { public object M() => {|#0:new ColumnSet(allColumns: true)|}; }
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
    public class Use : PluginShape { public ColumnSet M() => {|#0:new(true)|}; }
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
    public class Use : PluginShape { public ColumnSet M() => {|#0:new ColumnSet { AllColumns = true }|}; }
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
    public class Use : PluginShape { public ColumnSet M() => {|#0:new() { AllColumns = true }|}; }
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
    public class Use : PluginShape { public ColumnSet M() => new ColumnSet { AllColumns = false }; }
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

    [Fact]
    public async Task Flags_a_const_bool_argument_or_initializer()
    {
        // #448: the argument is read as a constant, so a const bool (or a constant expression) does not bypass it.
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use : PluginShape
    {
        private const bool All = true;
        public object A() => {|#0:new ColumnSet(All)|};
        public object B() => {|#1:new ColumnSet { AllColumns = All }|};
        public object C() => {|#2:new ColumnSet(!false)|};
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected(0), Expected(1), Expected(2));
    }

    [Fact]
    public async Task Does_not_flag_a_non_constant_bool()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use : PluginShape
    {
        private static bool _all = true;
        public object M(bool all) => new ColumnSet(all) { AllColumns = _all };
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_code_outside_a_plugin_type()
    {
        // #448: scoped like VELA019, so a test class or a tool in the same solution is left alone - including a type
        // nested in a plugin, which is not a plugin itself.
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class PluginTests { public object M() => new ColumnSet(true); }
    public class Use : PluginShape
    {
        private sealed class Nested { public object M() => new ColumnSet(true); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_neither_the_SDK_nor_Vela_is_referenced()
    {
        string code = @"
namespace Microsoft.Xrm.Sdk.Query { public class ColumnSet { public ColumnSet(bool allColumns) { } } }
namespace Demo
{
    using Microsoft.Xrm.Sdk.Query;
    public class Use { public object M() => new ColumnSet(true); }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
