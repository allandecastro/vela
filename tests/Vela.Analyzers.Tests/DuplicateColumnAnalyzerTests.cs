using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateColumnAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class DuplicateColumnAnalyzerTests
{
    // Fluent surface with column-list methods that take object-returning selectors, plus a leading alias overload
    // for images, and a tiny entity to select columns from.
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep FilterOn(params Expression<Func<Acme.Account, object>>[] columns);
        IFakeStep WithPreImage(params Expression<Func<Acme.Account, object>>[] columns);
        IFakeStep WithPreImage(string alias, params Expression<Func<Acme.Account, object>>[] columns);
        void Handle();
    }
}

namespace Acme
{
    public class Account
    {
        public string Name { get; set; }
        public string City { get; set; }
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    public class Use : Plugin
    {
        public void M()
        {
            " + body + @"
        }
    }
}
";

    private static DiagnosticResult Expected(string column) =>
        new DiagnosticResult(Vela.Analyzers.DuplicateColumnAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(column);

    [Fact]
    public async Task Flags_a_duplicate_column_in_FilterOn()
    {
        string code = InMethod("RegisterStep().FilterOn(a => a.Name, {|#0:a => a.Name|}).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Name"));
    }

    [Fact]
    public async Task Flags_a_duplicate_column_in_WithPreImage()
    {
        string code = InMethod("RegisterStep().WithPreImage(a => a.Name, a => a.City, {|#0:a => a.Name|}).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Name"));
    }

    [Fact]
    public async Task Ignores_a_leading_alias_string()
    {
        string code = InMethod("RegisterStep().WithPreImage(\"pre\", a => a.Name, a => a.City).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_distinct_columns()
    {
        string code = InMethod("RegisterStep().FilterOn(a => a.Name, a => a.City).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_the_same_column_across_two_separate_calls()
    {
        // The same column in FilterOn and in WithPreImage is legitimate (different concerns).
        string code = InMethod("RegisterStep().FilterOn(a => a.Name).WithPreImage(a => a.Name).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
