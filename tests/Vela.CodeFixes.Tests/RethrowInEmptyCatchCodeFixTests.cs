using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.EmptyCatchAnalyzer,
    Vela.CodeFixes.RethrowInEmptyCatchCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class RethrowInEmptyCatchCodeFixTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Demo
{
    using System;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp)
        {
            BODY
        }
    }
}";

    private static string Code(string body) => Harness.Replace("BODY", body);

    private static DiagnosticResult Expected() =>
        new DiagnosticResult(Vela.Analyzers.EmptyCatchAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0);

    [Fact]
    public async Task Rethrows_in_a_single_line_empty_catch()
    {
        await Verify.VerifyCodeFixAsync(Code("try { } {|#0:catch|} { }"), Expected(), Code("try { } catch { throw; }"));
    }

    [Fact]
    public async Task Rethrows_in_a_multi_line_empty_catch()
    {
        string before = Code("try\n            {\n            }\n            {|#0:catch (InvalidOperationException)|}\n            {\n            }");
        string after = Code("try\n            {\n            }\n            catch (InvalidOperationException)\n            {\n                throw;\n            }");
        await Verify.VerifyCodeFixAsync(before, Expected(), after);
    }

    [Fact]
    public async Task Drops_the_unused_exception_variable()
    {
        await Verify.VerifyCodeFixAsync(
            Code("try { } {|#0:catch (Exception ex)|} { }"),
            Expected(),
            Code("try { } catch (Exception) { throw; }"));
    }

    [Fact]
    public async Task Fixes_every_empty_clause_of_one_try()
    {
        DiagnosticResult second = new DiagnosticResult(Vela.Analyzers.EmptyCatchAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(1);
        await Verify.VerifyCodeFixAsync(
            Code("try { } {|#0:catch (ArgumentException)|} { } {|#1:catch|} {\n            }"),
            new[] { Expected(), second },
            Code("try { } catch (ArgumentException) { throw; } catch {\n                throw;\n            }"));
    }

    [Fact]
    public async Task Keeps_the_variable_a_when_filter_reads()
    {
        await Verify.VerifyCodeFixAsync(
            Code("try { } {|#0:catch (Exception ex) when (ex.Message.Length > 0)|} { }"),
            Expected(),
            Code("try { } catch (Exception ex) when (ex.Message.Length > 0) { throw; }"));
    }
}
