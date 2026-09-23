using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.StaticHandlerAnalyzer,
    Vela.CodeFixes.RemoveStaticModifierCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class RemoveStaticModifierCodeFixTests
{
    private const string Preamble = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep { void Handle(System.Action<int> handler); }
}
";

    [Fact]
    public async Task Removes_static_from_the_handler()
    {
        string before = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
        protected static void StaticHandler(int ctx) { }
    }
    public class Use : Plugin
    {
        public void M() => RegisterStep().Handle({|#0:StaticHandler|});
    }
}";

        string after = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
        protected void StaticHandler(int ctx) { }
    }
    public class Use : Plugin
    {
        public void M() => RegisterStep().Handle(StaticHandler);
    }
}";

        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.StaticHandlerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("StaticHandler");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Removes_a_leading_static_keeping_indentation()
    {
        // static is the FIRST of several modifiers, so it carries the method's leading newline/indentation. The
        // fix must transfer that trivia to the new first modifier instead of collapsing the method onto the line
        // above.
        string before = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
        static protected void StaticHandler(int ctx) { }
    }
    public class Use : Plugin
    {
        public void M() => RegisterStep().Handle({|#0:StaticHandler|});
    }
}";

        string after = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
        protected void StaticHandler(int ctx) { }
    }
    public class Use : Plugin
    {
        public void M() => RegisterStep().Handle(StaticHandler);
    }
}";

        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.StaticHandlerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("StaticHandler");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }
}
