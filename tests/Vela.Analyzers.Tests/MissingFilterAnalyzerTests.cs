using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.MissingFilterAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class MissingFilterAnalyzerTests
{
    // Stand-in for the builder surface: the filtered messages' config interfaces are named like the real ones
    // (I<Message><Stage>Config); Delete is a message that needs no filter.
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStage OnCreate(); IStage OnUpdate(); IStage OnCreateMultiple(); IDeleteStage OnDelete(); }
    public interface IStage : IFluentStep { ICreatePostOperationConfig PostOperation(); IUpdatePreOperationConfig PreOperation(); ICreateMultiplePreValidationConfig PreValidation(); }
    public interface IDeleteStage : IFluentStep { IDeletePostOperationConfig PostOperation(); }
    public interface ICreatePostOperationConfig : IFluentStep { ICreatePostOperationConfig FilterOn(string a); ICreatePostOperationConfig Disabled(); void Handle(System.Action h); }
    public interface IUpdatePreOperationConfig : IFluentStep { IUpdatePreOperationConfig FilterOn(string a); void Handle(System.Action h); }
    public interface ICreateMultiplePreValidationConfig : IFluentStep { ICreateMultiplePreValidationConfig FilterOn(string a); void Handle(System.Action h); }
    public interface IDeletePostOperationConfig : IFluentStep { void Handle(System.Action h); }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected ITrigger RegisterStep() => null!;
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    using Vela.Steps;
    public class Use : Plugin
    {
        private void H() { }
        public void M()
        {
            " + body + @"
        }
    }
}
";

    private static DiagnosticResult Missing(int location, string message) =>
        new DiagnosticResult(MissingFilterAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(location).WithArguments(message);

    [Fact]
    public async Task Flags_create_update_and_bulk_steps_without_FilterOn()
    {
        string code = InMethod(@"
            RegisterStep().OnCreate().PostOperation().Disabled().{|#0:Handle|}(H);
            RegisterStep().OnUpdate().PreOperation().{|#1:Handle|}(H);
            RegisterStep().OnCreateMultiple().PreValidation().{|#2:Handle|}(H);");
        await Verify.VerifyAnalyzerAsync(code, Missing(0, "Create"), Missing(1, "Update"), Missing(2, "CreateMultiple"));
    }

    [Fact]
    public async Task Accepts_a_filtered_step_and_a_message_that_needs_no_filter()
    {
        string code = InMethod(@"
            RegisterStep().OnCreate().PostOperation().FilterOn(""name"").Disabled().Handle(H);
            (RegisterStep().OnUpdate().PreOperation().FilterOn(""name"")).Handle(H);
            RegisterStep().OnDelete().PostOperation().Handle(H);");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Stays_silent_on_a_chain_split_across_a_local()
    {
        // The FilterOn may have been applied before the local was assigned: never a false positive.
        string code = InMethod(@"
            ICreatePostOperationConfig step = RegisterStep().OnCreate().PostOperation().FilterOn(""name"");
            step.Handle(H);");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
