using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Vela.Analyzers.Tests;

// The generator's pipeline models carry value equality so the incremental driver caches its output between
// keystrokes. These tests pin both halves of that contract: an edit that does not change any model leaves every
// output cached, and each model field takes part in equality (a field ignored by Equals would leave a stale
// projection or context behind after the edit that changes it).
public sealed class ImageProjectionGeneratorIncrementalTests
{
    private const string Plugin = @"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class AccountAudit : EntityPluginBase<Account>
    {
        private static readonly string Dynamic = ""dyn"";
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync()
                .WithPreImage(""Before"", a => a.Name)
                .WithPostImage(a => a.Revenue)
                .Handle(OnUpdated);
            RegisterStep().OnCreate().PostOperation().Sync().WithPostImage(Dynamic, a => a.Name).Handle(OnCreated);
        }
        private void OnUpdated() { }
        private void OnCreated() { }
    }
}
";

    [Fact]
    public void An_edit_that_changes_no_model_leaves_every_output_cached()
    {
        CSharpCompilation compilation = GeneratorHarness.Compile(Plugin);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ImageProjectionGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        GeneratorRunResult first = driver.GetRunResult().Results.Single();
        Assert.Equal(4, first.GeneratedSources.Length); // two projections, two contexts
        Assert.Single(first.Diagnostics);               // VELA021 on the non-constant alias

        // Re-parse the plugin file with a trailing comment: every syntax node is new, so each transform re-runs, but
        // the models it produces are equal to the previous ones.
        SyntaxTree old = compilation.SyntaxTrees.Single(t => t.FilePath == "Plugin0.cs");
        SyntaxTree edited = old.WithChangedText(old.GetText().WithChanges(new TextChange(new TextSpan(old.Length, 0), "// edited\n")));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(old, edited));
        GeneratorRunResult second = driver.GetRunResult().Results.Single();

        Assert.Equal(first.GeneratedSources.Select(s => s.SourceText.ToString()), second.GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Single(second.Diagnostics);
        Assert.NotEmpty(second.TrackedOutputSteps);
        foreach (IncrementalGeneratorRunStep step in second.TrackedOutputSteps.SelectMany(s => s.Value))
        {
            foreach ((object _, IncrementalStepRunReason reason) in step.Outputs)
            {
                Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"output re-ran: {reason}");
            }
        }
    }

    private static Type Nested(string name)
        => typeof(ImageProjectionGenerator).GetNestedType(name, BindingFlags.NonPublic)!;

    private static object Create(string type, params object?[] args)
        => Activator.CreateInstance(Nested(type), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null)!;

    private static void AssertFieldwiseEquality(Func<object?[]> baseline, string type, params Func<object?[], object?[]>[] variants)
    {
        object a = Create(type, baseline());
        object b = Create(type, baseline());
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not a model"));

        for (int i = 0; i < variants.Length; i++)
        {
            object changed = Create(type, variants[i](baseline()));
            Assert.False(a.Equals(changed), $"{type}: variant {i} compared equal");
            Assert.False(changed.Equals(a), $"{type}: variant {i} compared equal (reversed)");
        }
    }

    private static object?[] With(object?[] args, int index, object? value)
    {
        args[index] = value;
        return args;
    }

    private static object Columns(params (string Property, string Type, string Logical)[] columns)
    {
        Type columnType = Nested("ProjectionColumn");
        Array array = Array.CreateInstance(columnType, columns.Length);
        for (int i = 0; i < columns.Length; i++)
        {
            array.SetValue(Create("ProjectionColumn", columns[i].Property, columns[i].Type, columns[i].Logical), i);
        }

        MethodInfo createRange = typeof(ImmutableArray).GetMethods()
            .Single(m => m.Name == nameof(ImmutableArray.Create) && m.GetParameters() is [{ ParameterType.IsArray: true }])
            .MakeGenericMethod(columnType);
        return createRange.Invoke(null, new object[] { array })!;
    }

    [Fact]
    public void Every_projection_column_field_takes_part_in_equality()
    {
        AssertFieldwiseEquality(() => new object?[] { "Name", "string?", "name" }, "ProjectionColumn",
            a => With(a, 0, "Other"),
            a => With(a, 1, "int?"),
            a => With(a, 2, "other"));
    }

    [Fact]
    public void Every_projection_model_field_takes_part_in_equality()
    {
        AssertFieldwiseEquality(
            () => new object?[]
            {
                "Demo", ImmutableArray.Create("class AccountAudit"), "OnUpdated_PostImage", Columns(("Name", "string?", "name")),
                "OnUpdated", "Update", true, false, "global::Demo.Account", null, null,
            },
            "ProjectionModel",
            a => With(a, 0, "Other"),
            a => With(a, 1, ImmutableArray.Create("class Other")),
            a => With(a, 2, "Other_PostImage"),
            a => With(a, 3, Columns(("Name", "string?", "name"), ("Revenue", "decimal?", "revenue"))),
            a => With(a, 4, "OnOther"),
            a => With(a, 5, "Create"),
            a => With(a, 6, false),
            a => With(a, 7, true),
            a => With(a, 8, "global::Demo.Contact"),
            a => With(a, 9, "global::Demo.DeleteRequest"),
            a => With(a, 10, "Before"));
    }

    [Fact]
    public void Every_handler_model_field_takes_part_in_equality()
    {
        AssertFieldwiseEquality(
            () => new object?[] { "Demo", ImmutableArray.Create("class AccountAudit"), "OnUpdated", "Update", true, "global::Demo.Account", null },
            "HandlerModel",
            a => With(a, 0, "Other"),
            a => With(a, 1, ImmutableArray.Create("class Other")),
            a => With(a, 2, "OnOther"),
            a => With(a, 3, "Create"),
            a => With(a, 4, false),
            a => With(a, 5, "global::Demo.Contact"),
            a => With(a, 6, "global::Demo.DeleteRequest"));
    }

    [Fact]
    public void A_projection_result_compares_both_its_model_and_its_skip_reason()
    {
        LinePositionSpan line = new LinePositionSpan(new LinePosition(1, 2), new LinePosition(1, 15));
        object Skip(string reason) => Create("SkippedProjection", "Plugin0.cs", new TextSpan(10, 13), line, "WithPostImage", reason);
        object Skipped(string reason) => Activator.CreateInstance(Nested("ProjectionResult"),
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { Skip(reason) }, null)!;
        object Model(string typeName) => Create("ProjectionModel",
            "Demo", ImmutableArray.Create("class P"), typeName, Columns(("Name", "string?", "name")),
            null, "Update", true, false, "global::Demo.Account", null, null);
        object Result(object model) => Create("ProjectionResult", model);

        Assert.True(Skipped("a").Equals(Skipped("a")));
        Assert.False(Skipped("a").Equals(Skipped("b")));
        Assert.True(Result(Model("X")).Equals(Result(Model("X"))));
        Assert.False(Result(Model("X")).Equals(Result(Model("Y"))));
        Assert.False(Result(Model("X")).Equals(Skipped("a")));
        Assert.Equal(Skipped("a").GetHashCode(), Skipped("a").GetHashCode());
        Assert.Equal(Result(Model("X")).GetHashCode(), Result(Model("X")).GetHashCode());
        Assert.False(Skipped("a").Equals(null));
    }

    [Fact]
    public void Every_skipped_projection_field_takes_part_in_equality()
    {
        LinePositionSpan line = new LinePositionSpan(new LinePosition(1, 2), new LinePosition(1, 15));
        AssertFieldwiseEquality(
            () => new object?[] { "Plugin0.cs", new TextSpan(10, 13), line, "WithPostImage", "reason" },
            "SkippedProjection",
            a => With(a, 0, "Plugin1.cs"),
            a => With(a, 1, new TextSpan(11, 13)),
            a => With(a, 2, new LinePositionSpan(new LinePosition(2, 2), new LinePosition(2, 15))),
            a => With(a, 3, "WithPreImage"),
            a => With(a, 4, "another reason"));
    }
}
