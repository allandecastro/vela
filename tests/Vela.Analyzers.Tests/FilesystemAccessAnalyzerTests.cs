using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.FilesystemAccessAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class FilesystemAccessAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
";

    private static DiagnosticResult Expected(string type) =>
        new DiagnosticResult(Vela.Analyzers.FilesystemAccessAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithArguments(type);

    [Fact]
    public async Task Flags_File_static_call_in_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using System.IO;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp) { var s = {|#0:System.IO.File.ReadAllText|}(""c:\\x.txt""); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("File"));
    }

    [Fact]
    public async Task Flags_Directory_static_call_in_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using System.IO;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp) { var d = {|#0:Directory.GetFiles|}(""c:\\""); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("Directory"));
    }

    [Fact]
    public async Task Flags_new_FileStream_in_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using System.IO;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp) { var fs = new {|#0:FileStream|}(""c:\\x"", FileMode.Open); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("FileStream"));
    }

    [Fact]
    public async Task Does_not_flag_MemoryStream_or_Path_string_helpers_in_a_plugin()
    {
        // In-memory stream and pure-string Path helpers are fine in the sandbox.
        string code = Harness + @"
namespace Demo
{
    using System;
    using System.IO;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp)
        {
            var ms = new MemoryStream();
            var ext = Path.GetExtension(""a/b.txt"");
            var combined = Path.Combine(""a"", ""b"");
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_filesystem_access_outside_a_plugin_type()
    {
        // The rule is scoped to plugin / Custom API types; a plain helper class is not analyzed.
        string code = Harness + @"
namespace Demo
{
    using System.IO;
    public static class Helper
    {
        public static string Read(string p) => File.ReadAllText(p);
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
