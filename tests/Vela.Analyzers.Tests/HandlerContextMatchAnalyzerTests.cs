using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.HandlerContextMatchAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class HandlerContextMatchAnalyzerTests
{
    private const string Harness = @"
using System;

namespace Microsoft.Xrm.Sdk { public class Entity { } }

namespace Vela.Context
{
    public interface ILocalPluginContext { }
    public interface IUpdatePostContext<TEntity> : ILocalPluginContext { }
    public interface ICreatePostContext<TEntity> : ILocalPluginContext { }
}

namespace Vela.Steps
{
    using Vela.Context;
    public interface IFluentStep { }
    public interface IStepConfig<TEntity> : IFluentStep
    {
        IStepConfig<TEntity> OnCreate();
        IStepConfig<TEntity> OnUpdate();
        IStepConfig<TEntity> PostOperation();
        IStepConfig<TEntity> Sync();
        void Handle(Action<IUpdatePostContext<TEntity>> handler);
        void Handle(Delegate handler);
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
    using Vela.Context;
    using Vela.Plugin;

    public sealed class Account : Entity { }
    public sealed class Contact : Entity { }
    public sealed class GenCtx : IUpdatePostContext<Account> { }   // stands in for a generated typed context
";

    private static string InPlugin(string members) => Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            " + members + @"
        }
        private void OnGood(IUpdatePostContext<Account> ctx) { }
        private void OnWrong(ICreatePostContext<Account> ctx) { }
        private void OnWrongEntity(IUpdatePostContext<Contact> ctx) { }
        private void OnPlain(ILocalPluginContext ctx) { }
        private void OnGenerated(GenCtx ctx) { }
    }
}
";

    private static DiagnosticResult Expected(string handler) =>
        new DiagnosticResult(Vela.Analyzers.HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments(handler, "Update post-operation", "IUpdatePostContext");

    [Fact]
    public async Task Does_not_flag_the_matching_message_context()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnGood);"));

    [Fact]
    public async Task Does_not_flag_the_plain_local_plugin_context()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnPlain);"));

    [Fact]
    public async Task Does_not_flag_a_generated_context_subclass()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnGenerated);"));

    [Fact]
    public async Task Does_not_flag_a_lambda_handler()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle((IUpdatePostContext<Account> ctx) => { });"));

    [Fact]
    public async Task Flags_a_context_for_a_different_message()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnWrong|});"), Expected("OnWrong"));

    [Fact]
    public async Task Flags_the_right_context_shape_but_for_the_wrong_entity()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnWrongEntity|});"), Expected("OnWrongEntity"));
}
