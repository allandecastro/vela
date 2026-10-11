using System;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers StepBuilder<TEntity> branches: argument guards, optional-config null coalescing, the image
// add/configure paths, and the Build() image backstop (relaxed for non-modelled messages). The typed stage
// chain enforces the image/stage rules at compile time; this file exercises the runtime pieces that remain.
// The fluent transitions are Vela.Steps interface members, so the builder is driven through IStepTrigger<T>
// while a reference to the concrete StepBuilder<T> is kept to call Build() (the chain terminal is a void
// Handle(...) and cannot itself reach Build()).
public sealed class StepBuilderCoverageTests
{
    // An entity type without [EntityLogicalName] so the constructor's type-name fallback is exercised.
    private sealed class NoNameEntity : Entity
    {
        public NoNameEntity() : base("placeholder") { }
    }

    private static StepBuilder<TestAccount> New() => new StepBuilder<TestAccount>();

    private static IStepTrigger<TestAccount> Trigger(StepBuilder<TestAccount> builder) => builder;

    private void InstanceHandler(IDeletePostContext<TestAccount> ctx) { }

    private static class Holder
    {
        public static void StaticHandler(IDeletePostContext<TestAccount> ctx) { }
    }

    [Fact]
    public void Constructor_falls_back_to_the_type_name_without_the_logical_name_attribute()
    {
        StepBuilder<NoNameEntity> builder = new StepBuilder<NoNameEntity>();
        ((IStepTrigger<NoNameEntity>)builder).OnDelete().PostOperation().Sync();

        StepDefinition definition = builder.Build();

        Assert.Equal("nonameentity", definition.EntityLogicalName);
    }

    // A marker type with neither a RequestName nor [CustomMessage] names no message: the trigger throws instead
    // of registering the step on a message named after the type.
    private sealed class UnmarkedMessage
    {
    }

    [Fact]
    public void OnMessage_rejects_a_type_that_names_no_message()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Trigger(New()).OnMessage<UnmarkedMessage>());
        Assert.Contains(nameof(UnmarkedMessage), ex.Message);
        Assert.Contains("[CustomMessage", ex.Message);
    }

    [Fact]
    public void Handle_and_config_guards_and_trimming()
    {
        // Handler guards on the typed Delete step (Handle takes Action<IDeletePostContext<TestAccount>>).
        Assert.Throws<ArgumentNullException>(() => Trigger(New()).OnDelete().PostOperation().Sync()
            .Handle((Action<IDeletePostContext<TestAccount>>)null!));
        Assert.Throws<ArgumentException>(() => Trigger(New()).OnDelete().PostOperation().Sync()
            .Handle(Holder.StaticHandler)); // must be an instance method

        // Blank key rejected.
        Assert.Throws<ArgumentNullException>(() => Trigger(New()).OnDelete().PostOperation().Sync().WithStepKey("  "));

        StepBuilder<TestAccount> builder = New();
        Trigger(builder).OnDelete().PostOperation().Sync()
            .WithStepKey("  the-key  ")
            .WithDescription(null!)    // null -> empty
            .WithUnsecureConfig(null!) // null -> empty
            .Handle(InstanceHandler);  // instance delegate -> method name

        StepDefinition definition = builder.Build();

        Assert.Equal("the-key", definition.StepKey);
        Assert.Equal(nameof(InstanceHandler), definition.HandlerMethodName);
        Assert.Equal(string.Empty, definition.Description);
        Assert.Equal(string.Empty, definition.UnsecureConfig);
        Assert.Equal(string.Empty, definition.SecureConfigName);
    }

    [Fact]
    public void WithPreImage_and_WithPostImage_with_and_without_attributes()
    {
        StepBuilder<TestAccount> builder = New();
        Trigger(builder).OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage(a => a.Name)
            .WithPostImage(); // no attributes -> full image

        StepDefinition withAttrs = builder.Build();

        Assert.Single(withAttrs.PreImages);
        Assert.Equal(new[] { "name" }, withAttrs.PreImages[0].Attributes);
        Assert.Single(withAttrs.PostImages);
        Assert.Empty(withAttrs.PostImages[0].Attributes);
    }

    // A modelled message never reaches the permissive path: the typed chain prevents an invalid image at compile
    // time, and OnMessage<TMessage>() on a modelled message is rejected with the typed trigger to use (one way per
    // message), so the Build() image backstop only serves reflection-built steps.
    [Fact]
    public void A_modelled_message_cannot_take_the_permissive_path()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Trigger(New()).OnMessage<Microsoft.Xrm.Sdk.Messages.DeleteRequest>());
        Assert.Contains("OnDelete()", ex.Message);
    }

    [Fact]
    public void Build_backstop_is_permissive_for_a_non_modelled_message()
    {
        // Vela does not model this message's image rules, so the builder does not reject it (the platform
        // arbitrates at deploy time).
        StepBuilder<TestAccount> builder = New();
        Trigger(builder).OnMessage<AcmeDoThing>().PostOperation().Sync()
            .WithPreImage(a => a.Name)
            .WithPostImage(a => a.AccountId);

        StepDefinition definition = builder.Build();

        Assert.Single(definition.PreImages);
        Assert.Single(definition.PostImages);
    }

    [Fact]
    public void Build_rejects_duplicate_filtering_attributes_and_image_aliases()
    {
        StepBuilder<TestAccount> dupFiltering = New();
        Trigger(dupFiltering).OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name, a => a.Name); // duplicate
        Assert.Throws<InvalidOperationException>(() => dupFiltering.Build());

        StepBuilder<TestAccount> dupAlias = New();
        Trigger(dupAlias).OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .WithPreImage("dup", a => a.Name)
            .WithPostImage("dup", a => a.AccountId); // duplicate alias across images
        Assert.Throws<InvalidOperationException>(() => dupAlias.Build());
    }

    [Fact]
    public void Build_requires_a_filtering_attribute_for_create_and_update()
    {
        StepBuilder<TestAccount> builder = New();
        Trigger(builder).OnCreate().PostOperation().Sync(); // mutates builder, no filtering
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }
}
