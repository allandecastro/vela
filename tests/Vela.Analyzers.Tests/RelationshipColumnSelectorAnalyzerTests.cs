using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.RelationshipColumnSelectorAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class RelationshipColumnSelectorAnalyzerTests
{
    // A consumer namespace with the generated [RelationshipSchemaName] attribute and an entity carrying both a
    // real column and a relationship nav; Vela's step builder and typed query with their column selectors; and a
    // LINQ-style method to prove ordinary Select/Where is not flagged.
    private const string Harness = @"
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Acme
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class RelationshipSchemaNameAttribute : Attribute { public RelationshipSchemaNameAttribute(string schemaName) { } }

    public class Contact { public string FirstName { get; set; } }

    public class Account
    {
        public string Name { get; set; }
        [RelationshipSchemaName(""contact_customer_accounts"")]
        public IList<Contact> Contacts { get; set; }
    }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep FilterOn(params Expression<Func<Acme.Account, object>>[] columns);
        IFakeStep WithPreImage(params Expression<Func<Acme.Account, object>>[] columns);
        void Handle();
    }
}

namespace Vela.Querying
{
    public sealed class TypedQuery<TEntity>
    {
        public TypedQuery<TEntity> Select(params Expression<Func<TEntity, object>>[] columns) => this;
        public TypedQuery<TEntity> OrderBy(Expression<Func<TEntity, object>> column) => this;
    }
}

namespace Vela.Context
{
    public static class RetrieveExtensions
    {
        public static T Retrieve<T>(this object service, Guid id, Expression<Func<T, object>> column, params Expression<Func<T, object>>[] columns) => default!;
    }

    public static class AlternateKeyExtensions
    {
        public static T RetrieveByKey<T>(this object service, string key, params Expression<Func<T, object>>[] columns) => default!;
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
        protected Vela.Querying.TypedQuery<Acme.Account> Query() => null!;
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    using System.Linq;
    using System.Collections.Generic;
    public class Use : Plugin
    {
        public void M()
        {
            " + body + @"
        }
    }
}
";

    private static DiagnosticResult Expected(string property, string method) =>
        new DiagnosticResult(Vela.Analyzers.RelationshipColumnSelectorAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(property, method);

    [Fact]
    public async Task Flags_a_relationship_nav_in_FilterOn()
    {
        string code = InMethod("RegisterStep().FilterOn(a => {|#0:a.Contacts|}).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "FilterOn"));
    }

    [Fact]
    public async Task Flags_a_relationship_nav_in_WithPreImage()
    {
        string code = InMethod("RegisterStep().WithPreImage(a => a.Name, a => {|#0:a.Contacts|}).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "WithPreImage"));
    }

    [Fact]
    public async Task Flags_a_relationship_nav_in_a_query_Select()
    {
        string code = InMethod("Query().Select(a => {|#0:a.Contacts|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "Select"));
    }

    [Fact]
    public async Task Flags_a_relationship_nav_in_a_typed_Retrieve()
    {
        string code = InMethod("Vela.Context.RetrieveExtensions.Retrieve<Acme.Account>(this, Guid.NewGuid(), a => a.Name, a => {|#0:a.Contacts|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "Retrieve"));
    }

    [Fact]
    public async Task Flags_a_relationship_nav_in_RetrieveByKey()
    {
        string code = InMethod("Vela.Context.AlternateKeyExtensions.RetrieveByKey<Acme.Account>(this, \"k\", a => {|#0:a.Contacts|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "RetrieveByKey"));
    }

    [Fact]
    public async Task Flags_a_relationship_nav_behind_a_cast()
    {
        string code = InMethod("RegisterStep().FilterOn(a => (object){|#0:a.Contacts|}).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "FilterOn"));
    }

    [Fact]
    public async Task Flags_a_relationship_nav_behind_an_as_expression()
    {
        string code = InMethod("RegisterStep().FilterOn(a => {|#0:a.Contacts|} as object).Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("Contacts", "FilterOn"));
    }

    [Fact]
    public async Task Does_not_flag_a_real_column()
    {
        string code = InMethod("RegisterStep().FilterOn(a => a.Name).Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_ordinary_LINQ_on_a_nav_property()
    {
        // System.Linq.Enumerable.Select is not a Vela selector - the namespace gate must let it through.
        string code = InMethod("var names = new List<Acme.Account>().Select(a => a.Contacts);");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
