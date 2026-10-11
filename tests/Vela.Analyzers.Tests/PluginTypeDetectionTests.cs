using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.MutablePluginStateAnalyzer>;

namespace Vela.Analyzers.Tests;

// A plugin type is recognised from any one of its anchors, so a project that references Vela but not the SDK's
// IPlugin directly (or the other way round) is still analysed: IPlugin, Vela's PluginBase, EntityPluginBase<T> or
// CustomApiBase each make a derived type a plugin on its own. Checked through VELA011 (mutable plugin state).
public sealed class PluginTypeDetectionTests
{
    [Theory]
    [InlineData("namespace Vela.Plugin { public abstract class PluginBase { } }", "Vela.Plugin.PluginBase")]
    [InlineData("namespace Vela.Plugin { public abstract class EntityPluginBase<T> { } }", "Vela.Plugin.EntityPluginBase<object>")]
    [InlineData("namespace Vela.CustomApi { public abstract class CustomApiBase { } }", "Vela.CustomApi.CustomApiBase")]
    [InlineData("namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }", "Microsoft.Xrm.Sdk.IPlugin")]
    public async Task Each_anchor_alone_makes_a_derived_type_a_plugin(string anchor, string baseType)
    {
        string code = anchor + @"
namespace Demo
{
    public class MyPlugin : " + baseType + @"
    {
        private int {|#0:_count|};
    }

    public class NotAPlugin
    {
        private int _free;
    }
}";

        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(MutablePluginStateAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0).WithArguments("_count", MutablePluginStateAnalyzer.WritableField));
    }
}
