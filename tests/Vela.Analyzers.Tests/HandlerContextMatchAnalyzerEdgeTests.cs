using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.HandlerContextMatchAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA015 edges: the full user-facing message for a pre and a post step, one shape diagnostic (never a second
// context diagnostic) for a handler of the wrong arity, the ExpectedType property the code fix reads, a look-alike
// context interface from another namespace, a step registered outside a class, and the name fallback when the
// compilation does not define the context interface.
public sealed class HandlerContextMatchAnalyzerEdgeTests
{
    private static readonly string Harness = (string)typeof(HandlerContextMatchAnalyzerTests)
        .GetField("Harness", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static string Plugin(string register, string members) => Harness + @"
    public interface ILookalike { }
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            " + register + @"
        }
        " + members + @"
    }
}
";

    [Fact]
    public async Task The_message_names_the_handler_the_step_and_the_expected_context()
    {
        string code = Plugin(
            "RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnWrong|});\n" +
            "            RegisterStep().OnDelete().PreValidation().Sync().Handle({|#1:OnWrong|});",
            "private void OnWrong(ICreatePostContext<Account> ctx) { }");

        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithMessage("Handler 'OnWrong' takes a context that does not match this Update post-operation step; expected 'IUpdatePostContext' (or its generated typed context)"),
            new DiagnosticResult(HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(1)
                .WithMessage("Handler 'OnWrong' takes a context that does not match this Delete pre-operation step; expected 'IDeletePreContext' (or its generated typed context)"));
    }

    [Theory]
    [InlineData("private void OnBad() { }", 0, "void")]
    [InlineData("private void OnBad(string a, string b) { }", 2, "void")]
    [InlineData("private int OnBad(string a) => 0;", 1, "int")]
    public async Task A_handler_of_the_wrong_shape_gets_only_the_shape_diagnostic(string handler, int parameters, string returns)
    {
        string code = Plugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnBad|});", handler);

        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithMessage($"Handler 'OnBad' must be a void method taking exactly one context parameter; it takes {parameters} parameter(s) and returns '{returns}'"));
    }

    [Fact]
    public async Task A_same_named_context_interface_from_another_namespace_does_not_satisfy_the_step()
    {
        string code = Harness + @"
    public interface IUpdatePostContext<T> { }
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps() => RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnLookalike|});
        private void OnLookalike(Demo.IUpdatePostContext<Account> ctx) { }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithArguments("OnLookalike", "Update post-operation", "IUpdatePostContext"));
    }

    [Fact]
    public async Task A_step_registered_in_a_struct_is_checked_without_an_entity()
    {
        // No enclosing class -> no plugin entity: the context is matched on its interface alone.
        string code = Harness + @"
    public struct Registrar
    {
        private Vela.Steps.IStepConfig<Account> step;
        public void Register()
        {
            step.OnUpdate().PostOperation().Sync().Handle(OnAnyEntity);
            step.OnUpdate().PostOperation().Sync().Handle({|#0:OnWrong|});
        }
        private void OnAnyEntity(IUpdatePostContext<Contact> ctx) { }
        private void OnWrong(ICreatePostContext<Account> ctx) { }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithArguments("OnWrong", "Update post-operation", "IUpdatePostContext"));
    }

    [Fact]
    public async Task Without_the_context_interface_in_the_compilation_the_name_decides()
    {
        // This compilation defines no Vela.Context.IUpdatePostContext`1: a parameter whose type is NAMED
        // IUpdatePostContext is accepted, any other name is reported.
        string code = @"
using System;
namespace Microsoft.Xrm.Sdk { public class Entity { } }
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStepConfig<TEntity> : IFluentStep
    {
        IStepConfig<TEntity> OnUpdate();
        IStepConfig<TEntity> PostOperation();
        void Handle(Delegate handler);
    }
}
namespace Vela.Plugin
{
    public abstract class EntityPluginBase<TEntity> where TEntity : Microsoft.Xrm.Sdk.Entity
    {
        protected Vela.Steps.IStepConfig<TEntity> RegisterStep() => null!;
        protected virtual void RegisterPluginSteps() { }
    }
}
namespace Other { public interface IUpdatePostContext<T> { } public interface ICreatePostContext<T> { } }
namespace Demo
{
    public sealed class Account : Microsoft.Xrm.Sdk.Entity { }
    public sealed class P : Vela.Plugin.EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Handle(OnNamed);
            RegisterStep().OnUpdate().PostOperation().Handle({|#0:OnOther|});
        }
        private void OnNamed(Other.IUpdatePostContext<Account> ctx) { }
        private void OnOther(Other.ICreatePostContext<Account> ctx) { }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code,
            new DiagnosticResult(HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error).WithLocation(0)
                .WithArguments("OnOther", "Update post-operation", "IUpdatePostContext"));
    }

    [Fact]
    public async Task The_diagnostic_carries_the_fully_qualified_expected_context_for_the_code_fix()
    {
        string code = Plugin("RegisterStep().OnUpdate().PreOperation().Sync().Handle(OnWrong);", "private void OnWrong(ICreatePostContext<Account> ctx) { }");
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("ctx", new[] { CSharpSyntaxTree.ParseText(code) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HandlerContextMatchAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("global::Vela.Context.IUpdatePreContext<global::Demo.Account>", diagnostic.Properties["ExpectedType"]);
    }
}
