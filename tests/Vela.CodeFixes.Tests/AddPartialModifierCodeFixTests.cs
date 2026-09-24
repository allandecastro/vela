using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.PartialPluginForTypedImageAnalyzer,
    Vela.CodeFixes.AddPartialModifierCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class AddPartialModifierCodeFixTests
{
    private const string Preamble = @"
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

    private static DiagnosticResult Expected(string plugin) =>
        new DiagnosticResult(Vela.Analyzers.PartialPluginForTypedImageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments(plugin);

    [Fact]
    public async Task Adds_partial_after_the_sealed_modifier()
    {
        string before = Preamble + @"
namespace Demo
{
    using Vela.Plugin;
    public sealed class {|#0:Audit|} : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }
}";

        string after = Preamble + @"
namespace Demo
{
    using Vela.Plugin;
    public sealed partial class Audit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }
}";

        await Verify.VerifyCodeFixAsync(before, Expected("Audit"), after);
    }

    [Fact]
    public async Task Adds_partial_to_a_plugin_with_a_single_access_modifier()
    {
        string before = Preamble + @"
namespace Demo
{
    using Vela.Plugin;
    public class {|#0:Audit|} : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }
}";

        string after = Preamble + @"
namespace Demo
{
    using Vela.Plugin;
    public partial class Audit : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().WithPostImage(a => a.Name).Handle(() => { });
    }
}";

        await Verify.VerifyCodeFixAsync(before, Expected("Audit"), after);
    }
}
