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

    private static DiagnosticResult Static(string handler) =>
        new DiagnosticResult(Vela.Analyzers.StaticHandlerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments(handler);

    [Fact]
    public async Task Removes_static_from_a_handler_of_the_calling_class()
    {
        string before = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Use
    {
        private IFakeStep RegisterStep() => null!;
        public void M() => RegisterStep().Handle({|#0:OnCreated|});
        private static void OnCreated(int ctx) { }
    }
}";
        string after = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Use
    {
        private IFakeStep RegisterStep() => null!;
        public void M() => RegisterStep().Handle(OnCreated);
        private void OnCreated(int ctx) { }
    }
}";
        await Verify.VerifyCodeFixAsync(before, Static("OnCreated"), after);
    }

    [Fact]
    public async Task No_fix_for_a_handler_of_another_class()
    {
        // Handlers.OnCreated is a static helper of another (static) class: removing static there would not compile,
        // and the same-named OnCreated of the calling class must not be edited instead.
        string source = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public static class Handlers { public static void OnCreated(int ctx) { } }
    public class Use
    {
        private IFakeStep RegisterStep() => null!;
        public void M() => RegisterStep().Handle({|#0:Handlers.OnCreated|});
        private static void OnCreated(string unrelated) { }
    }
}";
        await Verify.VerifyCodeFixAsync(source, Static("OnCreated"), source);
    }

    [Fact]
    public async Task No_fix_when_the_call_is_in_a_static_member()
    {
        string source = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    public class Use
    {
        private static IFakeStep Step() => null!;
        public static void M() => Step().Handle({|#0:OnCreated|});
        private static void OnCreated(int ctx) { }
    }
}";
        await Verify.VerifyCodeFixAsync(source, Static("OnCreated"), source);
    }

    [Fact]
    public async Task No_fix_when_the_handler_is_imported_with_using_static()
    {
        string source = Preamble + @"
namespace Demo
{
    using Vela.Steps;
    using static Demo.Handlers;
    public class Handlers { public static void OnCreated(int ctx) { } }
    public class Use
    {
        private IFakeStep RegisterStep() => null!;
        public void M() => RegisterStep().Handle({|#0:OnCreated|});
    }
}";
        await Verify.VerifyCodeFixAsync(source, Static("OnCreated"), source);
    }
}
