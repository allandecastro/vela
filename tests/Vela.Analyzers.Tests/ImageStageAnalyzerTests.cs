using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.ImageStageAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class ImageStageAnalyzerTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep
    {
        IStages OnMessage(string message);
        IStages OnCreate();
        IStages OnUpdate();
        IStages OnDelete();
    }
    public interface IStages : IFluentStep { IMode PreValidation(); IMode PreOperation(); IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); }
    public interface IConfig : IFluentStep
    {
        IConfig WithPreImage(params string[] columns);
        IConfig WithPostImage(params string[] columns);
        void Handle();
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    public class Use : Plugin { public void M() { " + body + @" } }
}
";

    private static DiagnosticResult Expected(string image, string message, string reason) =>
        new DiagnosticResult(Vela.Analyzers.ImageStageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(image, message, reason);

    [Fact]
    public async Task Flags_pre_image_on_create_permissive()
    {
        string code = InMethod("RegisterStep().OnMessage(\"create\").PostOperation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "Create", "there is no prior row state before a create"));
    }

    [Fact]
    public async Task Flags_pre_image_on_create_typed_trigger()
    {
        string code = InMethod("RegisterStep().OnCreate().PostOperation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "Create", "there is no prior row state before a create"));
    }

    [Fact]
    public async Task Flags_post_image_on_delete()
    {
        string code = InMethod("RegisterStep().OnMessage(\"delete\").PostOperation().Sync().{|#0:WithPostImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A post-image", "Delete", "the row no longer exists after a delete"));
    }

    // A pre-image is allowed at PreValidation since #436, but never on Create, whatever the stage.
    [Fact]
    public async Task Flags_pre_image_on_create_at_prevalidation_permissive()
    {
        string code = InMethod("RegisterStep().OnMessage(\"create\").PreValidation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "Create", "there is no prior row state before a create"));
    }

    [Fact]
    public async Task Flags_pre_image_on_create_at_prevalidation_typed_trigger()
    {
        string code = InMethod("RegisterStep().OnCreate().PreValidation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "Create", "there is no prior row state before a create"));
    }

    [Theory]
    [InlineData("OnUpdate()")]
    [InlineData("OnDelete()")]
    [InlineData("OnMessage(\"update\")")]
    [InlineData("OnMessage(\"assign\")")]
    public async Task Does_not_flag_pre_image_at_prevalidation(string trigger)
    {
        string code = InMethod("RegisterStep()." + trigger + ".PreValidation().Sync().WithPreImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_pre_image_on_update()
    {
        string code = InMethod("RegisterStep().OnMessage(\"update\").PostOperation().Sync().WithPreImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_post_image_on_update()
    {
        string code = InMethod("RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_an_unmodelled_message()
    {
        string code = InMethod("RegisterStep().OnMessage(\"adc_DoThing\").PostOperation().Sync().WithPreImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
