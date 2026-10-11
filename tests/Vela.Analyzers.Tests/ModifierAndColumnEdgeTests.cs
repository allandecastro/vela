using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyColumns = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateColumnAnalyzer>;
using VerifyModifiers = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.DuplicateModifierAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA003 covers exactly the single-value modifiers (every one of them, and neither Disabled() nor the image calls);
// VELA005 covers exactly the column lists of FilterOn / WithPreImage / WithPostImage and Vela's typed-query Select,
// never another method that happens to take selectors.
public sealed class ModifierAndColumnEdgeTests
{
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStep : IFluentStep
    {
        IStep FilterOn(params Expression<Func<Acme.Account, object>>[] columns);
        IStep WithPreImage(params Expression<Func<Acme.Account, object>>[] columns);
        IStep WithPostImage(params Expression<Func<Acme.Account, object>>[] columns);
        IStep Include(params Expression<Func<Acme.Account, object>>[] columns);
        IStep WithExecutionOrder(int order);
        IStep WithDescription(string description);
        IStep WithStepKey(string key);
        IStep WithImpersonation(System.Guid user);
        IStep WithSupportedDeployment(int deployment);
        IStep WithUnsecureConfig(string config);
        IStep WithSecureConfigFrom(string name);
        IStep Disabled();
        void Handle();
    }
}

namespace Acme
{
    public class Account { public string Name { get; set; } public string City { get; set; } }
}

namespace Other.Querying
{
    public sealed class Query<T> { public Query<T> Select(params Expression<Func<T, object>>[] columns) => this; }
}

namespace Demo
{
    using Vela.Steps;
    public class Use
    {
        protected IStep RegisterStep() => null!;
        public void M()
        {
            BODY
        }
    }
}
";

    private static string In(string body) => Harness.Replace("BODY", body);

    [Theory]
    [InlineData("FilterOn(a => a.Name)", "FilterOn(a => a.City)", "FilterOn")]
    [InlineData("WithExecutionOrder(1)", "WithExecutionOrder(2)", "WithExecutionOrder")]
    [InlineData("WithDescription(\"a\")", "WithDescription(\"b\")", "WithDescription")]
    [InlineData("WithStepKey(\"a\")", "WithStepKey(\"b\")", "WithStepKey")]
    [InlineData("WithImpersonation(System.Guid.Empty)", "WithImpersonation(System.Guid.Empty)", "WithImpersonation")]
    [InlineData("WithSupportedDeployment(0)", "WithSupportedDeployment(2)", "WithSupportedDeployment")]
    [InlineData("WithUnsecureConfig(\"a\")", "WithUnsecureConfig(\"b\")", "WithUnsecureConfig")]
    [InlineData("WithSecureConfigFrom(\"a\")", "WithSecureConfigFrom(\"b\")", "WithSecureConfigFrom")]
    public async Task Every_single_value_modifier_may_be_set_only_once(string first, string second, string method)
    {
        string secondCall = second.Replace(method + "(", "{|#0:" + method + "|}(");
        await VerifyModifiers.VerifyAnalyzerAsync(In("RegisterStep()." + first + "." + secondCall + ".Handle();"),
            new DiagnosticResult(DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0).WithArguments(method));
    }

    [Theory]
    [InlineData("RegisterStep().Disabled().Disabled().Handle();")]
    [InlineData("RegisterStep().WithPreImage(a => a.Name).WithPreImage(a => a.City).WithPostImage(a => a.Name).WithPostImage(a => a.City).Handle();")]
    public async Task Disabled_and_the_image_calls_may_repeat(string body)
        => await VerifyModifiers.VerifyAnalyzerAsync(In(body));

    [Fact]
    public async Task A_duplicate_post_image_column_is_reported_with_its_name()
    {
        await VerifyColumns.VerifyAnalyzerAsync(In("RegisterStep().WithPostImage(a => a.Name, {|#0:a => a.Name|}).Handle();"),
            new DiagnosticResult(DuplicateColumnAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0).WithArguments("Name"));
    }

    [Theory]
    [InlineData("RegisterStep().Include(a => a.Name, a => a.Name).Handle();")]
    [InlineData("new Other.Querying.Query<Acme.Account>().Select(a => a.Name, a => a.Name);")]
    [InlineData("RegisterStep().FilterOn(null!, null!).Handle();")]
    public async Task Repeated_arguments_outside_a_column_list_are_not_reported(string body)
        => await VerifyColumns.VerifyAnalyzerAsync(In(body));

    [Fact]
    public async Task An_unresolved_Select_is_not_reported()
    {
        await VerifyColumns.VerifyAnalyzerAsync(In("{|#0:Unknown|}.Select((Acme.Account a) => a.Name, (Acme.Account a) => a.Name);"),
            DiagnosticResult.CompilerError("CS0103").WithLocation(0).WithArguments("Unknown"));
    }
}
