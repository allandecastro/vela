using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.SetStateStepAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class SetStateStepAnalyzerTests
{
    private const string Harness = @"
namespace Vela.Messages
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class SdkMessageAttribute : System.Attribute { public SdkMessageAttribute(string messageName) { } }
}

namespace Vela.Constants
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class CustomMessageAttribute : System.Attribute { public CustomMessageAttribute(string messageName) { } }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep On<TRequest>();
        IFakeStep OnMessage<TMessage>();
        void Handle();
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin
    {
        protected IFakeStep RegisterStep() => null!;
    }

    public class Other
    {
        public Other OnMessage<T>() => this;
    }

    public class SetStateRequest { }

    [Vela.Messages.SdkMessage(""SetState"")]
    public class PinnedSetState { }

    [Vela.Messages.SdkMessage(""setstate"")]
    public class PinnedSetStateLower { }

    [Vela.Constants.CustomMessage(""SetState"")]
    public class SetStateMarker { }

    [Vela.Messages.SdkMessage(""SetStateDynamicEntity"")]
    public class PinnedDynamic { }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    public class Use : Plugin
    {
        public void M()
        {
            " + body + @"
        }
    }
}
";

    private static DiagnosticResult Expected(string type) =>
        new DiagnosticResult(Vela.Analyzers.SetStateStepAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0).WithArguments(type);

    [Fact]
    public async Task Reports_a_request_type_pinned_to_SetState()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().{|#0:On<PinnedSetState>|}().Handle();"), Expected("PinnedSetState"));
    }

    [Fact]
    public async Task Reports_a_marker_pinned_to_SetState_case_insensitively()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().{|#0:OnMessage<PinnedSetStateLower>|}().Handle();"), Expected("PinnedSetStateLower"));
    }

    // The builder registers these on SetStateDynamicEntity (the request-name table maps SetState), so they fire.
    [Fact]
    public async Task Is_silent_for_SetStateRequest_and_a_CustomMessage_marker()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().On<SetStateRequest>().Handle(); RegisterStep().OnMessage<SetStateMarker>().Handle();"));
    }

    [Fact]
    public async Task Is_silent_for_SetStateDynamicEntity()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().On<PinnedDynamic>().Handle();"));
    }

    [Fact]
    public async Task Is_silent_for_a_non_step_OnMessage()
    {
        await Verify.VerifyAnalyzerAsync(InMethod("new Other().OnMessage<PinnedSetState>();"));
    }
}
