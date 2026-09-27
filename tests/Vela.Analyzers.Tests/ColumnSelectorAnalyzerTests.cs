using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.ColumnSelectorAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class ColumnSelectorAnalyzerTests
{
    // A self-contained compilation: a stub Microsoft.Xrm.Sdk.Entity (found by the analyzer via its metadata name),
    // a derived early-bound model with a column property, and a Vela-style column-selector API.
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Microsoft.Xrm.Sdk
{
    public class Entity
    {
        public string LogicalName { get; set; }
        public Guid Id { get; set; }
    }
}

namespace Demo
{
    using Microsoft.Xrm.Sdk;

    public class Account : Entity
    {
        public string Name { get; set; }
    }

    public static class Steps
    {
        public static void Filtering<TEntity>(params Expression<Func<TEntity, object>>[] columns) where TEntity : Entity { }
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    public class Use
    {
        public void M()
        {
            " + body + @"
        }
    }
}
";

    [Fact]
    public async Task Flags_a_base_Entity_member_selector()
    {
        // 'LogicalName' is declared on Entity, so it is not a column -> VELA001.
        string code = InMethod("Steps.Filtering<Account>(x => x.{|#0:LogicalName|});");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("LogicalName"));
    }

    [Fact]
    public async Task Flags_the_Id_base_member()
    {
        string code = InMethod("Steps.Filtering<Account>(x => x.{|#0:Id|});");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("Id"));
    }

    [Fact]
    public async Task Flags_a_base_member_through_an_explicit_cast()   // #309 L1
    {
        // (object)x.LogicalName - the leading cast must not hide the base-Entity member from the analyzer.
        string code = InMethod("Steps.Filtering<Account>(x => (object)x.{|#0:LogicalName|});");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("LogicalName"));
    }

    [Fact]
    public async Task Flags_a_base_member_through_an_as_expression()   // #309 L1
    {
        // x.LogicalName as object - the `as` conversion must not hide the base-Entity member.
        string code = InMethod("Steps.Filtering<Account>(x => x.{|#0:LogicalName|} as object);");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("LogicalName"));
    }

    [Fact]
    public async Task Does_not_flag_a_real_column_property()
    {
        // 'Name' is declared on the derived model -> allowed, no diagnostics.
        string code = InMethod("Steps.Filtering<Account>(x => x.Name);");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_plain_LINQ_Func_selector()
    {
        // A plain Func<...> lambda (not an Expression tree) is not a Vela column selector, even on a base member.
        string extra = @"
namespace Demo
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Xrm.Sdk;

    public class LinqUse
    {
        public void M(IEnumerable<Account> rows)
        {
            var names = rows.Select(x => x.LogicalName).ToList();
        }
    }
}
";
        await Verify.VerifyAnalyzerAsync(Harness + extra);
    }
}
