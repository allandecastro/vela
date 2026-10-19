using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using VerifyPartial = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.PartialPluginForTypedImageAnalyzer>;

namespace Vela.Analyzers.Tests;

// A generated {Handler}Context exposes a typed image only where its base context has one: Create post (PostImage),
// Update pre (PreImage), Update post (both), Delete (PreImage), the typed message contexts; never on the bulk or
// other message contexts. A projection captured for an image the stage cannot carry stays a standalone type.
public sealed class ImageProjectionGeneratorContextImageTests
{
    private static string Context(string chain, string handler = "OnStep")
    {
        GeneratorHarness.Run run = GeneratorHarness.Execute(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps() => RegisterStep()." + chain + @".Handle(" + handler + @");
        private void " + handler + @"() { }
    }
}
");
        Assert.Empty(run.CompileErrors);
        return run.Sources["Demo_P_" + handler + "Context.Context.g.cs"].Replace("\r\n", "\n");
    }

    [Fact]
    public void Update_pre_operation_exposes_the_typed_pre_image()
    {
        string context = Context("OnUpdate().PreOperation().Sync().WithPreImage(a => a.Name)");
        Assert.Contains(": global::Vela.Context.UpdatePreContext<global::Demo.Account>", context);
        Assert.Contains("public new OnStep_PreImage? PreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<OnStep_PreImage>(this);", context);
    }

    [Fact]
    public void Create_post_operation_exposes_only_the_post_image()
    {
        string context = Context("OnCreate().PostOperation().Sync().WithPreImage(a => a.Name).WithPostImage(a => a.Revenue)");
        Assert.Contains("public new OnStep_PostImage? PostImage", context);
        Assert.DoesNotContain("PreImage =>", context);
    }

    [Fact]
    public void Delete_exposes_only_the_pre_image()
    {
        string context = Context("OnDelete().PreOperation().Sync().WithPreImage(a => a.Name).WithPostImage(a => a.Revenue)");
        Assert.Contains("public new OnStep_PreImage? PreImage", context);
        Assert.DoesNotContain("PostImage =>", context);
    }

    [Theory]
    [InlineData("OnCreateMultiple().PostOperation().Sync().WithPostImage(a => a.Name)", "CreateMultiplePostContext", "OnStep_PostImage", "PostImages", "PostImagesAs")]
    [InlineData("OnUpdateMultiple().PreOperation().Sync().WithPreImage(a => a.Name)", "UpdateMultiplePreContext", "OnStep_PreImage", "PreImages", "PreImagesAs")]
    public void A_bulk_context_exposes_its_typed_images_per_record_never_a_single_one(string chain, string baseContext, string projection, string property, string accessor)
    {
        // Bulk images are per record: the context lists them by index, typed by the projection.
        string context = Context(chain);
        Assert.Contains(": global::Vela.Context." + baseContext + "<global::Demo.Account>", context);
        Assert.Contains($"public global::System.Collections.Generic.IReadOnlyList<{projection}?> {property} => global::Vela.Context.ImageProjectionExtensions.{accessor}<{projection}>(this);", context);
        Assert.DoesNotContain("public new", context);
    }

    [Fact]
    public void A_step_without_a_stage_gets_no_context_and_needs_no_partial_plugin()
    {
        GeneratorHarness.Run run = GeneratorHarness.Execute(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps() => RegisterStep().OnUpdate().Sync().Handle(OnStep);
        private void OnStep() { }
    }
}
");
        Assert.Empty(run.Sources);
    }

    [Fact]
    public async Task VELA014_is_not_reported_for_a_named_handler_that_gets_no_context()
    {
        // The generator emits nothing for this step (no stage), so the plugin does not have to be partial.
        string code = (string)typeof(PartialPluginForTypedImageAnalyzerTests)
            .GetMethod("InPlugin", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { @"
    public sealed class Plain : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps() => RegisterStep().OnUpdate().Sync().Handle(OnStep);
        private void OnStep() { }
    }" })!;

        await VerifyPartial.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public void A_collision_hash_covers_every_column_of_the_shape()
    {
        GeneratorHarness.Run run = GeneratorHarness.Execute(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class Collide : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Revenue, a => a.Telephone1).Handle(() => { });
        }
    }
}
");
        // Signature of the second shape: "Revenue:decimal?:revenue;Telephone1:string?:telephone1" (columns in
        // declaration order, ';'-separated).
        string hash = Fnv1a("Revenue:decimal?:revenue;Telephone1:string?:telephone1");
        Assert.Equal(
            new[] { "Demo_Collide_UpdatePostOp_PostImage.g.cs", $"Demo_Collide_UpdatePostOp_PostImage_{hash}.g.cs" }.OrderBy(x => x),
            run.Sources.Keys.OrderBy(x => x));
    }

    private static string Fnv1a(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value)
        {
            hash = unchecked((hash ^ c) * 16777619);
        }

        return hash.ToString("x8");
    }

    [Fact]
    public void A_chain_broken_after_a_bound_step_call_still_gets_its_context_and_projection()
    {
        // A chain that stops binding after PostOperation() (here Mode() stands for a missing Sync()/Async()): the
        // context and the projection are still emitted, so the compiler reports the chain's own error rather than
        // only a CS0246 on OnStepContext, and no VELA021 is added about a capture whose lambda cannot bind.
        GeneratorHarness.Run run = GeneratorHarness.Execute(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnCreate().PostOperation().Mode().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle(OnStep);
        private void OnStep(OnStepContext ctx) { }
    }
}
");
        Assert.Contains("Demo_P_OnStepContext.Context.g.cs", run.Sources.Keys);
        Assert.Contains(run.CompileErrors, e => e.Id == "CS1061" && e.GetMessage().Contains("Mode"));
        Assert.DoesNotContain(run.CompileErrors, e => e.Id == "CS0246");
        Assert.DoesNotContain(run.GeneratorDiagnostics, d => d.Id == "VELA021");
    }

    [Fact]
    public void A_call_on_an_unrelated_broken_chain_gets_nothing()
    {
        // The first call that binds is not a step-builder method: not a Vela chain.
        GeneratorHarness.Run run = GeneratorHarness.Execute(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps() => ""x"".Trim().Mode().WithPostImage(a => a.Name).Handle(OnStep);
        private void OnStep() { }
    }
}
");
        Assert.Empty(run.Sources);
    }
}
