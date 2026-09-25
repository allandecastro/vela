using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.HandlerContextMatchAnalyzer,
    Vela.CodeFixes.UseStepContextTypeCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class UseStepContextTypeCodeFixTests
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
";

    [Fact]
    public async Task Replaces_a_wrong_context_with_the_step_message_context()
    {
        string before = Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnWrong|});
        private void OnWrong(ICreatePostContext<Account> ctx) { }
    }
}
";

        string after = Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnWrong);
        private void OnWrong(global::Vela.Context.IUpdatePostContext<global::Demo.Account> ctx) { }
    }
}
";

        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("OnWrong", "Update post-operation", "IUpdatePostContext");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }
}
