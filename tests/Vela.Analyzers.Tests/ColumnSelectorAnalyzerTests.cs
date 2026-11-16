using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
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

// As in the SDK: AttributeLogicalNameAttribute lives in Microsoft.Xrm.Sdk (EntityLogicalNameAttribute in .Client).
namespace Microsoft.Xrm.Sdk
{
    public sealed class AttributeLogicalNameAttribute : Attribute
    {
        public AttributeLogicalNameAttribute(string logicalName) { }
    }
}

namespace Microsoft.Xrm.Sdk.Client
{
    public sealed class EntityLogicalNameAttribute : Attribute
    {
        public EntityLogicalNameAttribute(string logicalName) { }
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

    // An unrelated attribute that merely shares the SDK attribute's simple name does not name a column.
    public sealed class AttributeLogicalNameAttribute : Attribute
    {
        public AttributeLogicalNameAttribute(string logicalName) { }
    }

    public class Opportunity : Entity
    {
        [AttributeLogicalName(""opportunityid"")]
        public override Guid Id { get; set; }
    }

    // As `vela models` used to emit it: an Id override with no column name.
    public class Lead : Entity
    {
        public override Guid Id { get; set; }
    }

    // As `vela models` emits it now (and the SDK generator does): the override names the primary key column.
    public class Contact : Entity
    {
        [Microsoft.Xrm.Sdk.AttributeLogicalName(""contactid"")]
        public override Guid Id { get; set; }
    }

    public abstract class AuditedEntity : Entity
    {
        public override Guid Id { get; set; }
    }

    public class Invoice : AuditedEntity
    {
        public override Guid Id { get; set; }
        public decimal TotalAmount { get; set; }
    }

    // A non-Dataverse type that merely has the same member names.
    public class AccountDto
    {
        public string LogicalName { get; set; }
        public string Name { get; set; }
    }

    public static class Mapper
    {
        public static void Map<T>(Expression<Func<T, object>> member) { }
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
        string code = InMethod("Steps.Filtering<Lead>(x => x.{|#0:Id|}); Steps.Filtering<Contact>(x => x.Id); Steps.Filtering<Account>(x => x.PrimaryContactId); Steps.Filtering<Opportunity>(x => x.{|#1:Id|});");
        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error).WithLocation(0).WithArguments("Id"),
            // A look-alike attribute (not Microsoft.Xrm.Sdk's) does not name the column the runtime reads.
            new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error).WithLocation(1).WithArguments("Id"));
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

    private static DiagnosticResult Error(int location, string member) =>
        new DiagnosticResult(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithLocation(location)
            .WithArguments(member);

    [Fact]
    public async Task Flags_a_base_member_in_a_parenthesized_lambda_with_an_explicit_parameter_type()
    {
        string code = InMethod("Steps.Filtering<Account>((Account a) => a.{|#0:LogicalName|}, (a) => a.Name);");
        await Verify.VerifyAnalyzerAsync(code, Error(0, "LogicalName"));
    }

    [Fact]
    public async Task Flags_an_Id_override_inherited_through_an_intermediate_base_without_a_column_name()
    {
        // Invoice.Id overrides AuditedEntity.Id which overrides Entity.Id: still the base member.
        string code = InMethod("Steps.Filtering<Invoice>(x => x.{|#0:Id|}, x => x.TotalAmount);");
        await Verify.VerifyAnalyzerAsync(code, Error(0, "Id"));
    }

    [Fact]
    public async Task Does_not_flag_an_expression_selector_over_a_non_entity_type()
    {
        string code = InMethod("Mapper.Map<AccountDto>(x => x.LogicalName);");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_selector_whose_body_is_not_a_member_access()
    {
        // An indexer or a coalesce is not a simple member selector; the runtime rejects it with its own message.
        string code = InMethod("Steps.Filtering<Account>(x => x.Name ?? string.Empty);");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_selector_on_an_unresolved_member()
    {
        // The compiler already reports the typo; the analyzer stays silent instead of piling on.
        string code = InMethod("Steps.Filtering<Account>(x => x.{|CS1061:Nmae|});");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_block_bodied_LINQ_lambda_reading_a_base_member()
    {
        string extra = @"
namespace Demo
{
    using System.Collections.Generic;
    using System.Linq;

    public class LinqBlockUse
    {
        public List<string> M(IEnumerable<Account> rows) => rows.Select(x => { return x.LogicalName; }).ToList();
    }
}
";
        await Verify.VerifyAnalyzerAsync(Harness + extra);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_the_Dataverse_SDK_is_not_referenced()
    {
        string code = @"
using System;
using System.Linq.Expressions;

namespace Demo
{
    public class Entity { public string LogicalName { get; set; } }
    public class Account : Entity { }
    public static class Steps
    {
        public static void Filtering<TEntity>(params Expression<Func<TEntity, object>>[] columns) where TEntity : Entity { }
    }
    public class Use { public void M() => Steps.Filtering<Account>(x => x.LogicalName); }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    // The models a plugin or test project selects from are compiled into another assembly (the shared project), so
    // the selector's entity comes from metadata: the generated Id override must still be read as the primary key.
    private const string MetadataModels = @"
using System;

namespace Microsoft.Xrm.Sdk
{
    public class Entity
    {
        public string LogicalName { get; set; }
        public virtual Guid Id { get; set; }
        public void SetAttributeValue(string name, object value) { }
    }
}

// As in the SDK: AttributeLogicalNameAttribute lives in Microsoft.Xrm.Sdk (EntityLogicalNameAttribute in .Client).
namespace Microsoft.Xrm.Sdk
{
    public sealed class AttributeLogicalNameAttribute : Attribute
    {
        public AttributeLogicalNameAttribute(string logicalName) { }
    }
}

namespace Microsoft.Xrm.Sdk.Client
{
    public sealed class EntityLogicalNameAttribute : Attribute
    {
        public EntityLogicalNameAttribute(string logicalName) { }
    }
}

namespace Models
{
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    // As `vela models` writes it.
    [EntityLogicalName(""account"")]
    public sealed class Account : Entity
    {
        [AttributeLogicalName(""accountid"")]
        public override Guid Id
        {
            get => base.Id;
            set { base.Id = value; SetAttributeValue(""accountid"", value); }
        }
    }

    public sealed class Lead : Entity
    {
        public override Guid Id { get; set; }
    }
}
";

    private static readonly MetadataReference[] Platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => p.Length > 0)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    private static MetadataReference EmitModels()
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Models",
            [CSharpSyntaxTree.ParseText(MetadataModels)],
            Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream stream = new();
        Assert.True(compilation.Emit(stream).Success);
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    [Fact]
    public async Task Accepts_the_Id_override_of_a_model_compiled_in_another_assembly()
    {
        const string code = @"
using System;
using System.Linq.Expressions;

namespace Demo
{
    public static class Steps
    {
        public static void Filtering<TEntity>(params Expression<Func<TEntity, object>>[] columns) where TEntity : Microsoft.Xrm.Sdk.Entity { }
    }

    public class Use
    {
        public void Account() => Steps.Filtering<Models.Account>(x => x.Id);
        public void Lead() => Steps.Filtering<Models.Lead>(x => x.Id);
        public void Base() => Steps.Filtering<Models.Account>(x => x.LogicalName);
    }
}
";
        CSharpCompilation compilation = CSharpCompilation.Create(
            "consumer",
            [CSharpSyntaxTree.ParseText(code)],
            [.. Platform, EmitModels()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new Vela.Analyzers.ColumnSelectorAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        // The Account override names its column; Lead's does not, and LogicalName is Entity's own member.
        Assert.Equal(
            new[] { "'Id' is not", "'LogicalName' is not" },
            diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Select(d => d.GetMessage().Substring(0, d.GetMessage().IndexOf(" a ", StringComparison.Ordinal))).ToArray());
        Assert.All(diagnostics, d => Assert.Equal(Vela.Analyzers.ColumnSelectorAnalyzer.DiagnosticId, d.Id));
    }
}
