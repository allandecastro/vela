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
    public void TryInject_CustomMessage_UsesOnMessage()
    {
        bool ok = StepInjector.TryInject(HandlerBased, "Account", "acme_DoThing", "OnMessage(\"acme_DoThing\")", "HandleDoThing", null, out string result, out _);

        Assert.True(ok);
        Assert.Contains("RegisterStep().OnMessage(\"acme_DoThing\")", result);
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

    // HandlerBased with its Create step filtered on Name (and optionally another modifier).
    private static string FilteredCreate(string extra = "") => HandlerBased.Replace(
        "            RegisterStep().OnCreate()\n                .PostOperation()\n                .Sync()\n                .Handle(HandleCreate);",
        "            RegisterStep().OnCreate()\n                .PostOperation()\n                .Sync()\n                .FilterOn(a => a.Name)" + extra + "\n                .Handle(HandleCreate);");

    [Theory]
    [InlineData("OnCreate()", ".PostOperation()", ".Sync()")]
    [InlineData("OnMessage(\"create\")", ".PostOperation()", ".Sync()")]   // same message, string form
    public void TryInject_RefusesAnExactDuplicate_SameTriggerAndFilterColumns(string registrationCall, string stageCall, string modeCall)   // A-25 rule
    {
        string source = FilteredCreate();
        Assert.NotEqual(HandlerBased, source);

        bool ok = StepInjector.TryInject(source, "Account", "Create", registrationCall, "HandleOther", stageCall, modeCall, postStage: true, Name, out string result, out string error, out _);

        Assert.False(ok);
        Assert.Contains("already registers a Create PostOperation Sync step with the same filtering columns", error);
        Assert.Equal(source, result);
    }

    [Fact]
    public void TryInject_AllowsASharedTrigger_ToldApartByItsFilterColumns_AndSuggestsAStepKey()
    {
        bool ok = StepInjector.TryInject(FilteredCreate(), "Account", "Create", "OnCreate()", "HandleOther", ".PostOperation()", ".Sync()", postStage: true, ["Telephone1"], out string result, out string error, out string? note);

        Assert.True(ok, error);
        Assert.Contains(".FilterOn(e => e.Telephone1)", result);
        Assert.NotNull(note);
        Assert.Contains("WithStepKey", note);
    }

    [Theory]
    [InlineData("\n                .WithStepKey(\"main\")")]
    [InlineData("\n                .WithPostImage(a => a.Name)")]
    [InlineData("\n                .WithPreImage(a => a.Name)")]
    public void TryInject_AllowsTheSameFilterColumns_WhenTheExistingStepIsToldApartOtherwise(string modifier)
    {
        bool ok = StepInjector.TryInject(FilteredCreate(modifier), "Account", "Create", "OnCreate()", "HandleOther", ".PostOperation()", ".Sync()", postStage: true, Name, out _, out string error, out string? note);

        Assert.True(ok, error);
        Assert.NotNull(note);
    }

    [Theory]
    [InlineData(".PreOperation()", ".Sync()")]   // other stage
    [InlineData(".PostOperation()", ".Async()")] // other mode
    public void TryInject_OtherTrigger_IsNeitherRefusedNorNoted(string stageCall, string modeCall)
    {
        bool ok = StepInjector.TryInject(FilteredCreate(), "Account", "Create", "OnCreate()", "HandleOther", stageCall, modeCall, postStage: stageCall == ".PostOperation()", Name, out _, out string error, out string? note);

        Assert.True(ok, error);
        Assert.Null(note);
    }

    [Fact]
    public void TryInject_FilterColumnOrderDoesNotMatter()
    {
        string source = FilteredCreate().Replace(".FilterOn(a => a.Name)", ".FilterOn(a => a.Name, a => a.Telephone1)");

        Assert.False(StepInjector.TryInject(source, "Account", "Create", "OnCreate()", "HandleOther", ".PostOperation()", ".Sync()", postStage: true, ["Telephone1", "Name"], out _, out _, out _));
    }

    [Fact]
    public void TryInject_IgnoresACommentedOutRegistration_WhenLookingForDuplicates()
    {
        string source = HandlerBased.Replace(
            "            RegisterStep().OnCreate()\n                .PostOperation()\n                .Sync()\n                .Handle(HandleCreate);",
            "            RegisterStep().OnUpdate()\n                .PostOperation()\n                .Sync()\n                .FilterOn(e => e.Name)\n                .Handle(HandleCreate);\n            // RegisterStep().OnCreate().PostOperation().Sync().Handle(Old);");
        Assert.NotEqual(HandlerBased, source);

        bool ok = StepInjector.TryInject(source, "Account", "Create", "OnCreate()", "HandleNew", ".PostOperation()", ".Sync()", postStage: true, Name, out _, out string error, out string? note);

        Assert.True(ok, error);
        Assert.Null(note);
    }

    [Fact]
    public void TryInject_SkipsBracesInsideRawAndInterpolatedVerbatimStrings()   // A-78: the brace scanner
    {
        string source = HandlerBased.Replace(
            "        private void HandleCreate(ILocalPluginContext ctx)\n        {\n        }",
            "        private void HandleCreate(ILocalPluginContext ctx)\n        {\n            string a = \"\"\"\n{ not a brace\n\"\"\";\n            string b = @$\"{{ {a} \"\" }}\";\n        }");
        Assert.NotEqual(HandlerBased, source);
        source = source.Replace(
            "            RegisterStep().OnCreate()",
            "            string raw = $$\"\"\"{ \"json\": {{1}} }\"\"\";\n            RegisterStep().OnCreate()");

        bool ok = StepInjector.TryInject(source, "Account", "Update", "OnUpdate()", "HandleUpdate", Name, out string result, out string error);

        Assert.True(ok, error);
        // The new step lands inside RegisterPluginSteps, before its closing brace, and the handler after it.
        int registration = result.IndexOf("RegisterStep().OnUpdate()", StringComparison.Ordinal);
        int handler = result.IndexOf("private void HandleUpdate(", StringComparison.Ordinal);
        int existingHandler = result.IndexOf("private void HandleCreate(", StringComparison.Ordinal);
        Assert.True(registration > 0 && registration < existingHandler && existingHandler > handler, result);
    }
}
