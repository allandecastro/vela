using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyFilesystem = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.FilesystemAccessAnalyzer>;
using VerifyRelationship = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.RelationshipColumnSelectorAnalyzer>;

namespace Vela.Analyzers.Tests;

// The vocabularies two analyzers match on: every column-selector method of Vela's builder and typed query rejects a
// relationship navigation (VELA010), and every System.IO filesystem type is flagged in a plugin (VELA019), while
// the same names outside System.IO are not.
public sealed class SelectorAndFilesystemVocabularyTests
{
    private const string RelationshipHarness = @"
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Acme
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class RelationshipSchemaNameAttribute : Attribute { public RelationshipSchemaNameAttribute(string schemaName) { } }
    public class Contact { }
    public class Account
    {
        public string Name { get; set; }
        [RelationshipSchemaName(""contact_customer_accounts"")]
        public IList<Contact> Contacts { get; set; }
    }
}

namespace Vela.Steps { public interface IFluentStep { } }

namespace Vela.Querying
{
    public sealed class TypedQuery<T>
    {
        public TypedQuery<T> FilterOn(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> Select(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> Where(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> WhereAll(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> WhereAny(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> WhereColumns(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> OrderBy(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> OrderByDescending(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> WithPreImage(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> WithPostImage(Expression<Func<T, object>> c) => this;
        public TypedQuery<T> Include(Expression<Func<T, object>> c) => this;
    }
}

namespace Demo
{
    public class Use
    {
        public void M(Vela.Querying.TypedQuery<Acme.Account> q)
        {
            BODY
        }
    }
}
";

    [Theory]
    [InlineData("FilterOn")]
    [InlineData("Select")]
    [InlineData("Where")]
    [InlineData("WhereAll")]
    [InlineData("WhereAny")]
    [InlineData("WhereColumns")]
    [InlineData("OrderBy")]
    [InlineData("OrderByDescending")]
    [InlineData("WithPreImage")]
    [InlineData("WithPostImage")]
    public async Task A_relationship_in_any_column_selector_method_is_reported(string method)
    {
        await VerifyRelationship.VerifyAnalyzerAsync(RelationshipHarness.Replace("BODY", "q." + method + "(a => {|#0:a.Contacts|});"),
            new DiagnosticResult(RelationshipColumnSelectorAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0).WithArguments("Contacts", method));
    }

    [Fact]
    public async Task A_relationship_in_Include_is_not_a_column_selector()
        => await VerifyRelationship.VerifyAnalyzerAsync(RelationshipHarness.Replace("BODY", "q.Include(a => a.Contacts);"));

    private const string PluginHarness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Lookalike
{
    public class FileStream { public FileStream(string path) { } }
    public static class File { public static string ReadAllText(string path) => path; }
}
namespace Demo
{
    using System;
    using System.IO;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp)
        {
            BODY
        }
    }
}
";

    [Theory]
    [InlineData("var x = new {|#0:FileStream|}(\"p\", FileMode.Open);", "FileStream")]
    [InlineData("var x = new {|#0:FileInfo|}(\"p\");", "FileInfo")]
    [InlineData("var x = new {|#0:DirectoryInfo|}(\"p\");", "DirectoryInfo")]
    [InlineData("var x = new {|#0:DriveInfo|}(\"c\");", "DriveInfo")]
    [InlineData("var x = new {|#0:FileSystemWatcher|}(\"p\");", "FileSystemWatcher")]
    [InlineData("var x = {|#0:File.Exists|}(\"p\");", "File")]
    [InlineData("var x = {|#0:Directory.Exists|}(\"p\");", "Directory")]
    public async Task Every_filesystem_type_is_reported_in_a_plugin(string body, string type)
    {
        await VerifyFilesystem.VerifyAnalyzerAsync(PluginHarness.Replace("BODY", body),
            new DiagnosticResult(FilesystemAccessAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0)
                .WithMessage($"'{type}' accesses the filesystem, which the Dataverse sandbox denies - it throws at runtime"));
    }

    [Theory]
    [InlineData("var x = new Lookalike.FileStream(\"p\");")]
    [InlineData("var x = Lookalike.File.ReadAllText(\"p\");")]
    [InlineData("var x = new MemoryStream();")]
    public async Task A_same_named_type_outside_System_IO_or_an_in_memory_stream_is_not_reported(string body)
        => await VerifyFilesystem.VerifyAnalyzerAsync(PluginHarness.Replace("BODY", body));
}
