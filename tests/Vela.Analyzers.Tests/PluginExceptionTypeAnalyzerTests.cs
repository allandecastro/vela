using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.PluginExceptionTypeAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class PluginExceptionTypeAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk
{
    public interface IPlugin { }
    public class InvalidPluginExecutionException : System.Exception
    {
        public InvalidPluginExecutionException(string message) : base(message) { }
    }
}
";

    private static DiagnosticResult Expected(string type) =>
        new DiagnosticResult(Vela.Analyzers.PluginExceptionTypeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithArguments(type);

    [Fact]
    public async Task Flags_a_non_ipee_throw_in_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp) { throw new {|#0:InvalidOperationException|}(""bad""); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("InvalidOperationException"));
    }

    [Fact]
    public async Task Does_not_flag_InvalidPluginExecutionException()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp) { throw new InvalidPluginExecutionException(""bad""); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_rethrow()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp)
        {
            try { } catch (Exception ex) { throw; }
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_subclass_of_InvalidPluginExecutionException()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using Microsoft.Xrm.Sdk;
    public class MyPluginException : InvalidPluginExecutionException { public MyPluginException(string m) : base(m) { } }
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp) { throw new MyPluginException(""bad""); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_throw_outside_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    public class Helper
    {
        public void M() { throw new InvalidOperationException(""x""); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
