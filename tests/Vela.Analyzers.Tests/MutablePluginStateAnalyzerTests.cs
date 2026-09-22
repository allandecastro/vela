using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.MutablePluginStateAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class MutablePluginStateAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
";

    private static DiagnosticResult Expected(string field) =>
        new DiagnosticResult(Vela.Analyzers.MutablePluginStateAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithArguments(field);

    [Fact]
    public async Task Flags_a_writable_instance_field_on_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private int {|#0:_count|};
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("_count"));
    }

    [Fact]
    public async Task Flags_a_static_writable_field_on_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private static string {|#0:_cache|};
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("_cache"));
    }

    [Fact]
    public async Task Does_not_flag_const_readonly_or_static_readonly()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private const int Max = 5;
        private readonly int _id = 1;
        private static readonly string Name = ""x"";
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_field_on_a_non_plugin_type()
    {
        string code = Harness + @"
namespace Demo
{
    public class Helper
    {
        private int _count;
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
