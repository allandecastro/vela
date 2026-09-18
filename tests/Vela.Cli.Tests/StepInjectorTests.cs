using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class StepInjectorTests
{
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
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleUpdate", out string result, out string error);

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
    public void TryInject_CustomMessage_UsesOnMessage()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "acme_DoThing", "OnMessage(\"acme_DoThing\")", "HandleDoThing", out string result, out _);

        Assert.True(ok);
        Assert.Contains("RegisterStep().OnMessage(\"acme_DoThing\")", result);
    }

    [Fact]
    public void TryInject_RejectsSingleStepPlugin()
    {
        bool ok = StepInjector.TryInject(SingleStep, "Account", "Update", "OnUpdate()", "HandleUpdate", out _, out string error);

        Assert.False(ok);
        Assert.Contains("handler-bound step", error);
    }

    [Fact]
    public void TryInject_RejectsDuplicateHandlerName()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "Update", "OnUpdate()", "HandleCreate", out _, out string error);

        Assert.False(ok);
        Assert.Contains("already exists", error);
    }

    [Fact]
    public void TryInject_RejectsFileWithoutRegisterPluginSteps()
    {
        bool ok = StepInjector.TryInject("namespace N { public class C { } }", "Account", "Update", "OnUpdate()", "HandleUpdate", out _, out string error);

        Assert.False(ok);
        Assert.Contains("RegisterPluginSteps", error);
    }
}
