using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Pins the observable behaviour of the plugin runtime (PluginBase, EntityPluginBase<TEntity>, HandlerInvoker) that
// the mutation run of issue #448 found unasserted: configuration defaults, the depth guards, the handler-entry trace,
// the derived step-context resolution, the dispatch error messages and the handler exception unwrapping.
public sealed class PluginBaseMutationTests
{
    private static PluginContextBuilder<TestAccount> UpdatePost(int depth = 1) => PluginContext.For<TestAccount>()
        .OnUpdate().Stage(PluginStages.PostOperation).Sync().Depth(depth)
        .WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Acme" });

    // ---- PluginBase: configuration and depth threshold ------------------------------------------------------------

    private sealed class BarePlugin(string? unsecureConfig, string? secureConfig, int maxDepth = PluginBase.DefaultMaxDepth)
        : PluginBase(unsecureConfig!, secureConfig!, maxDepth)
    {
        public string Unsecure => UnsecureConfig;
        public string Secure => SecureConfig;
        public int Max => MaxDepth;
        public bool Ran { get; private set; }

        public static T Idempotent<T>(ILocalPluginContext context, Func<T> operation) => ExecuteIdempotentWithRetry(context, operation);

        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx) => Ran = true;
    }

    [Fact]
    public void The_configuration_strings_are_exposed_as_given()
    {
        BarePlugin plugin = new BarePlugin("{\"unsecure\":1}", "{\"secure\":2}");

        Assert.Equal("{\"unsecure\":1}", plugin.Unsecure);
        Assert.Equal("{\"secure\":2}", plugin.Secure);
    }

    [Fact]
    public void A_missing_configuration_reads_as_an_empty_string()
    {
        BarePlugin plugin = new BarePlugin(null, null);

        Assert.Equal(string.Empty, plugin.Unsecure);
        Assert.Equal(string.Empty, plugin.Secure);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    public void The_depth_threshold_is_at_least_one(int requested, int expected)
    {
        Assert.Equal(expected, new BarePlugin(null, null, requested).Max);
    }

    [Fact]
    public void A_zero_threshold_still_lets_a_top_level_execution_run()
    {
        BarePlugin plugin = new BarePlugin(null, null, maxDepth: 0);

        plugin.Execute(UpdatePost(depth: 1).BuildServiceProvider());

        Assert.True(plugin.Ran);
    }

    [Fact]
    public void The_depth_guard_traces_the_depth_and_threshold_it_stopped_at()
    {
        BarePlugin plugin = new BarePlugin(null, null, maxDepth: 2);
        PluginContextBuilder<TestAccount> builder = UpdatePost(depth: 3);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(builder.BuildServiceProvider()));

        Assert.Equal("Plugin execution stopped to prevent recursion.", ex.Message);
        Assert.Contains("Depth guard triggered. Depth=3, MaxDepth=2", builder.TraceCapture.Traces);
        Assert.False(plugin.Ran);
    }

    [Fact]
    public void An_idempotent_retry_replays_an_operation_that_timed_out()
    {
        PluginContextBuilder<TestAccount> builder = UpdatePost();
        builder.Context.IsInTransaction = false;
        LocalPluginContext context = builder.Build();
        int calls = 0;

        int result = BarePlugin.Idempotent(context, () =>
        {
            calls++;
            if (calls == 1)
            {
                throw new TimeoutException("the read timed out");
            }

            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(2, calls);
    }

    // ---- PluginBase: unexpected-error message hook ----------------------------------------------------------------

    private sealed class BrokenFormatterPlugin() : PluginBase(string.Empty, string.Empty)
    {
        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx) => throw new InvalidOperationException("handler failed");

        protected override string FormatUnexpectedErrorMessage(ILocalPluginContext context, Exception exception)
            => throw new FormatException("formatter broke");
    }

    [Fact]
    public void A_throwing_error_formatter_is_traced_and_falls_back_to_the_default_message()
    {
        Guid correlation = Guid.NewGuid();
        PluginContextBuilder<TestAccount> builder = UpdatePost().WithCorrelationId(correlation);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new BrokenFormatterPlugin().Execute(builder.BuildServiceProvider()));

        Assert.Equal($"An unexpected error occurred. Correlation id: {correlation}.", ex.Message);
        string trace = Assert.Single(builder.TraceCapture.Traces, t => t.StartsWith("FormatUnexpectedErrorMessage failed: ", StringComparison.Ordinal));
        Assert.Contains("formatter broke", trace, StringComparison.Ordinal);
    }

    // ---- Handler-entry trace and current handler ------------------------------------------------------------------

    private sealed class NamedHandlerPlugin(bool traceEntry) : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        public bool Ran { get; private set; }

        protected override bool AutoTraceHandlerEntry => traceEntry;

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnAccountUpdated);

        private void OnAccountUpdated(IUpdatePostContext<TestAccount> ctx)
        {
            ctx.Trace("inside the handler");
            Ran = true;
        }
    }

    private sealed class LambdaHandlerPlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        public bool Ran { get; private set; }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(_ => Ran = true);
    }

    [Fact]
    public void A_named_handler_is_traced_on_entry_and_names_the_log_scope()
    {
        NamedHandlerPlugin plugin = new NamedHandlerPlugin(traceEntry: true);
        PluginContextBuilder<TestAccount> builder = UpdatePost();

        plugin.Execute(builder.BuildServiceProvider());

        Assert.True(plugin.Ran);
        Assert.Contains("Entering handler 'OnAccountUpdated' for message 'Update'.", builder.TraceCapture.Traces);
        Assert.Equal("OnAccountUpdated", builder.LogCapture.Entries.Single(e => e.Message == "inside the handler").Scope["Method"]);
    }

    [Fact]
    public void The_handler_entry_trace_can_be_switched_off()
    {
        NamedHandlerPlugin plugin = new NamedHandlerPlugin(traceEntry: false);
        PluginContextBuilder<TestAccount> builder = UpdatePost();

        plugin.Execute(builder.BuildServiceProvider());

        Assert.True(plugin.Ran);
        Assert.DoesNotContain(builder.TraceCapture.Traces, t => t.StartsWith("Entering handler", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lambda_handler_with_a_compiler_generated_name_is_not_traced()
    {
        LambdaHandlerPlugin plugin = new LambdaHandlerPlugin();
        PluginContextBuilder<TestAccount> builder = UpdatePost();

        plugin.Execute(builder.BuildServiceProvider());

        Assert.True(plugin.Ran);
        Assert.DoesNotContain(builder.TraceCapture.Traces, t => t.StartsWith("Entering handler", StringComparison.Ordinal));
    }

    // ---- Handler exceptions surface unwrapped ---------------------------------------------------------------------

    private sealed class RejectingPlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(Reject);

        private void Reject(IUpdatePostContext<TestAccount> ctx) => throw new InvalidPluginExecutionException("Credit limit exceeded.");
    }

    [Fact]
    public void A_business_exception_thrown_by_a_step_handler_reaches_the_caller_unchanged()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new RejectingPlugin().Execute(UpdatePost().BuildServiceProvider()));

        Assert.Equal("Credit limit exceeded.", ex.Message);
    }

    [Fact]
    public void InvokeDelegate_rethrows_the_handler_exception_without_the_reflection_wrapper()
    {
        Action<ILocalPluginContext> handler = _ => throw new InvalidOperationException("boom");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => HandlerInvoker.InvokeDelegate(handler, [null!]));

        Assert.Equal("boom", ex.Message);
    }

    // ---- HandlerInvoker: resolution diagnostics -------------------------------------------------------------------

    private sealed class HandlerHost
    {
        public string? Received { get; private set; }

        public void Run(string value) => Received = value;

        public void Run(object value) => Received = "object";
    }

    [Fact]
    public void ResolveAndInvoke_reports_a_missing_handler()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => HandlerInvoker.ResolveAndInvoke(
            new HandlerHost(), "Missing", _ => true, ["x"], "Plugin 'P'", "step handler", "void Missing(ctx)"));

        Assert.Equal("Plugin 'P' step handler 'Missing' was not found.", ex.Message);
    }

    [Fact]
    public void ResolveAndInvoke_refuses_several_matching_overloads()
    {
        HandlerHost host = new HandlerHost();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => HandlerInvoker.ResolveAndInvoke(
            host, "Run", _ => true, ["x"], "Custom API 'P'", "handler", "void Run(ctx)"));

        Assert.Equal("Custom API 'P' has multiple overloads for handler 'Run'.", ex.Message);
        Assert.Null(host.Received);
    }

    [Fact]
    public void ResolveAndInvoke_invokes_the_single_matching_overload()
    {
        HandlerHost host = new HandlerHost();

        HandlerInvoker.ResolveAndInvoke(
            host, "Run", m => m.GetParameters()[0].ParameterType == typeof(string), ["x"], "Plugin 'P'", "step handler", "void Run(string)");

        Assert.Equal("x", host.Received);
    }

    // ---- Derived (generated) step contexts ------------------------------------------------------------------------

    public sealed class AuditPostContext(ILocalPluginContext inner) : UpdatePostContext<TestAccount>(inner);

    public sealed class AuditPreContext(ILocalPluginContext inner) : UpdatePreContext<TestAccount>(inner);

    public abstract class AbstractAuditContext(ILocalPluginContext inner) : UpdatePostContext<TestAccount>(inner);

    public sealed class NoContextConstructor(ILocalPluginContext inner, int extra) : UpdatePostContext<TestAccount>(inner)
    {
        public int Extra { get; } = extra;
    }

    public sealed class UnrelatedClass : List<int>
    {
    }

    // One Update step whose handler takes a parameter of type TContext (bound through the permissive Handle(Delegate)).
    private sealed class ContextProbe<TContext>(PluginStages stage) : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        public object? Received { get; private set; }

        protected override void RegisterPluginSteps()
        {
            Delegate handler = new Action<TContext>(Receive);
            if (stage == PluginStages.PreOperation)
            {
                ((IStepConfig<TestAccount>)RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name)).Handle(handler);
            }
            else
            {
                ((IStepConfig<TestAccount>)RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name)).Handle(handler);
            }
        }

        private void Receive(TContext ctx) => Received = ctx;
    }

    private static PluginContextBuilder<TestAccount> Update(PluginStages stage) => PluginContext.For<TestAccount>()
        .OnUpdate().Stage(stage).Sync().WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Acme" });

    [Fact]
    public void A_handler_taking_a_derived_post_context_receives_that_type()
    {
        ContextProbe<AuditPostContext> plugin = new ContextProbe<AuditPostContext>(PluginStages.PostOperation);

        plugin.Execute(Update(PluginStages.PostOperation).BuildServiceProvider());

        AuditPostContext received = Assert.IsType<AuditPostContext>(plugin.Received);
        Assert.Equal("Update", received.MessageName);
    }

    [Fact]
    public void A_handler_taking_a_derived_pre_context_receives_that_type()
    {
        ContextProbe<AuditPreContext> plugin = new ContextProbe<AuditPreContext>(PluginStages.PreOperation);

        plugin.Execute(Update(PluginStages.PreOperation).BuildServiceProvider());

        Assert.IsType<AuditPreContext>(plugin.Received);
    }

    [Fact]
    public void A_handler_taking_object_receives_the_modelled_context()
    {
        ContextProbe<object> plugin = new ContextProbe<object>(PluginStages.PostOperation);

        plugin.Execute(Update(PluginStages.PostOperation).BuildServiceProvider());

        Assert.IsType<UpdatePostContext<TestAccount>>(plugin.Received);
    }

    [Fact]
    public void An_abstract_derived_context_is_refused_by_name()
    {
        ContextProbe<AbstractAuditContext> plugin = new ContextProbe<AbstractAuditContext>(PluginStages.PostOperation);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => plugin.Execute(Update(PluginStages.PostOperation).BuildServiceProvider()));

        Assert.Equal(
            $"Step handler context '{typeof(AbstractAuditContext).FullName}' is abstract and cannot be constructed; use a concrete generated step context.",
            ex.Message);
        Assert.Null(plugin.Received);
    }

    [Fact]
    public void A_derived_context_without_the_context_constructor_is_refused_by_name()
    {
        ContextProbe<NoContextConstructor> plugin = new ContextProbe<NoContextConstructor>(PluginStages.PostOperation);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => plugin.Execute(Update(PluginStages.PostOperation).BuildServiceProvider()));

        Assert.Equal(
            $"Step handler context '{typeof(NoContextConstructor).FullName}' derives from a Vela step context but has no public constructor taking a single ILocalPluginContext, so the pipeline cannot build it.",
            ex.Message);
        Assert.Null(plugin.Received);
    }

    [Fact]
    public void A_handler_parameter_unrelated_to_step_contexts_is_not_built_as_one()
    {
        // A class outside the step-context hierarchy (even a generic-based one) is not built as a step context: the
        // step would pass the modelled UpdatePostContext, which cannot bind to the parameter, so the registration
        // refuses the handler before anything runs.
        ContextProbe<UnrelatedClass> plugin = new ContextProbe<UnrelatedClass>(PluginStages.PostOperation);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => plugin.Execute(Update(PluginStages.PostOperation).BuildServiceProvider()));

        Assert.Contains("but takes 'UnrelatedClass'; it is a context for another message or stage. This step passes IUpdatePostContext<TestAccount>", ex.Message);
        Assert.Null(plugin.Received);
    }

    // ---- Step registration and dispatch messages ------------------------------------------------------------------

    private sealed class NoStepsPlugin(int maxDepth = PluginBase.DefaultMaxDepth) : EntityPluginBase<TestAccount>(string.Empty, string.Empty, maxDepth)
    {
    }

    private sealed class HandlerlessPlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps() => RegisterStep().OnDelete().PostOperation().Sync();
    }

    private sealed class LeakedRegistrarPlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        public IStepTrigger<TestAccount>? Leaked { get; private set; }

        protected override void RegisterPluginSteps()
        {
            Leaked = RegisterStep();
            Leaked.OnDelete().PostOperation().Sync().Handle(_ => { });
        }
    }

    [Fact]
    public void A_plugin_without_steps_is_refused_by_name()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => new NoStepsPlugin().GetStepDefinitions());

        Assert.Equal($"Plugin '{typeof(NoStepsPlugin).FullName}' must register at least one step.", ex.Message);
    }

    [Fact]
    public void A_step_without_a_handler_is_refused_with_the_fix()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => new HandlerlessPlugin().GetStepDefinitions());

        Assert.Equal(
            $"Plugin '{typeof(HandlerlessPlugin).FullName}' has a step on message 'Delete' with no handler. End the RegisterStep() chain with .Handle(...).",
            ex.Message);
    }

    [Fact]
    public void A_registrar_kept_past_RegisterPluginSteps_cannot_add_steps()
    {
        LeakedRegistrarPlugin plugin = new LeakedRegistrarPlugin();
        Assert.Single(plugin.GetStepDefinitions());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => plugin.Leaked!.OnDelete());

        Assert.Equal($"Plugin '{typeof(LeakedRegistrarPlugin).FullName}' can only register steps inside RegisterPluginSteps().", ex.Message);
        Assert.Single(plugin.GetStepDefinitions());
    }

    [Fact]
    public void An_unmatched_message_is_reported_by_name()
    {
        NamedHandlerPlugin plugin = new NamedHandlerPlugin(traceEntry: true);
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnCreate().Stage(PluginStages.PostOperation).Sync()
            .WithTarget(new TestAccount { Name = "Acme" })
            .BuildServiceProvider();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Equal($"Plugin '{typeof(NamedHandlerPlugin).FullName}' has no registered step matching message 'Create', stage PostOperation, mode Synchronous on entity 'account'.", ex.Message);
        Assert.False(plugin.Ran);
    }

    [Fact]
    public void A_step_for_another_table_does_not_run()
    {
        NamedHandlerPlugin plugin = new NamedHandlerPlugin(traceEntry: true);
        IServiceProvider provider = UpdatePost().WithPrimaryEntity("contact").BuildServiceProvider();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Equal($"Plugin '{typeof(NamedHandlerPlugin).FullName}' has no registered step matching message 'Update', stage PostOperation, mode Synchronous on entity 'contact'.", ex.Message);
        Assert.False(plugin.Ran);
    }

    [Fact]
    public void The_table_check_ignores_case()
    {
        NamedHandlerPlugin plugin = new NamedHandlerPlugin(traceEntry: true);

        plugin.Execute(UpdatePost().WithPrimaryEntity("ACCOUNT").BuildServiceProvider());

        Assert.True(plugin.Ran);
    }

    private sealed class TwoKeyedStepsPlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithStepKey("alpha").Handle(_ => { });
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.AccountId).WithStepKey("beta").Handle(_ => { });
        }
    }

    [Fact]
    public void Steps_sharing_a_trigger_without_an_executing_step_name_are_reported_with_the_full_plugin_name()
    {
        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new TwoKeyedStepsPlugin().Execute(UpdatePost().BuildServiceProvider()));

        Assert.StartsWith(
            $"Plugin '{typeof(TwoKeyedStepsPlugin).FullName}' has 2 steps for message 'Update' at stage 40, mode 0,",
            ex.Message,
            StringComparison.Ordinal);
    }

    // ---- Relationship scope ---------------------------------------------------------------------------------------

    private sealed class ScopedAssociatePlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        public bool Ran { get; private set; }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssociate().PostOperation().Sync().ForRelationship("account_contacts").Handle(OnAssociated);

        private void OnAssociated(IAssociatePostContext<TestAccount> ctx) => Ran = true;
    }

    private static PluginContextBuilder<TestAccount> Associate(string relationship) => PluginContext.For<TestAccount>()
        .OnMessage(PluginMessages.Associate).Stage(PluginStages.PostOperation).Sync()
        .WithPrimaryEntity("contact")
        .WithInputParameter("Target", new EntityReference("contact", Guid.NewGuid()))
        .WithInputParameter("Relationship", new Relationship(relationship))
        .WithInputParameter("RelatedEntities", new EntityReferenceCollection { new EntityReference("account", Guid.NewGuid()) });

    [Fact]
    public void Another_relationship_is_skipped_with_a_trace()
    {
        ScopedAssociatePlugin plugin = new ScopedAssociatePlugin();
        PluginContextBuilder<TestAccount> builder = Associate("account_partners");

        plugin.Execute(builder.BuildServiceProvider());

        Assert.False(plugin.Ran);
        Assert.Contains("Vela: relationship outside the step's ForRelationship scope; nothing to run.", builder.TraceCapture.Traces);
    }

    [Fact]
    public void The_scoped_relationship_runs_whatever_the_primary_table()
    {
        ScopedAssociatePlugin plugin = new ScopedAssociatePlugin();
        PluginContextBuilder<TestAccount> builder = Associate("account_contacts");

        plugin.Execute(builder.BuildServiceProvider());

        Assert.True(plugin.Ran);
        Assert.DoesNotContain("Vela: relationship outside the step's ForRelationship scope; nothing to run.", builder.TraceCapture.Traces);
    }

    // ---- Step-level depth limit -----------------------------------------------------------------------------------

    private sealed class StepDepthPlugin(int stepMaxDepth, int pluginMaxDepth = PluginBase.DefaultMaxDepth)
        : EntityPluginBase<TestAccount>(string.Empty, string.Empty, pluginMaxDepth)
    {
        public bool Ran { get; private set; }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).SkipWhenDepthAbove(stepMaxDepth).Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<TestAccount> ctx) => Ran = true;
    }

    [Fact]
    public void A_step_past_its_depth_limit_is_skipped_with_a_trace()
    {
        StepDepthPlugin plugin = new StepDepthPlugin(stepMaxDepth: 2);
        PluginContextBuilder<TestAccount> builder = UpdatePost(depth: 3);

        plugin.Execute(builder.BuildServiceProvider());

        Assert.False(plugin.Ran);
        Assert.Contains("Vela: step skipped by SkipWhenDepthAbove. Depth=3, MaxDepth=2", builder.TraceCapture.Traces);
    }

    [Fact]
    public void A_step_at_its_depth_limit_runs()
    {
        StepDepthPlugin plugin = new StepDepthPlugin(stepMaxDepth: 2);
        PluginContextBuilder<TestAccount> builder = UpdatePost(depth: 2);

        plugin.Execute(builder.BuildServiceProvider());

        Assert.True(plugin.Ran);
        Assert.DoesNotContain(builder.TraceCapture.Traces, t => t.StartsWith("Vela: step skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_step_depth_limit_replaces_the_plugin_depth_guard()
    {
        StepDepthPlugin plugin = new StepDepthPlugin(stepMaxDepth: 5, pluginMaxDepth: 2);

        plugin.Execute(UpdatePost(depth: 4).BuildServiceProvider());

        Assert.True(plugin.Ran);
    }

    [Fact]
    public void The_plugin_depth_guard_applies_when_no_step_resolves()
    {
        // GetStepDefinitions fails (no step): the step-limit lookup reports "no limit", so the plugin guard stops the
        // execution before the dispatch would report the registration error.
        PluginContextBuilder<TestAccount> builder = UpdatePost(depth: 2);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new NoStepsPlugin(maxDepth: 1).Execute(builder.BuildServiceProvider()));

        Assert.Equal("Plugin execution stopped to prevent recursion.", ex.Message);
        Assert.Contains("Depth guard triggered. Depth=2, MaxDepth=1", builder.TraceCapture.Traces);
    }
}
