using System;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The registration-time guards of the step builder, each with the message the developer sees: set-once modifiers,
// the filtering rule, image validity, duplicates, and the argument checks of each modifier (including W1's
// ForRelationship and SkipWhenDepthAbove).
public sealed class StepBuilderGuardTests
{
    private sealed class DelegatePlugin(Action<IStepTrigger<TestAccount>> register) : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps() => register(RegisterStep());
    }

    private static StepDefinition Single(Action<IStepTrigger<TestAccount>> register) =>
        Assert.Single(new DelegatePlugin(register).GetStepDefinitions());

    // The registration error itself, unwrapped from any framework wrapper.
    private static Exception Error(Action<IStepTrigger<TestAccount>> register)
    {
        Exception ex = Assert.ThrowsAny<Exception>(() => new DelegatePlugin(register).GetStepDefinitions());
        while (ex is System.Reflection.TargetInvocationException or Microsoft.Xrm.Sdk.InvalidPluginExecutionException && ex.InnerException != null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private static string AlreadySet(string method) => $"{method}(...) is already set on this step; call it at most once.";

    [Fact]
    public void Each_single_value_modifier_can_be_set_only_once()
    {
        Guid user = Guid.NewGuid();
        Assert.Equal(AlreadySet("FilterOn"), Error(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).FilterOn(a => a.Name).Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithStepKey"), Error(s => s.OnDelete().PostOperation().Sync().WithStepKey("a").WithStepKey("b").Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("SkipWhenDepthAbove"), Error(s => s.OnDelete().PostOperation().Sync().SkipWhenDepthAbove(2).SkipWhenDepthAbove(3).Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("ForRelationship"), Error(s => s.OnAssociate().PostOperation().Sync().ForRelationship("a").ForRelationship("b").Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithDescription"), Error(s => s.OnDelete().PostOperation().Sync().WithDescription("a").WithDescription("b").Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithImpersonation"), Error(s => s.OnDelete().PostOperation().Sync().WithImpersonation(user).WithImpersonation(user).Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithSupportedDeployment"), Error(s => s.OnDelete().PostOperation().Sync().WithSupportedDeployment(SupportedDeployment.Both).WithSupportedDeployment(SupportedDeployment.Both).Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithUnsecureConfig"), Error(s => s.OnDelete().PostOperation().Sync().WithUnsecureConfig("a").WithUnsecureConfig("b").Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithSecureConfigFrom"), Error(s => s.OnDelete().PostOperation().Sync().WithSecureConfigFrom("A").WithSecureConfigFrom("B").Handle(_ => { })).Message);
        Assert.Equal(AlreadySet("WithExecutionOrder"), Error(s => s.OnDelete().PostOperation().Sync().WithExecutionOrder(1).WithExecutionOrder(2).Handle(_ => { })).Message);
    }

    // Markers for OnMessage<TMessage>(): a custom request name whose [SdkMessage] registers the step on a modelled
    // sdkmessage. The typed-trigger rule checks the request name, so the builder's backstops still apply to these.
    [Vela.Constants.CustomMessage("contract_UpdateAlias")]
    [Vela.Messages.SdkMessage("Update")]
    private sealed class UpdateAlias { }

    [Vela.Constants.CustomMessage("contract_DeleteAlias")]
    [Vela.Messages.SdkMessage("Delete")]
    private sealed class DeleteAlias { }

    [Vela.Constants.CustomMessage("contract_RouteAlias")]
    [Vela.Messages.SdkMessage("RouteTo")]
    private sealed class RouteAlias { }

    [Fact]
    public void A_step_registered_on_update_without_a_filter_is_refused()
    {
        Assert.StartsWith(
            "Step on message 'Update' must declare at least one filtering attribute via FilterOn(...). An unfiltered Create/Update step fires on every change.",
            Error(s => s.OnMessage<UpdateAlias>().PostOperation().Sync().Handle(_ => { })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_the_message_cannot_provide_is_refused_with_its_reason()
    {
        Assert.Equal("PreImage 'Old' is not supported for message 'RouteTo' at stage 'PostOperation': Dataverse supports no image on this message.",
            Error(s => s.OnMessage<RouteAlias>().PostOperation().Sync().WithPreImage("Old", a => a.Name).Handle(_ => { })).Message);
        Assert.Equal("PostImage 'Gone' is not supported for message 'Delete' at stage 'PostOperation': the row no longer exists after a delete.",
            Error(s => s.OnMessage<DeleteAlias>().PostOperation().Sync().WithPostImage("Gone", a => a.Name).Handle(_ => { })).Message);
    }

    [Fact]
    public void Duplicates_are_refused_case_insensitively()
    {
        Assert.Equal("Duplicate filtering attribute: 'name' is specified more than once.",
            Error(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name, a => a.Name).Handle(_ => { })).Message);
        Assert.Equal("Duplicate relationship: 'rel_a' is specified more than once.",
            Error(s => s.OnAssociate().PostOperation().Sync().ForRelationship("rel_a", " REL_A ").Handle(_ => { })).Message);
        Assert.Equal("Duplicate attribute in image 'PostImage': 'name' is specified more than once.",
            Error(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name, a => a.Name).Handle(_ => { })).Message);
        Assert.Equal("Duplicate image alias: 'Snap' is specified more than once.",
            Error(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage("Snap", a => a.Name).WithPostImage("snap", a => a.Name).Handle(_ => { })).Message);
    }

    [Fact]
    public void Modifier_arguments_are_validated()
    {
        ArgumentOutOfRangeException depth = Assert.IsType<ArgumentOutOfRangeException>(Error(s => s.OnDelete().PostOperation().Sync().SkipWhenDepthAbove(0).Handle(_ => { })));
        Assert.StartsWith("SkipWhenDepthAbove requires a depth of at least 1.", depth.Message, StringComparison.Ordinal);
        Assert.Equal(1, Single(s => s.OnDelete().PostOperation().Sync().SkipWhenDepthAbove(1).Handle(_ => { })).SkipWhenDepthAbove);

        Assert.StartsWith("ForRelationship requires non-empty relationship schema names.",
            Error(s => s.OnAssociate().PostOperation().Sync().ForRelationship("a", " ").Handle(_ => { })).Message, StringComparison.Ordinal);
        Assert.StartsWith("WithImpersonation requires a non-empty systemuser id.",
            Error(s => s.OnDelete().PostOperation().Sync().WithImpersonation(Guid.Empty).Handle(_ => { })).Message, StringComparison.Ordinal);
        Assert.Equal("stepKey", Assert.IsType<ArgumentNullException>(Error(s => s.OnDelete().PostOperation().Sync().WithStepKey("  ").Handle(_ => { }))).ParamName);
        Assert.Equal("handler", Assert.IsType<ArgumentNullException>(Error(s => s.OnDelete().PostOperation().Sync().Handle(null!))).ParamName);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("é")]
    public void A_secure_config_name_must_be_an_identifier(string name)
    {
        string message = Error(s => s.OnDelete().PostOperation().Sync().WithSecureConfigFrom(name).Handle(_ => { })).Message;
        Assert.StartsWith("WithSecureConfigFrom takes the NAME of a secure configuration", message, StringComparison.Ordinal);
        // The hint never suggests putting the secret on the command line.
        Assert.Contains("--secure-config NAME=@file or the VELA_SECURE_CONFIG_NAME environment variable", message, StringComparison.Ordinal);
        Assert.DoesNotContain("=VALUE", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("_x")]
    [InlineData("zZ09")]
    [InlineData("aA")]
    public void Identifier_names_are_accepted_as_secure_config_names(string name)
    {
        Assert.Equal(name, Single(s => s.OnDelete().PostOperation().Sync().WithSecureConfigFrom(name).Handle(_ => { })).SecureConfigName);
    }

    [Fact]
    public void Text_modifiers_are_trimmed_or_defaulted_and_relationship_steps_have_no_entity()
    {
        StepDefinition d = Single(s => s.OnDisassociate().PostOperation().Sync()
            .WithStepKey("  key  ").ForRelationship(" rel_a ", "rel_b").WithDescription(null!).WithUnsecureConfig(null!).Handle(_ => { }));

        Assert.Equal("key", d.StepKey);
        Assert.Equal(new[] { "rel_a", "rel_b" }, d.Relationships);
        Assert.Equal(string.Empty, d.Description);
        Assert.Equal(string.Empty, d.UnsecureConfig);
        Assert.Equal(string.Empty, d.EntityLogicalName);
        Assert.Equal("Disassociate", d.Message);
    }

    [Fact]
    public void An_image_without_columns_captures_every_column()
    {
        StepDefinition d = Single(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPreImage().Handle(_ => { }));

        Assert.Empty(Assert.Single(d.PreImages).Attributes);
    }

    private static void StaticHandler(IUpdatePostContext<TestAccount> ctx) { }

    [Fact]
    public void A_handler_must_be_an_instance_method_with_one_context_parameter()
    {
        Assert.StartsWith("Handler delegate must reference an instance method.",
            Error(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(StaticHandler)).Message, StringComparison.Ordinal);
        Assert.StartsWith("Step handler '",
            Error(s => ((IStepConfig<TestAccount>)s.OnDelete().PostOperation().Sync()).Handle(new Func<ILocalPluginContext, int>(_ => 1))).Message, StringComparison.Ordinal);
    }
}
