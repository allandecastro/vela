using System;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers StepBuilder<TEntity> branches: argument guards, optional-config null coalescing, the image
// add/configure paths, and the platform image-allowed rules (stage/message matrix).
public sealed class StepBuilderCoverageTests
{
    // An entity type without [EntityLogicalName] so the constructor's type-name fallback is exercised.
    private sealed class NoNameEntity : Entity
    {
        public NoNameEntity() : base("placeholder") { }
    }

    private static StepBuilder<TestAccount> New() => new StepBuilder<TestAccount>();

    private void InstanceHandler(ILocalPluginContext ctx, TestAccount target) { }

    private static class Holder
    {
        public static void StaticHandler(ILocalPluginContext ctx, TestAccount target) { }
    }

    [Fact]
    public void Constructor_falls_back_to_the_type_name_without_the_logical_name_attribute()
    {
        StepDefinition definition = new StepBuilder<NoNameEntity>()
            .OnDelete().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous).Build();

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
        Assert.Throws<ArgumentNullException>(() => New().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous).FilteringAttributes(null!));
    }

    [Fact]
    public void Stage_rejects_the_reserved_main_operation()
    {
        Assert.Throws<InvalidOperationException>(() => New().OnDelete().Stage(PluginStages.MainOperation));
    }

    [Fact]
    public void Key_Handler_and_config_guards_and_trimming()
    {
        IStepModeBuilder<TestAccount> step = New().OnDelete().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous);

        Assert.Throws<ArgumentNullException>(() => step.Key("  "));
        Assert.Throws<ArgumentNullException>(() => step.Handler((string)null!));
        Assert.Throws<ArgumentNullException>(() => step.Handler((Action<ILocalPluginContext, TestAccount>)null!));
        Assert.Throws<ArgumentException>(() => step.Handler(Holder.StaticHandler)); // must be an instance method

        StepDefinition definition = step
            .Key("  the-key  ")
            .Handler("  Handle  ")
            .Description(null!)        // null -> empty
            .UnsecureConfig(null!)     // null -> empty
            .SecureConfig(null!)       // null -> empty
            .Handler(InstanceHandler)  // instance delegate -> method name
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
        StepDefinition withAttrs = New().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)
            .AddPreImage(a => a.Name)
            .AddPostImage() // no attributes -> full image
            .Build();

        Assert.Single(withAttrs.PreImages);
        Assert.Equal(new[] { "name" }, withAttrs.PreImages[0].Attributes);
        Assert.Single(withAttrs.PostImages);
        Assert.Empty(withAttrs.PostImages[0].Attributes);
    }

    [Fact]
    public void Attributes_without_an_active_image_throws()
    {
        IStepImageBuilder<TestAccount> builder = (IStepImageBuilder<TestAccount>)New()
            .OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name);

        Assert.Throws<InvalidOperationException>(() => builder.Attributes(a => a.Name));
    }

    [Theory]
    // PostOperation: pre-image for Update, post-image for Create -> allowed.
    [InlineData(PluginMessages.Update, PluginStages.PostOperation, true, true)]
    [InlineData(PluginMessages.Create, PluginStages.PostOperation, false, true)]
    // PreOperation: only a pre-image, and only for Update/Delete.
    [InlineData(PluginMessages.Update, PluginStages.PreOperation, true, true)]
    [InlineData(PluginMessages.Update, PluginStages.PreOperation, false, false)] // post-image rejected
    // Bulk messages evaluate as their single-row counterpart at construction time (the harness simulates
    // per-record bulk images from them); the CLI enforces the stricter no-bulk-image deploy rule separately.
    [InlineData(PluginMessages.CreateMultiple, PluginStages.PostOperation, false, true)]
    [InlineData(PluginMessages.UpdateMultiple, PluginStages.PostOperation, true, true)]
    // PreValidation: no images at all.
    [InlineData(PluginMessages.Update, PluginStages.PreValidation, true, false)]
    public void Image_allowed_rules_match_the_platform_matrix(string message, PluginStages stage, bool preImage, bool allowed)
    {
        StepBuilder<TestAccount> builder = New();
        builder.OnMessage(message).Stage(stage); // mutates the shared builder's message + stage

        if (allowed)
        {
            IStepImageBuilder<TestAccount> image = preImage ? builder.PreImage() : builder.PostImage();
            Assert.NotNull(image);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => { if (preImage) builder.PreImage(); else builder.PostImage(); });
        }
    }

    [Fact]
    public void Build_rejects_duplicate_filtering_attributes_and_image_aliases()
    {
        Assert.Throws<InvalidOperationException>(() =>
            New().OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name, a => a.Name) // duplicate
                .Build());

        StepBuilder<TestAccount> dupAlias = New();
        dupAlias.OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)
            .PreImage("dup").Attributes(a => a.Name)
            .PostImage("dup").Attributes(a => a.AccountId); // duplicate alias across images
        Assert.Throws<InvalidOperationException>(() => dupAlias.Build());
    }

    [Fact]
    public void Build_requires_a_filtering_attribute_for_create_and_update()
    {
        StepBuilder<TestAccount> builder = New();
        builder.OnCreate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous); // mutates builder
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }
}
