using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Vela.Analyzers.Tests;

// The stand-in Vela surface the ImageProjectionGenerator mutation tests compile against (the same shape as the
// harness in ImageProjectionGeneratorTests), plus a runner that returns every generated file by hint name, the
// generator diagnostics and the compilation errors of the generated output.
internal static class GeneratorHarness
{
    internal const string Source = @"
using System;
using System.Linq.Expressions;

namespace Microsoft.Xrm.Sdk
{
    public class Entity
    {
        public virtual System.Guid Id { get; set; }
        public string LogicalName { get; set; }
        public T ToEntity<T>() where T : Entity, new() => new T();
    }
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class RelationshipSchemaNameAttribute : Attribute { public RelationshipSchemaNameAttribute(string schemaName) { } }
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
        IStepConfig<TEntity> OnRecalc();
        IStepConfig<TEntity> On<TRequest>();
        IStepConfig<TEntity> OnMessage(string messageName);
        IStepConfig<TEntity> OnMessage<TMessage>();
        IStepConfig<TEntity> PreOperation();
        IStepConfig<TEntity> PostOperation();
        IStepConfig<TEntity> Sync();
        IStepConfig<TEntity> PreValidation();
        IStepConfig<TEntity> WithDescription(string description);
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
    public class CreatePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public CreatePreContext(ILocalPluginContext inner) { } }
    public class CreateMultiplePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public CreateMultiplePostContext(ILocalPluginContext inner) { } }
    public class AssignPostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public AssignPostContext(ILocalPluginContext inner) { } }
    public class AssignPreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public AssignPreContext(ILocalPluginContext inner) { } }
    public class CreateMultiplePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public CreateMultiplePreContext(ILocalPluginContext inner) { } }
    public class UpdateMultiplePostContext<TEntity> : ILocalPluginContext where TEntity : Entity { public UpdateMultiplePostContext(ILocalPluginContext inner) { } }
    public class UpdateMultiplePreContext<TEntity> : ILocalPluginContext where TEntity : Entity { public UpdateMultiplePreContext(ILocalPluginContext inner) { } }
    // Vela also ships the entity-less generic message contexts (one type argument), which a context lookup for a
    // 'Message' pseudo-message would wrongly find.
    public sealed class MessagePreContext<TRequest> : ILocalPluginContext { public MessagePreContext(ILocalPluginContext inner) { } }
    public sealed class MessagePostContext<TRequest> : ILocalPluginContext { public MessagePostContext(ILocalPluginContext inner) { } }
    public static class ImageProjectionExtensions
    {
        public static TImage PreImageAs<TImage>(this ILocalPluginContext context, string alias = ""PreImage"") => default!;
        public static TImage PostImageAs<TImage>(this ILocalPluginContext context, string alias = ""PostImage"") => default!;
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

    internal sealed record Run(IReadOnlyDictionary<string, string> Sources, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompileErrors);

    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => p.Length > 0)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    internal static CSharpCompilation Compile(params string[] pluginSources) => CSharpCompilation.Create(
        "gen_mutation_test",
        new[] { CSharpSyntaxTree.ParseText(Source, path: "Harness.cs") }
            .Concat(pluginSources.Select((s, i) => CSharpSyntaxTree.ParseText(s, path: $"Plugin{i}.cs"))),
        References,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    internal static Run Execute(params string[] pluginSources)
    {
        CSharpCompilation compilation = Compile(pluginSources);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ImageProjectionGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out ImmutableArray<Diagnostic> diagnostics);
        GeneratorDriverRunResult result = driver.GetRunResult();
        Dictionary<string, string> sources = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);
        ImmutableArray<Diagnostic> errors = updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return new Run(sources, diagnostics, errors);
    }
}
