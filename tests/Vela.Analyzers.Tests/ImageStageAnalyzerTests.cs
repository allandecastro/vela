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
        IStages OnMessage<TMessage>() where TMessage : class;
        IStages On<TRequest>();
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

namespace Microsoft.Xrm.Sdk.Messages
{
    public class CreateRequest { }
    public class CreateMultipleRequest { }
    public class UpdateRequest { }
    public class UpdateMultipleRequest { }
    public class DeleteRequest { }
}

namespace Microsoft.Crm.Sdk.Messages
{
    public class RouteToRequest { }
    public class AssignRequest { }
}

namespace Vela.Constants
{
    public sealed class CustomMessageAttribute : System.Attribute { public CustomMessageAttribute(string messageName) { } }
}

namespace Vela.Messages
{
    public sealed class SdkMessageAttribute : System.Attribute { public SdkMessageAttribute(string messageName) { } }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }

    [Vela.Constants.CustomMessage(""adc_DoThing"")]
    public class DoThing { }

    // A generated request whose sdkmessage differs from its name states it with [SdkMessage].
    [Vela.Messages.SdkMessage(""Delete"")]
    public class PurgeRequest { }

    // A project type that merely shares an SDK request's name is not that message.
    public class CreateRequest { }
}
";

    private const string Xrm = "Microsoft.Xrm.Sdk.Messages.";
    private const string Crm = "Microsoft.Crm.Sdk.Messages.";

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
    public async Task Flags_pre_image_on_create_request_type()
    {
        string code = InMethod("RegisterStep().On<" + Xrm + "CreateRequest>().PostOperation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
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
        string code = InMethod("RegisterStep().OnMessage<" + Xrm + "DeleteRequest>().PostOperation().Sync().{|#0:WithPostImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A post-image", "Delete", "the row no longer exists after a delete"));
    }

    // A pre-image is allowed at PreValidation since #436, but never on Create, whatever the stage.
    [Fact]
    public async Task Flags_pre_image_on_create_at_prevalidation_request_type()
    {
        string code = InMethod("RegisterStep().On<" + Xrm + "CreateRequest>().PreValidation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "Create", "there is no prior row state before a create"));
    }

    [Fact]
    public async Task Flags_pre_image_on_create_at_prevalidation_typed_trigger()
    {
        string code = InMethod("RegisterStep().OnCreate().PreValidation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "Create", "there is no prior row state before a create"));
    }

    [Fact]
    public async Task Flags_pre_image_on_create_multiple_request_type()
    {
        string code = InMethod("RegisterStep().On<" + Xrm + "CreateMultipleRequest>().PostOperation().Sync().{|#0:WithPreImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A pre-image", "CreateMultiple", "there is no prior row state before a create"));
    }

    // RouteTo supports no image at all (the platform fails with 0x80044189), whatever the image type.
    [Theory]
    [InlineData("WithPreImage", "A pre-image")]
    [InlineData("WithPostImage", "A post-image")]
    public async Task Flags_any_image_on_route_to(string method, string image)
    {
        string code = InMethod("RegisterStep().OnMessage<" + Crm + "RouteToRequest>().PostOperation().Sync().{|#0:" + method + "|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected(image, "RouteTo", "Dataverse supports no image on this message"));
    }

    [Theory]
    [InlineData("On<" + Xrm + "UpdateMultipleRequest>()", "WithPreImage")]
    [InlineData("On<" + Xrm + "UpdateMultipleRequest>()", "WithPostImage")]
    [InlineData("On<" + Xrm + "CreateMultipleRequest>()", "WithPostImage")]
    public async Task Does_not_flag_bulk_images(string trigger, string method)
    {
        string code = InMethod("RegisterStep()." + trigger + ".PostOperation().Sync()." + method + "(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Theory]
    [InlineData("OnUpdate()")]
    [InlineData("OnDelete()")]
    [InlineData("On<" + Xrm + "UpdateRequest>()")]
    [InlineData("OnMessage<" + Crm + "AssignRequest>()")]
    public async Task Does_not_flag_pre_image_at_prevalidation(string trigger)
    {
        string code = InMethod("RegisterStep()." + trigger + ".PreValidation().Sync().WithPreImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_pre_image_on_update()
    {
        string code = InMethod("RegisterStep().On<" + Xrm + "UpdateRequest>().PostOperation().Sync().WithPreImage(\"name\").Handle();");
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
        string code = InMethod("RegisterStep().OnMessage<DoThing>().PostOperation().Sync().WithPreImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_an_image_on_the_sdkmessage_a_request_type_declares()
    {
        string code = InMethod("RegisterStep().On<PurgeRequest>().PostOperation().Sync().{|#0:WithPostImage|}(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code, Expected("A post-image", "Delete", "the row no longer exists after a delete"));
    }

    [Fact]
    public async Task Does_not_flag_a_project_type_named_like_an_sdk_request()
    {
        string code = InMethod("RegisterStep().On<CreateRequest>().PostOperation().Sync().WithPreImage(\"name\").Handle();");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
