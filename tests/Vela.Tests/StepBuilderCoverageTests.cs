using System;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers StepBuilder<TEntity> branches: argument guards, optional-config null coalescing, the image
// add/configure paths, and the Build() image backstop (relaxed for non-modelled messages). The typed stage
// chain enforces the image/stage rules at compile time; this file exercises the runtime pieces that remain.
public sealed class StepBuilderCoverageTests
{
    // An entity type without [EntityLogicalName] so the constructor's type-name fallback is exercised.
    private sealed class NoNameEntity : Entity
    {
        public NoNameEntity() : base("placeholder") { }
    }

    private static StepBuilder<TestAccount> New() => new StepBuilder<TestAccount>();

    private void InstanceHandler(ILocalPluginContext ctx) { }

    private static class Holder
    {
        public static void StaticHandler(ILocalPluginContext ctx) { }
    }

    [Fact]
    public void Constructor_falls_back_to_the_type_name_without_the_logical_name_attribute()
    {
        StepDefinition definition = new StepBuilder<NoNameEntity>()
            .OnDelete().PostOperation().Mode(ExecutionMode.Synchronous).Build();

        Assert.Equal("nonameentity", definition.EntityLogicalName);
    }

    [Fact]
    public void OnMessage_rejects_blank_names()
    {
        Assert.Throws<ArgumentNullException>(() => New().OnMessage(null!));
        Assert.Throws<ArgumentNullException>(() => New().OnMessage("   "));
    }

    [Fact]
    public void FilteringAttributes_rejects_a_null_primary_attribute()
    {
        Assert.Throws<ArgumentNullException>(() => New().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(null!));
    }

    [Fact]
    public void Key_Handler_and_config_guards_and_trimming()
    {
        // Handler guards on the typed Delete step (which exposes .Handler(Action<IDeletePostContext<TestAccount>>)).
        Assert.Throws<ArgumentNullException>(() => New().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous)
            .Handler((Action<IDeletePostContext<TestAccount>>)null!));
        Assert.Throws<ArgumentException>(() => New().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous)
            .Handler(Holder.StaticHandler)); // must be an instance method

        IPreImageStep<TestAccount> step = New().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous)
            .Handler(InstanceHandler); // instance delegate -> method name

        Assert.Throws<ArgumentNullException>(() => step.Key("  "));

        StepDefinition definition = step
            .Key("  the-key  ")
            .Description(null!)        // null -> empty
            .UnsecureConfig(null!)     // null -> empty
            .SecureConfig(null!)       // null -> empty
            .Build();

        Assert.Equal("the-key", definition.StepKey);
        Assert.Equal(nameof(InstanceHandler), definition.HandlerMethodName);
        Assert.Equal(string.Empty, definition.Description);
        Assert.Equal(string.Empty, definition.UnsecureConfig);
        Assert.Equal(string.Empty, definition.SecureConfig);
    }

    [Fact]
    public void AddPreImage_and_AddPostImage_with_and_without_attributes()
    {
        StepDefinition withAttrs = New().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)
            .AddPreImage(a => a.Name)
            .AddPostImage() // no attributes -> full image
            .Build();

        Assert.Single(withAttrs.PreImages);
        Assert.Equal(new[] { "name" }, withAttrs.PreImages[0].Attributes);
        Assert.Single(withAttrs.PostImages);
        Assert.Empty(withAttrs.PostImages[0].Attributes);
    }

    // Build() image backstop. The typed chain already prevents an invalid image at compile time for the
    // modelled messages; the backstop catches the same rules when the modelled message reaches the builder
    // through the permissive OnMessage(...) escape hatch (or reflection). Delete carries no attribute payload,
    // so no filtering requirement masks the image check.
    [Fact]
    public void Build_backstop_rejects_an_unsupported_image_on_a_modelled_message()
    {
        // Delete has a pre-image but no post-image.
        Assert.Throws<InvalidOperationException>(() =>
            New().OnMessage(PluginMessages.Delete).PostOperation().Mode(ExecutionMode.Synchronous).PostImage().Build());

        StepDefinition ok = New().OnMessage(PluginMessages.Delete).PostOperation().Mode(ExecutionMode.Synchronous)
            .PreImage().Build();
        Assert.Single(ok.PreImages);
    }

    [Fact]
    public void Build_backstop_is_permissive_for_a_non_modelled_message()
    {
        // Vela does not model this message's image rules, so the builder does not reject it (the platform
        // arbitrates at deploy time).
        StepDefinition definition = New().OnMessage("acme_DoThing").PostOperation().Mode(ExecutionMode.Synchronous)
            .PreImage().Attributes(a => a.Name)
            .PostImage().Attributes(a => a.AccountId)
            .Build();

        Assert.Single(definition.PreImages);
        Assert.Single(definition.PostImages);
    }

    [Fact]
    public void Build_rejects_duplicate_filtering_attributes_and_image_aliases()
    {
        Assert.Throws<InvalidOperationException>(() =>
            New().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name, a => a.Name) // duplicate
                .Build());

        StepBuilder<TestAccount> dupAlias = New();
        dupAlias.OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)
            .PreImage("dup").Attributes(a => a.Name)
            .PostImage("dup").Attributes(a => a.AccountId); // duplicate alias across images
        Assert.Throws<InvalidOperationException>(() => dupAlias.Build());
    }

    [Fact]
    public void Build_requires_a_filtering_attribute_for_create_and_update()
    {
        StepBuilder<TestAccount> builder = New();
        builder.OnCreate().PostOperation().Mode(ExecutionMode.Synchronous); // mutates builder
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }
}
