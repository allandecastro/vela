using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class StepInjectorTests
{
    private static readonly string[] Name = ["Name"];

    // A handler-based plugin (uses .Handle(...)), which is what 'vela add step' targets.
    private const string HandlerBased = """
namespace Acme.Plugins.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using Vela.Steps;
    using Acme.Shared.Models;

    public sealed class AccountRouter : EntityPluginBase<Account>
    {
        public AccountRouter(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .Handle(HandleCreate);
        }

        private void HandleCreate(ILocalPluginContext ctx)
        {
        }
    }
}
""";

    // A plugin whose step chain never reaches .Handle(...) (no handler-bound step) — must be rejected.
    private const string SingleStep = """
namespace Acme.Plugins.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using Vela.Steps;
    using Acme.Shared.Models;

    public sealed class AccountValidator : EntityPluginBase<Account>
    {
        public AccountValidator(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync();
        }

        protected override void Execute(ILocalPluginContext ctx)
        {
        }
    }
}
""";

    [Fact]
    public void TryInject_AddsRegistrationAndHandler_WithBalancedBraces()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleUpdate", Name, out string result, out string error);

        Assert.True(ok, error);
        Assert.Contains("RegisterStep().OnUpdate()", result);
        Assert.Contains(".Handle(HandleUpdate);", result);
        Assert.Contains("private void HandleUpdate(IUpdatePostContext<Account> ctx)", result);
        // The original step is preserved.
        Assert.Contains(".Handle(HandleCreate);", result);
        // Braces stay balanced — the edit didn't corrupt the file.
        Assert.Equal(result.Count(c => c == '{'), result.Count(c => c == '}'));
    }

    [Fact]
    public void TryInject_CustomMessage_UsesRequestTypedTriggerAndContext()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "acme_DoThing", "On<acme_DoThingRequest>()", "HandleDoThing", null, out string result, out _);

        Assert.True(ok);
        Assert.Contains("RegisterStep().On<acme_DoThingRequest>()", result);
        Assert.Contains("private void HandleDoThing(IMessagePostContext<acme_DoThingRequest> ctx)", result);
        Assert.Contains("ctx.Request", result);
    }

    [Fact]
    public void TryInject_RejectsSingleStepPlugin()
    {
        bool ok = StepInjector.TryInject(SingleStep, "Account", "Update", "OnUpdate()", "HandleUpdate", Name, out _, out string error);

        Assert.False(ok);
        Assert.Contains("handler-bound step", error);
    }

    [Fact]
    public void TryInject_RejectsDuplicateHandlerName()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleCreate", Name, out _, out string error);

        Assert.False(ok);
        Assert.Contains("already exists", error);
    }

    [Fact]
    public void TryInject_RejectsFileWithoutRegisterPluginSteps()
    {
        bool ok = StepInjector.TryInject("namespace N { public class C { } }", "Account", "Update", "OnUpdate()", "HandleUpdate", Name, out _, out string error);

        Assert.False(ok);
        Assert.Contains("RegisterPluginSteps", error);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("CreateMultiple")]
    [InlineData("UpdateMultiple")]
    public void TryInject_RejectsAFilteredMessageWithoutAttributes(string message)
    {
        // These steps must FilterOn: emitting none (or a placeholder such as e => e.Id, a VELA001 error) would give a
        // plugin that fails to build its steps at runtime.
        bool ok = StepInjector.TryInject(HandlerBased, "Account", message, $"On{message}()", "HandleNew" + message, null, out string result, out string error);

        Assert.False(ok);
        Assert.Contains("filtering attributes", error);
        Assert.Equal(HandlerBased, result);
    }

    [Fact]
    public void TryInject_HandlerTodoUsesTypedRequestNotGetTarget()
    {
        StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleUpdate", Name, out string single, out _);
        Assert.DoesNotContain("GetTarget<", single);
        Assert.Contains("ctx.Request.Target", single);

        StepInjector.TryInject(HandlerBased, "Account", "UpdateMultiple", "OnUpdateMultiple()", "HandleUpdateMultiple", Name, out string bulk, out _);
        Assert.Contains("ctx.Request.Targets", bulk);
    }

    [Fact]
    public void TryInject_PreStage_EmitsPreStageCallAndPreContext()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleUpdate", ".PreOperation()", ".Sync()", postStage: false, explicitFilterAttributes: Name, out string result, out string error);

        Assert.True(ok, error);
        Assert.Contains(".PreOperation()", result);
        Assert.DoesNotContain(".PostOperation()", result[result.IndexOf("HandleUpdate", System.StringComparison.Ordinal)..]);
        Assert.Contains("private void HandleUpdate(IUpdatePreContext<Account> ctx)", result);
    }

    [Fact]
    public void TryInject_AsyncMode_EmitsAsyncCall()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleUpdate", ".PostOperation()", ".Async()", postStage: true, explicitFilterAttributes: Name, out string result, out string error);

        Assert.True(ok, error);
        Assert.Contains(".Async()", result);
        Assert.Contains("IUpdatePostContext<Account>", result);
    }

    [Fact]
    public void TryInject_ExplicitFilterAttributes_EmitsFilterOnAndNoTodo()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleUpdate", ".PostOperation()", ".Sync()", postStage: true, new[] { "Name", "Telephone1" }, out string result, out string error);

        Assert.True(ok, error);
        Assert.Contains(".FilterOn(e => e.Name, e => e.Telephone1)", result);
        Assert.DoesNotContain("// TODO: add .FilterOn", result);
    }
}
