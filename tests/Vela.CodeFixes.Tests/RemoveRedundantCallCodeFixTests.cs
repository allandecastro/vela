using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyDup = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.DuplicateModifierAnalyzer,
    Vela.CodeFixes.RemoveRedundantCallCodeFix>;
using VerifyImage = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.ImageStageAnalyzer,
    Vela.CodeFixes.RemoveRedundantCallCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class RemoveRedundantCallCodeFixTests
{
    private const string DupHarness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep WithExecutionOrder(int order);
        IFakeStep FilterOn(params string[] columns);
        IFakeStep FilterOn(params System.Func<Demo.Account, object>[] columns);
        void Handle();
    }
}
namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected IFakeStep RegisterStep() => null!; }
    public class Account { public string Name { get; set; } public string Email { get; set; } }
    public class Use : Plugin { public void M() { BODY } }
}";

    private const string ImageHarness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages On<TRequest>(); }
    public interface IStages : IFluentStep { IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); }
    public interface IConfig : IFluentStep
    {
        IConfig WithPreImage(params string[] columns);
        void Handle();
    }
}
namespace Microsoft.Xrm.Sdk.Messages { public class CreateRequest { } }
namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }
    public class Use : Plugin { public void M() { BODY } }
}";

    [Fact]
    public async Task Removes_a_duplicate_modifier_VELA003()
    {
        string before = DupHarness.Replace("BODY", "RegisterStep().WithExecutionOrder(1).{|#0:WithExecutionOrder|}(2).Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().WithExecutionOrder(1).Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("WithExecutionOrder");
        await VerifyDup.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Removes_all_duplicates_when_a_modifier_repeats_three_times_VELA003()
    {
        // Three calls -> two diagnostics on nested invocation nodes. The fix-all must remove both redundant calls
        // in one rewrite (the BatchFixer would drop one via overlapping ReplaceNode edits).
        string before = DupHarness.Replace("BODY", "RegisterStep().WithExecutionOrder(1).{|#0:WithExecutionOrder|}(2).{|#1:WithExecutionOrder|}(3).Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().WithExecutionOrder(1).Handle();");
        DiagnosticResult first = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("WithExecutionOrder");
        DiagnosticResult second = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(1).WithArguments("WithExecutionOrder");
        await VerifyDup.VerifyCodeFixAsync(before, new[] { first, second }, after);
    }

    [Fact]
    public async Task Removes_an_invalid_image_VELA007()
    {
        string before = ImageHarness.Replace("BODY", "RegisterStep().On<Microsoft.Xrm.Sdk.Messages.CreateRequest>().PostOperation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        string after = ImageHarness.Replace("BODY", "RegisterStep().On<Microsoft.Xrm.Sdk.Messages.CreateRequest>().PostOperation().Sync().Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("A pre-image", "Create", "there is no prior row state before a create");
        await VerifyImage.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Merges_a_duplicate_FilterOn_into_the_first_instead_of_dropping_its_columns_VELA003()
    {
        // A-31: dropping the second FilterOn lost its columns; they move into the first one (a repeat is skipped).
        string before = DupHarness.Replace("BODY", "RegisterStep().FilterOn(\"name\").WithExecutionOrder(1).{|#0:FilterOn|}(\"email\", \"name\").Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().FilterOn(\"name\", \"email\").WithExecutionOrder(1).Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("FilterOn");
        await VerifyDup.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Merges_every_duplicate_FilterOn_in_source_order_VELA003()
    {
        string before = DupHarness.Replace("BODY", "RegisterStep().FilterOn(\"a\").{|#0:FilterOn|}(\"b\").{|#1:FilterOn|}(\"c\").Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().FilterOn(\"a\", \"b\", \"c\").Handle();");
        DiagnosticResult first = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("FilterOn");
        DiagnosticResult second = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(1).WithArguments("FilterOn");
        await VerifyDup.VerifyCodeFixAsync(before, new[] { first, second }, after);
    }

    [Fact]
    public async Task Does_not_merge_a_column_already_selected_under_another_spelling_VELA003()
    {
        // a => a.Name and x => (object)x.Name select the same column; keeping both would introduce VELA005.
        string before = DupHarness.Replace("BODY", "RegisterStep().FilterOn(a => a.Name).{|#0:FilterOn|}(x => (object)x.Name, x => x.Email).Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().FilterOn(a => a.Name, x => x.Email).Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("FilterOn");
        await VerifyDup.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Does_not_merge_a_constant_equal_to_an_already_listed_column_VELA003()
    {
        string before = DupHarness.Replace("BODY", "const string Column = \"name\"; RegisterStep().FilterOn(\"name\").{|#0:FilterOn|}(Column).Handle();");
        string after = DupHarness.Replace("BODY", "const string Column = \"name\"; RegisterStep().FilterOn(\"name\").Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("FilterOn");
        await VerifyDup.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Compares_selector_columns_through_casts_and_parentheses_when_merging_VELA003()
    {
        // (y) => (y.Email) and z => z.Email as object are Email; a => a.Name.Length is not a plain column, kept by text.
        string before = DupHarness.Replace("BODY", "RegisterStep().FilterOn((y) => (y.Email)).{|#0:FilterOn|}(z => z.Email as object, a => a.Name.Length, a => a.Name.Length).Handle();");
        string after = DupHarness.Replace("BODY", "RegisterStep().FilterOn((y) => (y.Email), a => a.Name.Length).Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("FilterOn");
        await VerifyDup.VerifyCodeFixAsync(before, expected, after);
    }

    private static DiagnosticResult DuplicateFilterOn(int location) =>
        new DiagnosticResult(Vela.Analyzers.DuplicateModifierAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(location).WithArguments("FilterOn");

    [Fact]
    public async Task Merges_into_the_first_FilterOn_through_parentheses_and_a_null_forgiving_operator_VELA003()
    {
        string before = DupHarness.Replace("BODY", "(RegisterStep().FilterOn(\"a\"))!.{|#0:FilterOn|}(\"b\").Handle();");
        string after = DupHarness.Replace("BODY", "(RegisterStep().FilterOn(\"a\", \"b\"))!.Handle();");
        await VerifyDup.VerifyCodeFixAsync(before, DuplicateFilterOn(0), after);
    }

    [Fact]
    public async Task Compares_selectors_that_read_no_column_of_their_parameter_by_text_VELA003()
    {
        // A block body, the parameter itself and another variable's member are no column of the parameter: each is
        // kept unless the very same text is already listed.
        string before = DupHarness.Replace("BODY", "var other = new Account(); RegisterStep().FilterOn(a => a, a => other.Name).{|#0:FilterOn|}(a => a, a => other.Name, a => { return a.Name; }, b => other.Name).Handle();");
        string after = DupHarness.Replace("BODY", "var other = new Account(); RegisterStep().FilterOn(a => a, a => other.Name, a => { return a.Name; }, b => other.Name).Handle();");
        await VerifyDup.VerifyCodeFixAsync(before, DuplicateFilterOn(0), after);
    }
}
