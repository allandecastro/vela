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
        public virtual Guid Id { get; set; }
    }

    public class EntityReference
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }
}

namespace Microsoft.Xrm.Sdk.Client
{
    public sealed class AttributeLogicalNameAttribute : Attribute
    {
        public AttributeLogicalNameAttribute(string logicalName) { }
    }
}

namespace Demo
{
    using Microsoft.Xrm.Sdk;

    public class Account : Entity
    {
        public string Name { get; set; }
        public EntityReference PrimaryContactId { get; set; }
        public Account ParentAccount { get; set; }
    }

    // As `vela models` used to emit it: an Id override with no column name.
    public class Lead : Entity
    {
        public override Guid Id { get; set; }
    }

    // As `vela models` emits it now (and the SDK generator does): the override names the primary key column.
    public class Contact : Entity
    {
        [Microsoft.Xrm.Sdk.Client.AttributeLogicalName(""contactid"")]
        public override Guid Id { get; set; }
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
    public async Task Flags_a_nested_path_which_is_not_a_column_of_the_entity()
    {
        // a.PrimaryContactId.Id / a.ParentAccount.Name used to register "id" / "name": a wrong or nonexistent column.
        string code = InMethod("Steps.Filtering<Account>(x => {|#0:x.PrimaryContactId.Id|}, x => {|#1:x.ParentAccount.Name|});");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error).WithLocation(0).WithArguments("x.PrimaryContactId.Id"),
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error).WithLocation(1).WithArguments("x.ParentAccount.Name"));
    }

    [Fact]
    public async Task Flags_an_Id_override_without_a_column_name_but_not_one_with_it()
    {
        string code = InMethod("Steps.Filtering<Lead>(x => x.{|#0:Id|}); Steps.Filtering<Contact>(x => x.Id); Steps.Filtering<Account>(x => x.PrimaryContactId);");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error).WithLocation(0).WithArguments("Id"));
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
