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
        IStepConfig<TEntity> OnUpdateMultiple();
        IStepConfig<TEntity> On<TRequest>();
        IStepConfig<TEntity> OnMessage(string messageName);
        IStepConfig<TEntity> OnMessage<TMessage>();
        IStepConfig<TEntity> PreOperation();
        IStepConfig<TEntity> PostOperation();
        IStepConfig<TEntity> Sync();
        IStepConfig<TEntity> FilterOn(Expression<Func<TEntity, object>> a);
        IStepConfig<TEntity> WithPreImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPreImage(string alias, params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPostImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPostImage(string alias, params Expression<Func<TEntity, object>>[] a);
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
    public static class ImageProjectionExtensions
    {
        public static TImage PreImageAs<TImage>(this ILocalPluginContext context) => default!;
        public static TImage PostImageAs<TImage>(this ILocalPluginContext context) => default!;
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
        Assert.Contains("public static explicit operator global::Demo.Account(OnUpdated_PostImage projection)", output);
        Assert.Contains("Source!.ToEntity<global::Demo.Account>()", output);
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
    public void Emits_a_projection_for_the_permissive_string_message_path()
    {
        // OnMessage("update") is the permissive/string trigger: the message is the string literal, capitalized
        // into the type-name segment (update -> Update), so a string-triggered step still gets a typed image.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class PermissiveAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage(""update"").PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class PermissiveAudit", output);
        Assert.Contains("class UpdatePostOp_PostImage", output);
        Assert.Contains("AttributeLogicalName(\"name\")", output);
    }

    [Fact]
    public void Emits_a_projection_for_a_custom_message_string()
    {
        // A non-standard message string (a custom message) sanitizes and capitalizes into a legal type-name
        // segment, so custom-message steps get a typed image too.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class CustomMessageAudit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage(""gh_recalculate"").PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("partial class CustomMessageAudit", output);
        Assert.Contains("class Gh_recalculatePostOp_PostImage", output);
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
    public void Emits_nothing_for_a_non_literal_message()
    {
        // A message passed as a variable/const is not statically resolvable, so no projection is generated
        // (the developer keeps the full early-bound image), consistent with the unresolved-capture fallback.
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class DynamicMessageAudit : EntityPluginBase<Account>
    {
        private const string Msg = ""update"";
        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage(Msg).PostOperation().Sync()
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.True(string.IsNullOrWhiteSpace(output));
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
