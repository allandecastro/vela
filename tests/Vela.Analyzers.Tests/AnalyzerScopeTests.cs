using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Vela.Analyzers.Tests;

// Every analyzer switches itself off in a project that does not reference the Vela / Dataverse types it reasons
// about (a non-plugin project that happens to use the same method names), and does so without throwing. The probe
// code below has the shape of a plugin - a RegisterPluginSteps() method, a fluent chain ending in Handle(...), images,
// filters, a mutable field, a throw, filesystem access, ColumnSet(true), Target writes, HasChanged - but every type is
// its own, so no analyzer may report anything. Each variant defines a subset of the Vela anchors, so an analyzer that
// needs two of them must still stay silent when only one is present.
public sealed class AnalyzerScopeTests
{
    private const string LookAlikePlugin = @"
using System;
using System.IO;
using System.Linq.Expressions;

namespace LookAlike
{
    public class Entity { public string Name { get; set; } public Entity Target { get; set; } }
    public class ColumnSet { public ColumnSet(bool all) { } }
    public class Context { public Entity Target { get; set; } public bool HasChanged(Func<Entity, object> c) => true; }
    public class Step
    {
        public Step OnMessage(string name) => this;
        public Step OnCreate() => this;
        public Step OnUpdate() => this;
        public Step PreOperation() => this;
        public Step PostOperation() => this;
        public Step Async() => this;
        public Step FilterOn(Expression<Func<Entity, object>> a, Expression<Func<Entity, object>> b = null) => this;
        public Step WithPreImage(params Expression<Func<Entity, object>>[] a) => this;
        public Step WithPostImage(params Expression<Func<Entity, object>>[] a) => this;
        public Step WithDescription(string d) => this;
        public void Handle(Action<Context> handler) { }
    }

    public partial class EntityPluginBase<T> { }

    public class Lookalike : EntityPluginBase<Entity>
    {
        private int _count;
        private static Step RegisterStep() => new Step();

        protected void RegisterPluginSteps()
        {
            RegisterStep().OnMessage(""create"").PostOperation().Async()
                .FilterOn(a => a.Name, a => a.Name)
                .WithPreImage(a => a.Name, a => a.Name)
                .WithDescription(""a"").WithDescription(""b"")
                .Handle(OnCreated);
            RegisterStep().OnMessage(""create"").PostOperation().Async().Handle(OnCreated);
            RegisterStep().OnCreate().PreOperation().WithPreImage(a => a.Target).Handle(Static);
            RegisterStep().OnUpdate().PostOperation();
        }

        private void OnCreated(Context ctx)
        {
            _count++;
            ctx.Target.Name = ""changed"";
            if (ctx.HasChanged(a => a.Name)) { }
            var all = new ColumnSet(true);
            string text = File.ReadAllText(""x"");
            using var stream = new FileStream(""x"", FileMode.Open);
            throw new InvalidOperationException(text);
        }

        private static void Static(Context ctx) { }
    }
}
";

    private const string FluentAnchorOnly = @"
namespace Vela.Steps { public interface IFluentStep { } }
";

    private const string ContextAnchorOnly = @"
namespace Vela.Context { public interface ILocalPluginContext { } }
";

    private const string PluginBaseAnchorOnly = @"
namespace Vela.Plugin { public abstract class EntityPluginBase<TEntity> { } }
";

    private const string SdkExceptionOnly = @"
namespace Microsoft.Xrm.Sdk { public class InvalidPluginExecutionException : System.Exception { } public class Entity { } }
";

    public static IEnumerable<object[]> Cases()
    {
        string[] variants = { string.Empty, FluentAnchorOnly, ContextAnchorOnly, PluginBaseAnchorOnly, SdkExceptionOnly };
        foreach (Type analyzer in typeof(ImageProjectionGenerator).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
                     .OrderBy(t => t.Name))
        {
            for (int i = 0; i < variants.Length; i++)
            {
                yield return new object[] { analyzer.Name, i };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task An_analyzer_reports_nothing_and_does_not_throw_without_the_Vela_types(string analyzerName, int variant)
    {
        string[] variants = { string.Empty, FluentAnchorOnly, ContextAnchorOnly, PluginBaseAnchorOnly, SdkExceptionOnly };
        Type type = typeof(ImageProjectionGenerator).Assembly.GetTypes().Single(t => t.Name == analyzerName);
        DiagnosticAnalyzer analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;

        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "scope_test",
            new[] { CSharpSyntaxTree.ParseText(LookAlikePlugin), CSharpSyntaxTree.ParseText(variants[variant]) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        List<Exception> failures = new List<Exception>();
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer),
            new CompilationWithAnalyzersOptions(
                new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                onAnalyzerException: (ex, _, _) => failures.Add(ex),
                concurrentAnalysis: false,
                logAnalyzerExecutionTime: false));

        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();

        Assert.Empty(failures);
        Assert.Empty(diagnostics);
    }
}
