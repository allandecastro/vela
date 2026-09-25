using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.PartialPluginForTypedImageAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class PartialPluginForTypedImageAnalyzerTests
{
    // Minimal Vela surface the analyzer inspects: a fluent step config (implementing Vela.Steps.IFluentStep) with
    // the image capture methods and a Handle terminal, EntityPluginBase<TEntity>, an Entity base and a sample entity.
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Microsoft.Xrm.Sdk { public class Entity { } }

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStepConfig<TEntity> : IFluentStep
    {
        IStepConfig<TEntity> OnUpdate();
        IStepConfig<TEntity> PostOperation();
        IStepConfig<TEntity> Sync();
        IStepConfig<TEntity> WithPreImage(params Expression<Func<TEntity, object>>[] a);
        IStepConfig<TEntity> WithPostImage(params Expression<Func<TEntity, object>>[] a);
        void Handle(Action handler);
    }
}

namespace Vela.Plugin
{
    using Vela.Steps;
    public abstract class EntityPluginBase<TEntity> where TEntity : Microsoft.Xrm.Sdk.Entity
    {
        protected IStepConfig<TEntity> RegisterStep() => null!;
        protected virtual void RegisterPluginSteps() { }
    }
}

namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public sealed class Account : Entity { public string Name { get; set; } }
}
";

    private static string InPlugin(string pluginSource) => Harness + @"
namespace Demo
{
    using Vela.Plugin;
    " + pluginSource + @"
}
";

    private static DiagnosticResult At(int marker, string className, string plugin) =>
        new DiagnosticResult(Vela.Analyzers.PartialPluginForTypedImageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(marker)
            .WithArguments(className, plugin);

    [Fact]
    public async Task Flags_a_non_partial_plugin_that_captures_a_typed_image()
    {
        string code = InPlugin(@"
    public sealed class {|#0:Audit|} : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }");

        await Verify.VerifyAnalyzerAsync(code, At(0, "Audit", "Audit"));
    }

    [Fact]
    public async Task Flags_a_non_partial_outer_class_of_a_nested_plugin()
    {
        // Both the outer class and the plugin lack partial: the generated projection nests inside both, so both fail
        // to compile without partial. Each is reported.
        string code = InPlugin(@"
    public class {|#0:Outer|}
    {
        public sealed class {|#1:Audit|} : EntityPluginBase<Account>
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
        }
    }");

        await Verify.VerifyAnalyzerAsync(code, At(0, "Outer", "Audit"), At(1, "Audit", "Audit"));
    }

    [Fact]
    public async Task Flags_the_outer_class_even_when_the_plugin_is_partial()
    {
        // The plugin being partial is not enough: a non-partial enclosing class still breaks the build (CS0260).
        string code = InPlugin(@"
    public class {|#0:Outer|}
    {
        public sealed partial class Audit : EntityPluginBase<Account>
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
        }
    }");

        await Verify.VerifyAnalyzerAsync(code, At(0, "Outer", "Audit"));
    }

    [Fact]
    public async Task Does_not_flag_a_partial_plugin_that_captures_a_typed_image()
    {
        string code = InPlugin(@"
    public sealed partial class Audit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }");

        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_capture_all_image_with_no_column_selectors()
    {
        // WithPreImage() with no selectors keeps the full entity and generates no projection, so no nested type is
        // emitted and the class does not need to be partial.
        string code = InPlugin(@"
    public sealed class Audit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPreImage().Handle(() => { });
    }");

        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_plugin_without_any_typed_image_capture()
    {
        string code = InPlugin(@"
    public sealed class NoImages : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().Handle(() => { });
    }");

        await Verify.VerifyAnalyzerAsync(code);
    }
}
