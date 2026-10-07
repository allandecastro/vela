using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.SingleStepPerTypedContextHandlerAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class SingleStepPerTypedContextHandlerAnalyzerTests
{
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Microsoft.Xrm.Sdk { public class Entity { } }

namespace Vela.Context
{
    public interface ILocalPluginContext { }
    public interface IUpdatePostContext<TEntity> : ILocalPluginContext { }
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
        IStepConfig<TEntity> FilterOn(Expression<Func<TEntity, object>> a);
        IStepConfig<TEntity> WithPostImage(params Expression<Func<TEntity, object>>[] a);
        void Handle(Action<IUpdatePostContext<TEntity>> handler);
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

    public sealed class Account : Entity { public string? Name { get; set; } }
";

    private static string InPlugin(string body) => Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            " + body + @"
        }
        private void Shared(IUpdatePostContext<Account> ctx) { }
        private void OnA(IUpdatePostContext<Account> ctx) { }
        private void OnB(IUpdatePostContext<Account> ctx) { }
    }
}
";

    private static DiagnosticResult At(int marker) =>
        new DiagnosticResult(Vela.Analyzers.SingleStepPerTypedContextHandlerAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(marker).WithArguments("Shared");

    [Fact]
    public async Task Flags_a_handler_reused_across_two_image_capturing_steps()
    {
        string code = InPlugin(@"
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle({|#0:Shared|});
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle({|#1:Shared|});");

        await Verify.VerifyAnalyzerAsync(code, At(0), At(1));
    }

    [Fact]
    public async Task Does_not_flag_distinct_handlers()
    {
        string code = InPlugin(@"
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle(OnA);
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle(OnB);");

        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_shared_handler_without_image_captures()
    {
        string code = InPlugin(@"
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(Shared);
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(Shared);");

        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_container_class_whose_nested_plugins_reuse_a_handler_name()
    {
        // A-31: each nested plugin gets its own {Handler}Context scope; the container class itself registers nothing,
        // so two plugins that both name their handler Shared do not collide.
        string code = Harness + @"
    public static class Plugins
    {
        public sealed class First : EntityPluginBase<Account>
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle(Shared);
            private void Shared(IUpdatePostContext<Account> ctx) { }
        }

        public sealed class Second : EntityPluginBase<Account>
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithPostImage(a => a.Name).Handle(Shared);
            private void Shared(IUpdatePostContext<Account> ctx) { }
        }
    }
}
";

        await Verify.VerifyAnalyzerAsync(code);
    }
}
