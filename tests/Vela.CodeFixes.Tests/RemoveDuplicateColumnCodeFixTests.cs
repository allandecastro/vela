using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.DuplicateColumnAnalyzer,
    Vela.CodeFixes.RemoveDuplicateColumnCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class RemoveDuplicateColumnCodeFixTests
{
    private const string Harness = @"
using System;
using System.Linq.Expressions;
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep FilterOn(params Expression<Func<Acme.Account, object>>[] columns);
        void Handle();
    }
}
namespace Acme { public class Account { public string Name { get; set; } public string City { get; set; } } }
namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected IFakeStep RegisterStep() => null!; }
    public class Use : Plugin { public void M() { BODY } }
}";

    [Fact]
    public async Task Removes_the_duplicate_column()
    {
        string before = Harness.Replace("BODY", "RegisterStep().FilterOn(a => a.Name, {|#0:a => a.Name|}).Handle();");
        string after = Harness.Replace("BODY", "RegisterStep().FilterOn(a => a.Name).Handle();");
        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.DuplicateColumnAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("Name");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }
}
