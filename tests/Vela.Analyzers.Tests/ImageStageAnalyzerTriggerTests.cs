using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.ImageStageAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA007 for every trigger spelling of Create and Delete (typed, bulk, permissive in any casing), with the full
// message, and never for a same-named chain on a type that is not Vela's builder.
public sealed class ImageStageAnalyzerTriggerTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStep : IFluentStep
    {
        IStep OnMessage(string message);
        IStep OnCreate();
        IStep OnCreateMultiple();
        IStep OnDelete();
        IStep PostOperation();
        IStep WithPreImage(params string[] columns);
        IStep WithPostImage(params string[] columns);
        void Handle();
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Lookalike
    {
        public Lookalike OnCreate() => this;
        public Lookalike WithPreImage(params string[] columns) => this;
    }

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
    [InlineData("OnCreate()", "Create")]
    [InlineData("OnCreateMultiple()", "CreateMultiple")]
    [InlineData("OnMessage(\"Create\")", "Create")]
    [InlineData("OnMessage(\" createmultiple \")", "CreateMultiple")]
    public async Task A_pre_image_on_any_create_trigger_is_reported(string trigger, string message)
    {
        await Verify.VerifyAnalyzerAsync(In("RegisterStep()." + trigger + ".PostOperation().{|#0:WithPreImage|}(\"name\").Handle();"),
            new DiagnosticResult(ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithMessage($"A pre-image is not valid on a {message} step: there is no prior row state before a create"));
    }

    [Theory]
    [InlineData("OnDelete()")]
    [InlineData("OnMessage(\"DELETE\")")]
    public async Task A_post_image_on_any_delete_trigger_is_reported(string trigger)
    {
        await Verify.VerifyAnalyzerAsync(In("RegisterStep()." + trigger + ".PostOperation().{|#0:WithPostImage|}(\"name\").Handle();"),
            new DiagnosticResult(ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithMessage("A post-image is not valid on a Delete step: the row no longer exists after a delete"));
    }

    [Theory]
    [InlineData("RegisterStep().OnCreate().PostOperation().WithPostImage(\"name\").Handle();")]
    [InlineData("RegisterStep().OnDelete().PostOperation().WithPreImage(\"name\").Handle();")]
    [InlineData("new Lookalike().OnCreate().WithPreImage(\"name\");")]
    public async Task A_valid_image_or_a_look_alike_chain_is_not_reported(string body)
        => await Verify.VerifyAnalyzerAsync(In(body));
}
