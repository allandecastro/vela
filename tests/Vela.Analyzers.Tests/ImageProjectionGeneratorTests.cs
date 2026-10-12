using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vela.Analyzers.Tests;

public sealed class ImageProjectionGeneratorTests
{
    // Self-contained stand-ins for the Vela surface the generator inspects: a fluent config implementing
    // Vela.Steps.IFluentStep exposing the chain methods, EntityPluginBase<TEntity>, an Entity base and the
    // AttributeLogicalName attribute. The generator resolves everything from this compilation, so no external
    // assembly reference is needed beyond the framework.
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Microsoft.Xrm.Sdk
{
    public class Entity { public T ToEntity<T>() where T : Entity, new() => new T(); }
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class AttributeLogicalNameAttribute : Attribute { public AttributeLogicalNameAttribute(string logicalName) { } }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStepConfig<TEntity> : IFluentStep
    {
        IStepConfig<TEntity> OnCreate();
        IStepConfig<TEntity> OnDelete();
        IStepConfig<TEntity> OnUpdate();
        IStepConfig<TEntity> OnCreateMultiple();
        IStepConfig<TEntity> OnUpdateMultiple();
        IStepConfig<TEntity> OnAssign();
        IStepConfig<TEntity> OnMerge();
        IStepConfig<TEntity> OnRecalc();
        IStepConfig<TEntity> On<TRequest>();
        IStepConfig<TEntity> OnMessage<TMessage>();
        IStepConfig<TEntity> PreValidation();
        IStepConfig<TEntity> PreOperation();
        IStepConfig<TEntity> PostOperation();
        IStepConfig<TEntity> Sync();
        IStepConfig<TEntity> FilterOn(Expression<Func<TEntity, object>> a);
        IStepConfig<TEntity> WithPreImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPreImage(string alias, params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPostImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPostImage(string alias, params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithSubordinatePreImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithSubordinatePreImage(string alias, params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithSubordinatePostImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithSubordinatePostImage(string alias, params Expression<Func<TEntity, object>>[] a);
        void Handle(Action handler);
    }
}

namespace Vela.Plugin
{
    using Vela.Steps;
    public abstract class EntityPluginBase<TEntity> where TEntity : Microsoft.Xrm.Sdk.Entity
    {
        protected IStepConfig<TEntity> RegisterStep() => null;
        protected virtual void RegisterPluginSteps() { }
    }
}

namespace Vela.Context
{
    using Microsoft.Xrm.Sdk;
    // Minimal stand-ins for the concrete step contexts a generated per-step context derives from, plus the image
    // accessor extensions it calls. Enough for the generated context to compile in the generator tests.
    public interface IImageProjection { Microsoft.Xrm.Sdk.Entity? Source { get; set; } }
    public interface ILocalPluginContext { }
    public class CreatePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public CreatePostContext(ILocalPluginContext inner) { } public TEntity? PostImage => default; }
    public class UpdatePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public UpdatePreContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; }
    public class UpdatePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public UpdatePostContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; public TEntity? PostImage => default; }
    public class DeletePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public DeletePreContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; }
    public class DeletePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public DeletePostContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; }
    public class MessagePreContext<TRequest, TEntity> : ILocalPluginContext where TEntity : Entity { public MessagePreContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; }
    public class MessagePostContext<TRequest, TEntity> : ILocalPluginContext where TEntity : Entity { public MessagePostContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; public TEntity? PostImage => default; }
    public class CreatePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public CreatePreContext(ILocalPluginContext inner) { } }
    public class CreateMultiplePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public CreateMultiplePostContext(ILocalPluginContext inner) { } }
    public class UpdateMultiplePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public UpdateMultiplePostContext(ILocalPluginContext inner) { } }
    public class MergePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public MergePreContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; public TEntity? SubordinatePreImage => default; }
    public class MergePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public MergePostContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; public TEntity? PostImage => default; public TEntity? SubordinatePreImage => default; public TEntity? SubordinatePostImage => default; }
    public class AssignPreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public AssignPreContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; }
    public class AssignPostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public AssignPostContext(ILocalPluginContext inner) { } public TEntity? PreImage => default; public TEntity? PostImage => default; }
    public static class ImageProjectionExtensions
    {
        public static TImage PreImageAs<TImage>(this ILocalPluginContext context, string alias = ""PreImage"") => default!;
        public static TImage PostImageAs<TImage>(this ILocalPluginContext context, string alias = ""PostImage"") => default!;
        public static System.Collections.Generic.IReadOnlyList<TImage> PreImagesAs<TImage>(this ILocalPluginContext context, string alias = ""PreImage"") => default!;
        public static System.Collections.Generic.IReadOnlyList<TImage> PostImagesAs<TImage>(this ILocalPluginContext context, string alias = ""PostImage"") => default!;
    }
}

namespace Demo
{
    using Microsoft.Xrm.Sdk;
    using Vela.Plugin;

    public sealed class Account : Entity
    {
        [AttributeLogicalName(""name"")] public string Name { get; set; }
        [AttributeLogicalName(""revenue"")] public decimal? Revenue { get; set; }
        [AttributeLogicalName(""telephone1"")] public string Telephone1 { get; set; }
        [AttributeLogicalName(""numberofemployees"")] public int NumberOfEmployees { get; set; }
        public Account Parent { get; set; }
    }

    public sealed class DeleteRequest { }
}
";

    private static string RunAndConcat(string pluginSource)
    {
        string source = Harness + pluginSource;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "gen_test",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ImageProjectionGenerator());
        driver = driver.RunGenerators(compilation);

        return string.Join("\n\n", driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString()));
    }

    [Fact]
    public void Emits_a_projection_with_only_the_captured_columns()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name, a => a.Revenue)
                .Handle(() => { });
    }
}
");

        Assert.Contains("namespace Demo", output);
        // The projection is nested inside the plugin's own partial class and named without the plugin prefix.
        Assert.Contains("partial class AccountAudit", output);
        Assert.Contains("class UpdatePostOp_PostImage", output);
        Assert.DoesNotContain("AccountAudit_UpdatePostOp", output);
        Assert.DoesNotContain("Generated.Images", output);
        Assert.Contains("AttributeLogicalName(\"name\")", output);
        // Reference-type columns are nullable (an image column can be absent/null) so the #nullable-enable
        // generated file does not warn CS8618.
        Assert.Contains("string? Name", output);
        Assert.Contains("AttributeLogicalName(\"revenue\")", output);
        Assert.Contains("Revenue", output);
        // A column that was NOT captured must not appear on the projection.
        Assert.DoesNotContain("telephone1", output);
    }

    [Fact]
    public void Projection_implements_IImageProjection_and_an_explicit_cast_to_the_entity()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated() { }
    }
}
");

        // The projection carries its source image via the marker interface, so the cast is lossless (not a
        // reconstruction): it implements IImageProjection with an explicit Source.
        Assert.Contains("class OnUpdated_PostImage : global::Vela.Context.IImageProjection", output);
        Assert.Contains("global::Microsoft.Xrm.Sdk.Entity? global::Vela.Context.IImageProjection.Source", output);
        // The widening is an EXPLICIT operator to the entity (never implicit - that would reopen the silent-null
        // footgun invisibly at the call site), built from the retained source image.
        Assert.Contains("public static explicit operator global::Demo.Account(OnUpdated_PostImage? projection)", output);
        Assert.Contains("source.ToEntity<global::Demo.Account>()", output);
        // Null-tolerant like GetPostImage<T>(): an absent image casts to null, it does not throw.
        Assert.Contains("Source: { } source", output);
        Assert.DoesNotContain("implicit operator", output);
    }

    [Fact]
    public void Value_type_columns_are_projected_as_nullable()   // #309 L4
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class ValueTypeAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.NumberOfEmployees)
                .Handle(() => { });
    }
}
");
        // A non-nullable value-type column becomes T? so an ABSENT image column reads null, not its default (0).
        Assert.Contains("int? NumberOfEmployees", output);
        Assert.DoesNotContain("int NumberOfEmployees", output); // never the non-nullable form
    }

    [Fact]
    public void Colliding_lambda_projections_keep_the_first_name_plain_and_suffix_the_rest()   // #309 L3
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class CollisionLambda : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Revenue).Handle(() => { });
        }
    }
}
");
        // Two lambda steps on the same message/stage with different columns collide on the projection name. The FIRST
        // keeps the plain, guessable name; only the later one gets a stable hash suffix.
        Assert.Matches(@"class UpdatePostOp_PostImage(?![_0-9A-Za-z])", output); // plain name kept
        Assert.Contains("class UpdatePostOp_PostImage_", output);               // the other is suffixed
    }

    [Fact]
    public void Emits_nothing_for_a_capture_all_image_with_no_columns()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class CaptureAll : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage()
                .Handle(() => { });
    }
}
");

        Assert.True(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public void Uses_the_alias_in_the_type_name()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AliasedImage : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(""Snapshot"", a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class AliasedImage", output);
        Assert.Contains("class UpdatePostOp_PreImage_Snapshot", output);
    }

    [Fact]
    public void Emits_a_projection_for_the_typed_on_request_path()
    {
        // On<DeleteRequest>() derives the message "Delete" from the request type name.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class TypedDelete : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class TypedDelete", output);
        Assert.Contains("class DeletePreOp_PreImage", output);
        Assert.Contains("AttributeLogicalName(\"name\")", output);
    }

    [Fact]
    public void Emits_a_projection_for_a_bulk_step()
    {
        // OnUpdateMultiple() maps to the "UpdateMultiple" message (lambda handler -> fallback name).
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class BulkAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class BulkAudit", output);
        Assert.Contains("class UpdateMultiplePostOp_PostImage", output);
    }

    [Fact]
    public void Names_the_projection_after_the_named_handler_method()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class NamedHandlerPlugin : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated() { }
    }
}
");

        Assert.Contains("partial class NamedHandlerPlugin", output);
        Assert.Contains("class OnUpdated_PostImage", output);
        Assert.DoesNotContain("NamedHandlerPlugin_OnUpdated", output);
    }

    [Fact]
    public void Emits_a_projection_for_the_permissive_generic_message_path()
    {
        // OnMessage<TMessage>() derives the message from the marker type name, same rule as On<TRequest>().
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class Merge { }
    public sealed class GenericMessageAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<Merge>().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class GenericMessageAudit", output);
        Assert.Contains("class MergePostOp_PostImage", output);
    }

    [Fact]
    public void An_unrelated_edit_reuses_the_cached_projections_and_contexts()
    {
        // The incremental pipeline compares its models (columns, projections, handlers) by value: an edit elsewhere in
        // the compilation must leave every generated source cached rather than regenerated.
        string plugin = @"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage(""before"", a => a.Revenue).WithPostImage(a => a.Name, a => a.NumberOfEmployees).Handle(OnUpdated);
            RegisterStep().OnCreate().PostOperation().Sync().WithPostImage(a => a.Telephone1).Handle(OnCreated);
        }
        private void OnUpdated() { }
        private void OnCreated() { }
    }
}
";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "gen_cache_test",
            new[] { CSharpSyntaxTree.ParseText(Harness + plugin) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ImageProjectionGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        int generated = driver.GetRunResult().Results.Single().GeneratedSources.Length;

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Other { public class Unrelated { } }")));
        GeneratorRunResult second = driver.GetRunResult().Results.Single();

        Assert.True(generated >= 3); // two projections + at least one context
        Assert.Equal(generated, second.GeneratedSources.Length);
        Assert.All(
            second.TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(run => run.Outputs),
            output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, output.Reason.ToString()));
    }

    [Fact]
    public void Resolves_a_parenthesized_receiver()
    {
        // a => (a).Name is a.Name: the parentheses vanish from the expression tree, and VELA001 accepts it.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountParen : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => (a).Name).Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.Contains("class OnUpdated_PostImage", output);
    }

    [Fact]
    public void Emits_nothing_for_a_nested_path_selector()
    {
        // a.Parent.Name is another record's column (VELA001), never a column of this image.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountNested : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Parent.Name).Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.DoesNotContain("class OnUpdated_PostImage", output);
    }

    [Fact]
    public void Resolves_a_column_selector_behind_a_cast()
    {
        // a => (object)a.Name must not suppress the projection: the cast is unwrapped to the member access.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class CastAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPostImage(a => (object)a.Name, a => a.Revenue as object)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class CastAudit", output);
        Assert.Contains("class UpdatePostOp_PostImage", output);
        Assert.Contains("AttributeLogicalName(\"name\")", output);
        Assert.Contains("AttributeLogicalName(\"revenue\")", output);
    }

    [Fact]
    public void Emits_both_projections_when_one_named_handler_binds_two_different_shapes()
    {
        // Two steps bind the same named handler with different captured columns: both projections must be emitted
        // (the later one must not collapse into the earlier by name), disambiguated by a column-shape hash.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class SharedHandlerPlugin : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(Shared);
            RegisterStep().OnUpdate().PreOperation().Sync().WithPostImage(a => a.Revenue).Handle(Shared);
        }

        private void Shared() { }
    }
}
");

        // Both captured columns survive - the second shape is not dropped by the dedup.
        Assert.Contains("AttributeLogicalName(\"name\")", output);
        Assert.Contains("AttributeLogicalName(\"revenue\")", output);
        // Two distinct projection types are generated for the one handler name.
        int typeCount = System.Text.RegularExpressions.Regex.Matches(output, @"class Shared_PostImage").Count;
        Assert.Equal(2, typeCount);
    }

    [Fact]
    public void Nests_the_projection_in_the_plugin_partial_class_without_a_global_using()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class NestedPlugin : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated() { }
    }
}
");

        // The projection lives in the plugin's own namespace, nested in a partial reopening of the plugin class,
        // so the handler reads it unqualified; the separate Generated.Images namespace and its global using are gone.
        Assert.Contains("namespace Demo", output);
        Assert.Contains("partial class NestedPlugin", output);
        Assert.Contains("class OnUpdated_PostImage", output);
        Assert.DoesNotContain("Generated.Images", output);
        Assert.DoesNotContain("global using", output);
    }

    [Fact]
    public void Generated_nested_projection_compiles_with_a_sealed_partial_primary_ctor_plugin()
    {
        // The real-world shape: a sealed partial plugin with a primary constructor, reading the short nested
        // projection name unqualified. The generated partial part must coexist with it (no CS0260, no primary-ctor
        // conflict) and the short name must resolve from inside the handler.
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAudit(string unsecure, string secure) : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated()
        {
            OnUpdated_PostImage image = new OnUpdated_PostImage();
            _ = image.Name;
        }
    }
}
");

        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generated_explicit_cast_from_the_projection_to_the_entity_compiles()
    {
        // The widening operator the generator emits must compile and be invocable: (Account)projection.
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    using Microsoft.Xrm.Sdk;
    public sealed partial class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated()
        {
            OnUpdated_PostImage image = new OnUpdated_PostImage();
            Account full = (Account)image;   // explicit widening via the generated operator
            _ = full;
        }
    }
}
");

        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_non_partial_plugin_that_captures_a_typed_image_fails_to_compile()
    {
        // Why VELA014 exists: without partial, the generated partial reopening of the class collides with the
        // developer's non-partial declaration (CS0260).
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains(diagnostics, d => d.Id == "CS0260");
    }

    [Fact]
    public void Emits_a_typed_step_context_for_update_post_with_pre_and_post()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPreImage(a => a.Revenue)
                .WithPostImage(a => a.Name, a => a.Revenue)
                .Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.Contains("partial class AccountAudit", output);
        Assert.Contains("class OnUpdatedContext : global::Vela.Context.UpdatePostContext<global::Demo.Account>", output);
        Assert.Contains("public new OnUpdated_PreImage? PreImage", output);
        Assert.Contains("public new OnUpdated_PostImage? PostImage", output);
    }

    [Fact]
    public void A_typed_context_reads_each_image_under_its_alias()
    {
        // The step registers the images as "before" / "snap": reading them under the default aliases returned null.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPreImage(""before"", a => a.Revenue)
                .WithPostImage(""snap"", a => a.Name)
                .Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.Contains("PreImageAs<OnUpdated_PreImage_before>(this, \"before\")", output);
        Assert.Contains("PostImageAs<OnUpdated_PostImage_snap>(this, \"snap\")", output);
    }

    [Fact]
    public void A_constant_alias_is_resolved_and_a_non_constant_one_generates_nothing()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAudit : EntityPluginBase<Account>
    {
        private const string Snap = ""snap"";
        private static readonly string Dynamic = ""dyn"";
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(Snap, a => a.Name).Handle(OnUpdated);
            RegisterStep().OnDelete().PreOperation().Sync().WithPreImage(Dynamic, a => a.Name).Handle(OnDeleted);
        }
        private void OnUpdated() { }
        private void OnDeleted() { }
    }
}
");

        Assert.Contains("PostImageAs<OnUpdated_PostImage_snap>(this, \"snap\")", output);
        Assert.DoesNotContain("OnDeleted_PreImage", output); // the runtime alias is unknown: no projection reading null
    }

    [Fact]
    public void A_merge_context_exposes_the_master_and_subordinate_projections()   // #462
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountMerge : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMerge().PostOperation().Sync()
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Revenue)
                .WithSubordinatePreImage(a => a.Telephone1)
                .WithSubordinatePostImage(""dupAfter"", a => a.NumberOfEmployees)
                .Handle(OnMerged);
            RegisterStep().OnMerge().PreOperation().Sync()
                .WithSubordinatePreImage(a => a.Name)
                .Handle(OnMerging);
        }
        private void OnMerged() { }
        private void OnMerging() { }
    }
}
");

        Assert.Contains("class OnMergedContext : global::Vela.Context.MergePostContext<global::Demo.Account>", output);
        Assert.Contains("public new OnMerged_PreImage? PreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<OnMerged_PreImage>(this);", output);
        Assert.Contains("public new OnMerged_PostImage? PostImage", output);
        // A subordinate image always names its alias: the accessor's default alias is the master's.
        Assert.Contains("public new OnMerged_SubordinatePreImage? SubordinatePreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<OnMerged_SubordinatePreImage>(this, \"SubordinatePreImage\");", output);
        Assert.Contains("public new OnMerged_SubordinatePostImage_dupAfter? SubordinatePostImage => global::Vela.Context.ImageProjectionExtensions.PostImageAs<OnMerged_SubordinatePostImage_dupAfter>(this, \"dupAfter\");", output);
        Assert.Contains(" Telephone1 { get; set; }", output);

        Assert.Contains("class OnMergingContext : global::Vela.Context.MergePreContext<global::Demo.Account>", output);
        Assert.Contains("public new OnMerging_SubordinatePreImage? SubordinatePreImage", output);
        Assert.DoesNotContain("OnMerging_PreImage", output);
    }

    [Fact]
    public void An_assign_context_exposes_the_pre_and_post_projections()   // #475
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAssign : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnAssign().PostOperation().Sync()
                .WithPreImage(a => a.Name)
                .WithPostImage(""after"", a => a.Revenue)
                .Handle(OnAssigned);
            RegisterStep().OnAssign().PreValidation().Sync()
                .WithPreImage(a => a.Telephone1)
                .Handle(OnAssigning);
        }
        private void OnAssigned() { }
        private void OnAssigning() { }
    }
}
");

        Assert.Contains("class OnAssignedContext : global::Vela.Context.AssignPostContext<global::Demo.Account>", output);
        Assert.Contains("public new OnAssigned_PreImage? PreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<OnAssigned_PreImage>(this);", output);
        Assert.Contains("public new OnAssigned_PostImage_after? PostImage => global::Vela.Context.ImageProjectionExtensions.PostImageAs<OnAssigned_PostImage_after>(this, \"after\");", output);

        Assert.Contains("class OnAssigningContext : global::Vela.Context.AssignPreContext<global::Demo.Account>", output);
        Assert.Contains("public new OnAssigning_PreImage? PreImage", output);
        Assert.Contains(" Telephone1 { get; set; }", output);
    }

    [Fact]
    public void An_assign_capture_compiles_without_VELA021()   // #475
    {
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAssign : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssign().PostOperation().Sync().WithPreImage(a => a.Name).WithPostImage(a => a.Name).Handle(() => { });
        private static string Read(AssignPostOp_PreImage pre, AssignPostOp_PostImage post) => pre.Name + post.Name;
    }
}
");

        Assert.DoesNotContain(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_typed_context_with_a_default_alias_passes_no_alias()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountCreate : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(OnCreated);
        private void OnCreated() { }
    }
}
");

        Assert.Contains("PostImageAs<OnCreated_PostImage>(this);", output);
    }

    [Fact]
    public void Emits_a_post_only_context_for_create_post()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountCreate : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(OnCreated);
        private void OnCreated() { }
    }
}
");

        Assert.Contains("class OnCreatedContext : global::Vela.Context.CreatePostContext<global::Demo.Account>", output);
        Assert.Contains("public new OnCreated_PostImage? PostImage", output);
        Assert.DoesNotContain("PreImage =>", output);   // no pre-image member on a create-post context
    }

    [Fact]
    public void Emits_a_pre_only_context_for_delete_pre()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountDelete : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnDeleting);
        private void OnDeleting() { }
    }
}
");

        Assert.Contains("class OnDeletingContext : global::Vela.Context.DeletePreContext<global::Demo.Account>", output);
        Assert.Contains("public new OnDeleting_PreImage? PreImage", output);
        Assert.DoesNotContain("PostImage =>", output);   // no post-image member on a delete-pre context
    }

    // #436: a pre-image is valid at PreValidation; the typed projection and context are the same as at PreOperation.
    [Fact]
    public void Emits_a_typed_pre_image_context_for_update_prevalidation()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountGuard : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreValidation().Sync()
                .FilterOn(a => a.Revenue)
                .WithPreImage(a => a.Revenue)
                .Handle(OnValidating);
        private void OnValidating() { }
    }
}
");

        Assert.Contains("class OnValidating_PreImage", output);
        Assert.Contains("AttributeLogicalName(\"revenue\")", output);
        Assert.DoesNotContain("telephone1", output);
        Assert.Contains("class OnValidatingContext : global::Vela.Context.UpdatePreContext<global::Demo.Account>", output);
        Assert.Contains("public new OnValidating_PreImage? PreImage", output);
    }

    [Fact]
    public void Emits_a_typed_pre_image_context_for_delete_prevalidation()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountDeleteGuard : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreValidation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnDeleteValidating);
        private void OnDeleteValidating() { }
    }
}
");

        Assert.Contains("class OnDeleteValidatingContext : global::Vela.Context.DeletePreContext<global::Demo.Account>", output);
        Assert.Contains("public new OnDeleteValidating_PreImage? PreImage", output);
    }

    [Fact]
    public void Names_a_lambda_prevalidation_projection_after_message_and_stage()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountGuard : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreValidation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("class UpdatePreVal_PreImage", output);
    }

    [Fact]
    public void Emits_a_bare_context_for_an_imageless_named_handler()
    {
        // The stable-signature case: a named handler with no captured image still gets a {Handler}Context. Adding a
        // WithPreImage/WithPostImage later enriches this same type with a typed PreImage/PostImage - the handler
        // signature never changes.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountValidate : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.Contains("class OnUpdatedContext : global::Vela.Context.UpdatePostContext<global::Demo.Account>", output);
        // No captured image, so no shadowed image members on the bare context.
        Assert.DoesNotContain("PreImage =>", output);
        Assert.DoesNotContain("PostImage =>", output);
    }

    [Fact]
    public void Emits_a_bare_context_for_a_bulk_handler()
    {
        // Every stage now carries Handle(Delegate), so a bulk handler gets a {Handler}Context too - a bare subclass
        // of the bulk message context (bulk images are per-record, so no single PreImage/PostImage member).
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountBulk : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreateMultiple().PostOperation().Sync().Handle(OnCreatedMany);
        private void OnCreatedMany() { }
    }
}
");

        Assert.Contains("class OnCreatedManyContext : global::Vela.Context.CreateMultiplePostContext<global::Demo.Account>", output);
        Assert.DoesNotContain("PreImage =>", output);
        Assert.DoesNotContain("PostImage =>", output);
    }

    [Fact]
    public void A_bulk_handler_context_lists_the_steps_own_images_per_target()
    {
        // Verified live: bulk images reach the plugin per target through the image collections, which can also hold
        // other steps' and platform images, so the generated lists select the step's own alias.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountBulkUpdate : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(""Before"", a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(OnUpdatedMany);
        private void OnUpdatedMany() { }
    }
}
");

        Assert.Contains("class OnUpdatedManyContext : global::Vela.Context.UpdateMultiplePostContext<global::Demo.Account>", output);
        Assert.Contains("IReadOnlyList<OnUpdatedMany_PreImage_Before?> PreImages => global::Vela.Context.ImageProjectionExtensions.PreImagesAs<OnUpdatedMany_PreImage_Before>(this, \"Before\")", output);
        Assert.Contains("PostImages => global::Vela.Context.ImageProjectionExtensions.PostImagesAs<OnUpdatedMany_PostImage>(this)", output);
        Assert.DoesNotContain("PreImage =>", output);
    }

    [Fact]
    public void Emits_a_bare_context_for_a_non_crud_modelled_message()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountAssign : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssign().PostOperation().Sync().Handle(OnAssigned);
        private void OnAssigned() { }
    }
}
");

        Assert.Contains("class OnAssignedContext : global::Vela.Context.AssignPostContext<global::Demo.Account>", output);
    }

    [Fact]
    public void Does_not_emit_a_context_for_a_non_modelled_trigger()
    {
        // A trigger with no matching Vela.Context.{Msg}{Pre|Post}Context type (e.g. a generated Custom API On{X}
        // extension) gets no context - the type-existence check excludes it even though the name strips to a message.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountRecalc : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnRecalc().PostOperation().Sync().Handle(OnRecalced);
        private void OnRecalced() { }
    }
}
");

        Assert.DoesNotContain("OnRecalcedContext", output);
        Assert.DoesNotContain("RecalcPostContext", output);
    }

    [Fact]
    public void Emits_a_pre_stage_context_for_a_typed_on_request_handler()
    {
        // Every typed config stage now carries Handle(Delegate), so a pre-stage typed handler gets a context too,
        // deriving from the pre-stage generic message context.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class AccountTypedPre : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PreOperation().Sync().Handle(OnDeleting);
        private void OnDeleting() { }
    }
}
");

        Assert.Contains("class OnDeletingContext : global::Vela.Context.MessagePreContext<global::Demo.DeleteRequest, global::Demo.Account>", output);
    }

    [Fact]
    public void Does_not_emit_a_context_for_a_lambda_handler()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class LambdaAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        // The projection is still emitted, but no typed step context (a lambda has no name to key it).
        Assert.Contains("_PostImage", output);
        Assert.DoesNotContain("Context : global::Vela.Context", output);
    }

    [Fact]
    public void Emits_a_message_context_for_the_typed_on_request_path()
    {
        // On<DeleteRequest>() is the typed path: the context derives from the generic message context
        // MessagePostContext<TRequest, TEntity>, not the guided DeletePostContext.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class TypedAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PostOperation().Sync()
                .WithPreImage(a => a.Name)
                .WithPostImage(a => a.Revenue)
                .Handle(OnDeleted);
        private void OnDeleted() { }
    }
}
");

        Assert.Contains("class OnDeletedContext : global::Vela.Context.MessagePostContext<global::Demo.DeleteRequest, global::Demo.Account>", output);
        Assert.Contains("public new OnDeleted_PreImage? PreImage", output);
        Assert.Contains("public new OnDeleted_PostImage? PostImage", output);
    }

    [Fact]
    public void Typed_message_context_compiles()
    {
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class TypedAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().On<DeleteRequest>().PreOperation().Sync()
                .WithPreImage(a => a.Name)
                .Handle(OnDeleting);

        private void OnDeleting() { }   // generated OnDeletingContext (MessagePreContext<DeleteRequest, Account>) is compiled with the sources
    }
}
");

        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generated_context_compiles_when_a_handler_name_collides()
    {
        // Two steps bind the same named handler with different captured columns: the projection types are
        // hash-disambiguated. The generated context must reference the emitted (hashed) projection name, not the
        // original, or it fails with CS0246.
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class CollisionAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(Shared);
            RegisterStep().OnUpdate().PreOperation().Sync().WithPostImage(a => a.Revenue).Handle(Shared);
        }

        private void Shared() { }
    }
}
");

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS0246");
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generated_parts_compile_for_a_generic_plugin_nested_in_a_record_struct()
    {
        // A-77: the generator reopened every containing type as 'partial class Name', which for a struct / record
        // container or a generic plugin declares a different type (CS0261 / CS0260 / a missing type parameter).
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public partial record struct Outer
    {
        public sealed partial class Audit<TMarker> : EntityPluginBase<Account>
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(OnUpdated);
            private void OnUpdated() { }
            private static string Read(OnUpdatedContext ctx) => ctx.PostImage.Name;
        }
    }
}
");

        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Projects_a_column_without_AttributeLogicalName_under_its_lowercased_name()
    {
        // The runtime resolves such a property to its lower-cased name, so the generator does the same rather than
        // silently generating nothing.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountPlain : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Parent).Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.Contains("class OnUpdated_PostImage", output);
        Assert.Contains("AttributeLogicalName(\"parent\")", output);
    }

    [Fact]
    public void Reports_VELA021_when_a_non_constant_alias_prevents_the_projection()
    {
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountDyn : EntityPluginBase<Account>
    {
        private static readonly string Dynamic = ""dyn"";
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreOperation().Sync().WithPreImage(Dynamic, a => a.Name).Handle(OnDeleted);
        private void OnDeleted() { }
    }
}
");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("alias is not a compile-time constant", warning.GetMessage());
        Assert.Contains("GetPreImage", warning.GetMessage());
    }

    [Fact]
    public void Reports_VELA021_when_a_selector_is_not_a_column_property()
    {
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountCall : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name, a => a.GetHashCode()).Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.Contains("a => a.GetHashCode()", warning.GetMessage());
    }

    [Fact]
    public void Reports_VELA021_when_the_message_of_a_RegisterStep_chain_cannot_be_read()
    {
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountVar : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }
}
");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.Contains("message or stage", warning.GetMessage());
    }

    [Fact]
    public void Does_not_report_VELA021_for_a_selector_an_analyzer_already_reports()
    {
        // A nested path is VELA001's error; the generator skips it without a second diagnostic. A chain split
        // across locals (trigger not visible here) is not reported either.
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    using Vela.Steps;
    public sealed partial class AccountNested : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Parent.Name).Handle(() => { });
            IStepConfig<Account> split = RegisterStep().OnUpdate();
            split.PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
        }
    }
}
");

        Assert.DoesNotContain(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
    }

    [Fact]
    public void Ignores_a_same_named_fluent_marker_outside_Vela_Steps()
    {
        // A-77: the fluent marker is matched by symbol (Vela.Steps.IFluentStep), not by its simple name.
        string output = RunAndConcat(@"
namespace Other
{
    public interface IFluentStep { }
    public interface IOtherStep : IFluentStep
    {
        IOtherStep OnUpdate();
        IOtherStep PostOperation();
        IOtherStep WithPostImage(params System.Linq.Expressions.Expression<System.Func<Demo.Account, object>>[] a);
        void Handle(System.Action handler);
    }
}

namespace Demo
{
    using Vela.Plugin;
    public sealed partial class Lookalike : EntityPluginBase<Account>
    {
        private Other.IOtherStep Other() => null;
        protected override void RegisterPluginSteps()
            => Other().OnUpdate().PostOperation().WithPostImage(a => a.Name).Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Assert.DoesNotContain("OnUpdated_PostImage", output);
        Assert.DoesNotContain("OnUpdatedContext", output);
    }

    [Fact]
    public void Generated_parts_keep_the_variance_of_a_generic_interface_container()
    {
        // A partial interface must repeat its type parameters' variance (CS1067 otherwise). C# itself forbids a class
        // inside a variant interface (CS8427, the user's own error), but the generated part must not add CS1067.
        string source = @"
namespace Demo
{
    using Vela.Plugin;
    public partial interface IBox<out TItem, in TKey>
    {
        public sealed partial class Audit : EntityPluginBase<Account>
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(OnUpdated);
            private void OnUpdated() { }
        }
    }
}
";
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(source);

        Assert.Contains("partial interface IBox<out TItem,in TKey>", RunAndConcat(source));
        Assert.DoesNotContain(diagnostics, d => d.Id == "CS1067");
    }

    [Fact]
    public void Reports_VELA021_for_a_column_selector_held_in_a_variable()
    {
        // Not an inline lambda: the captured columns are unknown, which is not a capture-all image.
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using System;
    using System.Linq.Expressions;
    using Vela.Plugin;
    public sealed partial class AccountVarColumn : EntityPluginBase<Account>
    {
        private static readonly Expression<Func<Account, object>> Column = a => a.Name;
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(Column).Handle(OnUpdated);
        private void OnUpdated() { }
    }
}
");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.Contains("not an inline selector lambda", warning.GetMessage());
    }

    [Theory]
    [InlineData("WithSubordinatePreImage(Column)", "ctx.GetPreImage<T>(\"SubordinatePreImage\")")]
    [InlineData("WithSubordinatePostImage(Column)", "ctx.GetPostImage<T>(\"SubordinatePostImage\")")]
    [InlineData("WithSubordinatePreImage(\"dupBefore\", Column)", "ctx.GetPreImage<T>(\"dupBefore\")")]
    [InlineData("WithSubordinatePostImage(Dynamic, a => a.Name)", "ctx.GetPostImage<T>(Dynamic)")]
    [InlineData("WithPreImage(\"before\", Column)", "ctx.GetPreImage<T>(\"before\")")]
    [InlineData("WithPreImage(Dynamic, a => a.Name)", "ctx.GetPreImage<T>(Dynamic)")]
    [InlineData("WithPostImage(Column)", "ctx.GetPostImage<T>()")]
    public void VELA021_recommends_the_full_image_read_with_the_alias_the_image_needs(string capture, string accessor)
    {
        // A subordinate image never has the master's default alias, so the plain read must name it.
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using System;
    using System.Linq.Expressions;
    using Vela.Plugin;
    public sealed partial class AccountMergeSkip : EntityPluginBase<Account>
    {
        private static readonly Expression<Func<Account, object>> Column = a => a.Name;
        private static readonly string Dynamic = ""dyn"";
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMerge().PostOperation().Sync()." + capture + @".Handle(OnMerged);
        private void OnMerged() { }
    }
}
");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.EndsWith("; read the full image with " + accessor + " instead, or fix the capture", warning.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_subordinate_projection_documents_the_read_with_its_alias()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountMergeDoc : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMerge().PostOperation().Sync()
                .WithSubordinatePreImage(a => a.Name)
                .WithSubordinatePostImage(""dupAfter"", a => a.Name)
                .Handle(OnMerged);
        private void OnMerged() { }
    }
}
");

        Assert.Contains("Read this via ctx.PreImageAs&lt;T&gt;(\"SubordinatePreImage\").", output);
        Assert.Contains("Read this via ctx.PostImageAs&lt;T&gt;(\"dupAfter\").", output);
        Assert.DoesNotContain("Read this via ctx.PreImageAs&lt;T&gt;() / ctx.PostImageAs&lt;T&gt;().", output);
    }

    [Fact]
    public void Emits_a_projection_for_a_marker_OnMessage_without_VELA021()
    {
        ImmutableArray<Diagnostic> diagnostics = CompileDiagnostics(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class Update { }
    public sealed partial class AccountConstMessage : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<Update>().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
        private static string Read(UpdatePostOp_PostImage image) => image.Name;
    }
}
");

        Assert.DoesNotContain(diagnostics, d => d.Id == ImageProjectionGenerator.NotGeneratedDiagnosticId);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static ImmutableArray<Diagnostic> CompileDiagnostics(string pluginSource)
    {
        string source = Harness + pluginSource;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "gen_compile_test",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ImageProjectionGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out ImmutableArray<Diagnostic> generatorDiagnostics);

        return updated.GetDiagnostics().AddRange(generatorDiagnostics);
    }
}
