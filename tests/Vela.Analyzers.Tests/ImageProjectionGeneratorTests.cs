using System;
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
    public class Entity { }
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class AttributeLogicalNameAttribute : Attribute { public AttributeLogicalNameAttribute(string logicalName) { } }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStepConfig<TEntity> : IFluentStep
    {
        IStepConfig<TEntity> OnUpdate();
        IStepConfig<TEntity> On<TRequest>();
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

namespace Demo
{
    using Microsoft.Xrm.Sdk;
    using Vela.Plugin;

    public sealed class Account : Entity
    {
        [AttributeLogicalName(""name"")] public string Name { get; set; }
        [AttributeLogicalName(""revenue"")] public decimal? Revenue { get; set; }
        [AttributeLogicalName(""telephone1"")] public string Telephone1 { get; set; }
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

        Assert.Contains("namespace Demo.Generated.Images", output);
        Assert.Contains("class AccountAudit_UpdatePostOp_PostImage", output);
        Assert.Contains("AttributeLogicalName(\"name\")", output);
        Assert.Contains("Name", output);
        Assert.Contains("AttributeLogicalName(\"revenue\")", output);
        Assert.Contains("Revenue", output);
        // A column that was NOT captured must not appear on the projection.
        Assert.DoesNotContain("telephone1", output);
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

        Assert.DoesNotContain("Generated.Images", output);
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

        Assert.Contains("class AliasedImage_UpdatePostOp_PreImage_Snapshot", output);
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

        Assert.Contains("class TypedDelete_DeletePreOp_PreImage", output);
        Assert.Contains("AttributeLogicalName(\"name\")", output);
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

        Assert.Contains("class NamedHandlerPlugin_OnUpdated_PostImage", output);
    }

    [Fact]
    public void Emits_a_global_using_for_the_generated_namespace()
    {
        string output = RunAndConcat(@"
namespace Demo
{
    using Vela.Plugin;
    public sealed class GlobalUsingPlugin : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPostImage(a => a.Name)
                .Handle(() => { });
    }
}
");

        Assert.Contains("global using global::Demo.Generated.Images;", output);
    }
}
