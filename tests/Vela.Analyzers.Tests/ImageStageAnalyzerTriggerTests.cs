using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.ImageStageAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA007 for every trigger spelling of Create and Delete (typed, bulk, a type-stated message in any casing), with
// the full message, and never for a same-named chain on a type that is not Vela's builder.
public sealed class ImageStageAnalyzerTriggerTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStep : IFluentStep
    {
        IStep OnMessage<TMessage>();
        IStep OnCreate();
        IStep OnCreateMultiple();
        IStep OnDelete();
        IStep PostOperation();
        IStep WithPreImage(params string[] columns);
        IStep WithPostImage(params string[] columns);
        void Handle();
    }
}

namespace Vela.Messages
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class SdkMessageAttribute : System.Attribute { public SdkMessageAttribute(string messageName) { } }
}

namespace Demo
{
    using Vela.Steps;
    // Message types for the type-stated OnMessage<TMessage>() trigger, in any casing.
    [Vela.Messages.SdkMessage(""Create"")] public sealed class CreateAlias { }
    [Vela.Messages.SdkMessage(""createmultiple"")] public sealed class BulkCreateAlias { }
    [Vela.Messages.SdkMessage(""DELETE"")] public sealed class DeleteAlias { }

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
    [InlineData("OnMessage<CreateAlias>()", "Create")]
    [InlineData("OnMessage<BulkCreateAlias>()", "createmultiple")] // a declared name is reported as declared
    public async Task A_pre_image_on_any_create_trigger_is_reported(string trigger, string message)
    {
        await Verify.VerifyAnalyzerAsync(In("RegisterStep()." + trigger + ".PostOperation().{|#0:WithPreImage|}(\"name\").Handle();"),
            new DiagnosticResult(ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithMessage($"A pre-image is not valid on a {message} step: there is no prior row state before a create"));
    }

    [Theory]
    [InlineData("OnDelete()", "Delete")]
    [InlineData("OnMessage<DeleteAlias>()", "DELETE")]
    public async Task A_post_image_on_any_delete_trigger_is_reported(string trigger, string message)
    {
        await Verify.VerifyAnalyzerAsync(In("RegisterStep()." + trigger + ".PostOperation().{|#0:WithPostImage|}(\"name\").Handle();"),
            new DiagnosticResult(ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithMessage($"A post-image is not valid on a {message} step: the row no longer exists after a delete"));
    }

    [Theory]
    [InlineData("RegisterStep().OnCreate().PostOperation().WithPostImage(\"name\").Handle();")]
    [InlineData("RegisterStep().OnDelete().PostOperation().WithPreImage(\"name\").Handle();")]
    [InlineData("new Lookalike().OnCreate().WithPreImage(\"name\");")]
    public async Task A_valid_image_or_a_look_alike_chain_is_not_reported(string body)
        => await Verify.VerifyAnalyzerAsync(In(body));
}
