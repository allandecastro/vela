using System;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Exercises the target/parameter/image/service helpers in PluginContextExtensions: the null-context guards,
// the wrong-type and missing paths, the bulk per-index image variants, the service resolver helpers, and the
// image/target merge edge cases.
public sealed class PluginContextExtensionsCoverageTests
{
    private static LocalPluginContext Ctx() => new PluginContextBuilder().Build();

    // ---- Target helpers --------------------------------------------------------------------------------

    [Fact]
    public void TryGetTarget_handles_missing_wrong_type_and_present()
    {
        LocalPluginContext ctx = Ctx();
        Assert.False(ctx.TryGetTarget(out Entity? none));     // no Target param
        Assert.Null(none);

        ctx.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", Guid.NewGuid());
        Assert.False(ctx.TryGetTarget(out Entity? wrong));    // Target is not an Entity
        Assert.Null(wrong);

        Entity target = new Entity("account", Guid.NewGuid());
        ctx.PluginExecutionContext.InputParameters["Target"] = target;
        Assert.True(ctx.TryGetTarget(out Entity? found));
        Assert.Same(target, found);
    }

    [Fact]
    public void GetTargetRef_returns_reference_or_throws()
    {
        LocalPluginContext ctx = Ctx();
        EntityReference reference = new EntityReference("account", Guid.NewGuid());
        ctx.PluginExecutionContext.InputParameters["Target"] = reference;
        Assert.Equal(reference, ctx.GetTargetRef());

        LocalPluginContext empty = Ctx();
        Assert.Throws<InvalidPluginExecutionException>(() => empty.GetTargetRef());
    }

    [Fact]
    public void GetTarget_typed_returns_entity_or_throws_when_absent()
    {
        LocalPluginContext ctx = Ctx();
        ctx.PluginExecutionContext.InputParameters["Target"] = new Entity("account", Guid.NewGuid()) { ["name"] = "Acme" };
        Assert.Equal("Acme", ctx.GetTarget<TestAccount>().Name);

        Assert.Throws<InvalidPluginExecutionException>(() => Ctx().GetTarget<TestAccount>());
    }

    [Fact]
    public void TryGetTargets_prefers_bulk_then_input_parameter_then_fails()
    {
        // BulkTargets present.
        EntityCollection bulk = new EntityCollection();
        Mock<ILocalPluginContext> mock = new Mock<ILocalPluginContext>();
        mock.SetupGet(c => c.BulkTargets).Returns(bulk);
        Assert.True(mock.Object.TryGetTargets(out EntityCollection? fromBulk));
        Assert.Same(bulk, fromBulk);

        // No bulk, Targets input parameter.
        LocalPluginContext ctx = Ctx();
        EntityCollection viaParam = new EntityCollection();
        ctx.PluginExecutionContext.InputParameters["Targets"] = viaParam;
        Assert.True(ctx.TryGetTargets(out EntityCollection? fromParam));
        Assert.Same(viaParam, fromParam);

        // None at all.
        Assert.False(Ctx().TryGetTargets(out _));
        Assert.Throws<InvalidPluginExecutionException>(() => Ctx().GetTargets());
    }

    // ---- Parameter helpers -----------------------------------------------------------------------------

    [Fact]
    public void GetInputParameter_returns_value_default_on_missing_or_wrong_type()
    {
        LocalPluginContext ctx = Ctx();
        ctx.PluginExecutionContext.InputParameters["Count"] = 7;
        Assert.Equal(7, ctx.GetInputParameter<int>("Count"));
        Assert.Equal(0, ctx.GetInputParameter<int>("Missing"));         // missing -> default
        Assert.Null(ctx.GetInputParameter<string>("Count"));            // wrong type -> default
    }

    [Fact]
    public void SetOutputParameter_and_GetOutputParameter_round_trip()
    {
        LocalPluginContext ctx = Ctx();
        ctx.SetOutputParameter("Score", 99);
        Assert.Equal(99, ctx.GetOutputParameter<int>("Score"));
        Assert.Null(ctx.GetOutputParameter<string>("Score")); // wrong type
    }

    // ---- Service resolver helpers ----------------------------------------------------------------------

    [Fact]
    public void GetService_resolves_on_local_context_and_returns_null_otherwise()
    {
        LocalPluginContext ctx = Ctx();
        Assert.Same(ctx.TracingService, ctx.GetService<ITracingService>());

        // A non-LocalPluginContext implementation cannot expose the resolver.
        Assert.Null(Mock.Of<ILocalPluginContext>().GetService<ITracingService>());
    }

    [Fact]
    public void GetRequiredService_resolves_on_local_context_and_throws_otherwise()
    {
        LocalPluginContext ctx = Ctx();
        Assert.Same(ctx.TracingService, ctx.GetRequiredService<ITracingService>());
        Assert.Throws<InvalidPluginExecutionException>(() => Mock.Of<ILocalPluginContext>().GetRequiredService<ITracingService>());
    }

    // ---- Null-context guards ---------------------------------------------------------------------------

    [Fact]
    public void Helpers_reject_a_null_context()
    {
        ILocalPluginContext ctx = null!;
        Assert.Throws<ArgumentNullException>(() => ctx.TryGetTarget(out _));
        Assert.Throws<ArgumentNullException>(() => ctx.TryGetTargets(out _));
        Assert.Throws<ArgumentNullException>(() => ctx.GetInputParameter<int>("x"));
        Assert.Throws<ArgumentNullException>(() => ctx.GetOutputParameter<int>("x"));
        Assert.Throws<ArgumentNullException>(() => ctx.SetOutputParameter("x", 1));
        Assert.Throws<ArgumentNullException>(() => ctx.GetService<ITracingService>());
        Assert.Throws<ArgumentNullException>(() => ctx.GetRequiredService<ITracingService>());
    }

    // ---- Single-image helpers --------------------------------------------------------------------------

    [Fact]
    public void GetPreImage_and_GetPostImage_return_image_or_null()
    {
        LocalPluginContext ctx = Ctx();
        Assert.Null(ctx.GetPreImage());
        Assert.Null(ctx.GetPostImage<TestAccount>());

        Entity pre = new Entity("account", Guid.NewGuid()) { ["name"] = "Old" };
        ctx.PluginExecutionContext.PreEntityImages["PreImage"] = pre;
        Assert.Same(pre, ctx.GetPreImage());
        Assert.Equal("Old", ctx.GetPreImage<TestAccount>()!.Name);
    }

    // ---- Bulk per-index image helpers ------------------------------------------------------------------

    [Fact]
    public void Bulk_image_helpers_validate_index_and_return_image_or_null()
    {
        EntityImageCollection images = new EntityImageCollection();
        Entity img = new Entity("account", Guid.NewGuid()) { ["name"] = "Bulk" };
        images["PreImage"] = img;

        Mock<ILocalPluginContext> mock = new Mock<ILocalPluginContext>();
        mock.SetupGet(c => c.BulkPreEntityImagesCollection).Returns(new[] { images });
        mock.SetupGet(c => c.BulkPostEntityImagesCollection).Returns(Array.Empty<EntityImageCollection>());
        ILocalPluginContext ctx = mock.Object;

        Assert.Equal("Bulk", ctx.GetPreImage(0)!.GetAttributeValue<string>("name")); // valid index
        Assert.Null(ctx.GetPreImage(5));                                              // index out of range
        Assert.Null(ctx.GetPostImage(0));                                             // empty collection
        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.GetPreImage(-1));        // negative index
    }

    // ---- Merge helpers ---------------------------------------------------------------------------------

    [Fact]
    public void GetPreImageMerged_overlays_target_onto_image()
    {
        LocalPluginContext ctx = Ctx();
        Guid id = Guid.NewGuid();
        ctx.PluginExecutionContext.PreEntityImages["PreImage"] = new Entity("account", id) { ["name"] = "Old", ["phone"] = "555" };
        ctx.PluginExecutionContext.InputParameters["Target"] = new Entity("account", id) { ["name"] = "New" };

        Entity merged = ctx.GetPreImageMerged();
        Assert.Equal("New", merged.GetAttributeValue<string>("name")); // target wins
        Assert.Equal("555", merged.GetAttributeValue<string>("phone")); // image-only attribute kept
    }

    [Fact]
    public void GetPostImageMerged_uses_target_only_when_no_image()
    {
        LocalPluginContext ctx = Ctx();
        ctx.PluginExecutionContext.InputParameters["Target"] = new Entity("account", Guid.NewGuid()) { ["name"] = "Solo" };
        Assert.Equal("Solo", ctx.GetPostImageMerged().GetAttributeValue<string>("name"));
    }

    [Fact]
    public void GetPreImageMerged_uses_image_only_when_no_target_and_throws_when_neither()
    {
        LocalPluginContext withImage = Ctx();
        Entity image = new Entity("account", Guid.NewGuid()) { ["name"] = "ImageOnly" };
        withImage.PluginExecutionContext.PreEntityImages["PreImage"] = image;

        // With no target the merged result carries the image's values but must be an independent copy, so
        // mutating it cannot corrupt the shared PreEntityImages entry.
        Entity merged = withImage.GetPreImageMerged();
        Assert.NotSame(image, merged);
        Assert.Equal("ImageOnly", merged.GetAttributeValue<string>("name"));
        merged["name"] = "Mutated";
        Assert.Equal("ImageOnly", image.GetAttributeValue<string>("name"));

        Assert.Throws<InvalidPluginExecutionException>(() => Ctx().GetPreImageMerged());
    }

    [Fact]
    public void Bulk_merged_image_overlays_target_at_index_and_validates()
    {
        Guid id = Guid.NewGuid();
        EntityCollection targets = new EntityCollection();
        targets.Entities.Add(new Entity("account", id) { ["name"] = "New" });

        EntityImageCollection images = new EntityImageCollection();
        images["PreImage"] = new Entity("account", id) { ["name"] = "Old", ["phone"] = "555" };

        Mock<ILocalPluginContext> mock = new Mock<ILocalPluginContext>();
        mock.SetupGet(c => c.BulkTargets).Returns(targets);
        mock.SetupGet(c => c.BulkPreEntityImagesCollection).Returns(new[] { images });
        ILocalPluginContext ctx = mock.Object;

        Entity merged = ctx.GetPreImageMerged(0);
        Assert.Equal("New", merged.GetAttributeValue<string>("name"));
        Assert.Equal("555", merged.GetAttributeValue<string>("phone"));

        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.GetPreImageMerged(-1));
    }
}
