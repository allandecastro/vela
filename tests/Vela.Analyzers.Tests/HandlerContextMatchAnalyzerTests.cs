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
    public interface IUpdatePreContext<TEntity> : ILocalPluginContext { }
    public interface IDeletePreContext<TEntity> : ILocalPluginContext { }
}

namespace Vela.Steps
{
    using Vela.Context;
    public interface IFluentStep { }
    public interface IStepConfig<TEntity> : IFluentStep
    {
        IStepConfig<TEntity> OnCreate();
        IStepConfig<TEntity> OnUpdate();
        IStepConfig<TEntity> OnDelete();
        IStepConfig<TEntity> PreValidation();
        IStepConfig<TEntity> PreOperation();
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
        private void OnPre(IUpdatePreContext<Account> ctx) { }
        private void OnDeletePre(IDeletePreContext<Account> ctx) { }
        private void OnTwo(IUpdatePostContext<Account> ctx, int extra) { }
        private bool OnReturns(IUpdatePostContext<Account> ctx) => true;
    }
}
";

    private static DiagnosticResult Expected(string handler, string step = "Update post-operation", string expected = "IUpdatePostContext") =>
        new DiagnosticResult(Vela.Analyzers.HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments(handler, step, expected);

    private static DiagnosticResult Shape(string handler, int parameters, string returns) =>
        new DiagnosticResult(Vela.Analyzers.HandlerContextMatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0).WithArguments(handler, parameters, returns);

    [Fact]
    public async Task Flags_a_handler_that_takes_two_parameters()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PostOperation().Sync().Handle({|#0:OnTwo|});"), Shape("OnTwo", 2, "void"));

    [Fact]
    public async Task Flags_a_handler_that_returns_a_value_whatever_the_trigger()
        // No guided message on this chain: the shape is checked anyway, since the builder rejects it on every path.
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().PostOperation().Sync().Handle({|#0:OnReturns|});"), Shape("OnReturns", 1, "bool"));

    [Fact]
    public async Task Flags_a_wrong_context_on_a_parenthesized_chain()
        => await Verify.VerifyAnalyzerAsync(InPlugin("(RegisterStep().OnUpdate()).PostOperation().Sync().Handle({|#0:OnWrong|});"), Expected("OnWrong"));

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

    [Fact]
    public async Task Flags_a_post_context_bound_to_a_pre_operation_update_step()
        => await Verify.VerifyAnalyzerAsync(
            InPlugin("RegisterStep().OnUpdate().PreOperation().Sync().Handle({|#0:OnGood|});"),
            Expected("OnGood", "Update pre-operation", "IUpdatePreContext"));

    [Fact]
    public async Task Does_not_flag_a_pre_context_on_a_pre_validation_update_step()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().PreValidation().Sync().Handle(OnPre);"));

    [Fact]
    public async Task Flags_an_update_context_bound_to_a_create_post_operation_step()
        => await Verify.VerifyAnalyzerAsync(
            InPlugin("RegisterStep().OnCreate().PostOperation().Sync().Handle({|#0:OnGood|});"),
            Expected("OnGood", "Create post-operation", "ICreatePostContext"));

    [Fact]
    public async Task Does_not_flag_the_matching_create_post_context()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnCreate().PostOperation().Sync().Handle(OnWrong);"));

    [Fact]
    public async Task Flags_an_update_context_bound_to_a_delete_pre_operation_step()
        => await Verify.VerifyAnalyzerAsync(
            InPlugin("RegisterStep().OnDelete().PreOperation().Sync().Handle({|#0:OnGood|});"),
            Expected("OnGood", "Delete pre-operation", "IDeletePreContext"));

    [Fact]
    public async Task Does_not_flag_the_matching_delete_pre_context()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnDelete().PreOperation().Sync().Handle(OnDeletePre);"));

    [Fact]
    public async Task Does_not_flag_a_chain_whose_stage_is_not_set()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().OnUpdate().Sync().Handle(OnWrong);"));

    [Fact]
    public async Task Does_not_flag_a_chain_whose_message_is_not_set()
        => await Verify.VerifyAnalyzerAsync(InPlugin("RegisterStep().PostOperation().Sync().Handle(OnWrong);"));

    [Fact]
    public async Task Flags_a_step_started_from_a_field_through_this()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        private readonly Vela.Steps.IStepConfig<Account> _step = null!;
        protected override void RegisterPluginSteps()
        {
            this._step.OnUpdate().PostOperation().Handle({|#0:OnWrong|});
        }
        private void OnWrong(ICreatePostContext<Account> ctx) { }
    }
}
", Expected("OnWrong"));

    [Fact]
    public async Task Flags_a_wrong_entity_through_an_intermediate_plugin_base()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public abstract class AccountPluginBase : EntityPluginBase<Account> { }

    public sealed class P : AccountPluginBase
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Handle({|#0:OnWrongEntity|});
        }
        private void OnWrongEntity(IUpdatePostContext<Contact> ctx) { }
    }
}
", Expected("OnWrongEntity"));

    [Fact]
    public async Task Flags_a_wrong_message_in_a_registration_helper_outside_a_plugin()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public static class AccountSteps
    {
        public static void Register(Vela.Steps.IStepConfig<Account> step)
        {
            step.OnUpdate().PostOperation().Handle({|#0:OnWrong|});
        }
        private static void OnWrong(ICreatePostContext<Account> ctx) { }
    }
}
", Expected("OnWrong"));

    [Fact]
    public async Task Does_not_flag_an_entity_mismatch_when_the_plugin_entity_is_unknown()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public static class AccountSteps
    {
        public static void Register(Vela.Steps.IStepConfig<Account> step)
        {
            step.OnUpdate().PostOperation().Handle(OnOtherEntity);
        }
        private static void OnOtherEntity(IUpdatePostContext<Contact> ctx) { }
    }
}
");

    [Fact]
    public async Task Does_not_flag_a_handler_whose_context_type_does_not_resolve()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Handle(OnMissing);
        }
        private void OnMissing({|CS0246:IUpdatePostContxt<Account>|} ctx) { }
    }
}
");

    [Fact]
    public async Task Does_not_flag_a_handler_whose_context_entity_does_not_resolve()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public sealed class P : EntityPluginBase<Account>
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Handle(OnTypo);
        }
        private void OnTypo(IUpdatePostContext<{|CS0246:Acount|}> ctx) { }
    }
}
");

    [Fact]
    public async Task Does_not_flag_a_Handle_method_group_on_a_non_Vela_type()
        => await Verify.VerifyAnalyzerAsync(Harness + @"
    public sealed class MessageBus
    {
        public MessageBus OnUpdate() => this;
        public MessageBus PostOperation() => this;
        public void Handle(Delegate handler) { }
    }

    public sealed class Consumer
    {
        public void Wire(MessageBus bus) => bus.OnUpdate().PostOperation().Handle(OnWrong);
        private void OnWrong(ICreatePostContext<Account> ctx) { }
    }
}
");

    [Fact]
    public async Task Does_not_flag_anything_when_the_Vela_step_builder_is_not_referenced()
        => await Verify.VerifyAnalyzerAsync(@"
using System;

namespace Demo
{
    public interface ICreatePostContext { }

    public sealed class Bus
    {
        public Bus OnUpdate() => this;
        public Bus PostOperation() => this;
        public void Handle(Delegate handler) { }
    }

    public sealed class Consumer
    {
        public void Wire(Bus bus) => bus.OnUpdate().PostOperation().Handle(OnCreated);
        private void OnCreated(ICreatePostContext ctx) { }
    }
}
");
}
