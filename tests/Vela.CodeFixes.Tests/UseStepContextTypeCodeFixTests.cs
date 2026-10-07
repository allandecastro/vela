using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.HandlerContextMatchAnalyzer,
    Vela.CodeFixes.UseStepContextTypeCodeFix>;
using FixTest = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixTest<
    Vela.Analyzers.HandlerContextMatchAnalyzer,
    Vela.CodeFixes.UseStepContextTypeCodeFix,
    Microsoft.CodeAnalysis.Testing.Verifiers.XUnitVerifier>;

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
    public async Task Replaces_a_wrong_context_with_the_simplified_step_message_context()
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
        private void OnWrong(IUpdatePostContext<Account> ctx) { }
    }
}
";

        DiagnosticResult expected = new DiagnosticResult(Vela.Analyzers.HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments("OnWrong", "Update post-operation", "IUpdatePostContext");
        await Verify.VerifyCodeFixAsync(before, expected, after);
    }

    [Fact]
    public async Task Fixes_a_handler_declared_in_a_different_file()   // #309 M3
    {
        // The handler is a partial-class member in a SECOND file. The fix must edit that file's document (Solution-
        // based), not the file with the .Handle(...) diagnostic - otherwise ReplaceNode throws "node not in tree".
        string file1 = Harness + @"
    public sealed partial class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnWrong|});
    }
}
";
        const string file2 = @"
namespace Demo
{
    using Vela.Context;
    public sealed partial class P
    {
        private void OnWrong(ICreatePostContext<Account> ctx) { }
    }
}
";
        string file1Fixed = Harness + @"
    public sealed partial class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnWrong);
    }
}
";
        const string file2Fixed = @"
namespace Demo
{
    using Vela.Context;
    public sealed partial class P
    {
        private void OnWrong(IUpdatePostContext<Account> ctx) { }
    }
}
";

        var test = new FixTest
        {
            TestState =
            {
                Sources = { file1, file2 },
                ExpectedDiagnostics =
                {
                    new DiagnosticResult(Vela.Analyzers.HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                        .WithLocation(0).WithArguments("OnWrong", "Update post-operation", "IUpdatePostContext"),
                },
            },
            FixedState = { Sources = { file1Fixed, file2Fixed } },
        };

        await test.RunAsync();
    }
}
