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
        public Account Parent { get; set; }
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
    public async Task Does_not_flag_a_nested_path_as_a_duplicate_of_a_direct_column()   // #309 L2
    {
        // a.Name is a column; a.Parent.Name reads the Name of a RELATED entity. They share the leaf name but are
        // different columns - comparing by simple name alone wrongly flagged them as duplicates.
        string code = InMethod("RegisterStep().FilterOn(a => a.Name, a => a.Parent.Name).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
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

    // Typed-query surface: Vela.Querying.Query.For<T>().Select(...). IFluentStep must still exist so the analyzer's
    // compilation-start gate runs; the Select column list itself is recognised by its Vela.Querying namespace.
    private const string QueryHarness = @"
using System;
using System.Linq.Expressions;

namespace Vela.Steps
{
    public interface IFluentStep { }
}

namespace Acme
{
    public class Account
    {
        public string Name { get; set; }
        public string City { get; set; }
        public Account Parent { get; set; }
    }
}

namespace Vela.Querying
{
    public sealed class TypedQuery<TEntity>
    {
        public TypedQuery<TEntity> Select(params Expression<Func<TEntity, object>>[] columns) => this;
    }

    public static class Query
    {
        public static TypedQuery<TEntity> For<TEntity>() => new TypedQuery<TEntity>();
    }
}
";

    private static string InQueryMethod(string body) => QueryHarness + @"
namespace Demo
{
    using Vela.Querying;
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
    public async Task Flags_a_duplicate_column_in_typed_query_Select()
    {
        string code = InQueryMethod("Query.For<Acme.Account>().Select(c => c.Name, {|#0:c => c.Name|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("Name"));
    }

    [Fact]
    public async Task Does_not_flag_distinct_columns_in_typed_query_Select()
    {
        string code = InQueryMethod("Query.For<Acme.Account>().Select(c => c.Name, c => c.City);");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
