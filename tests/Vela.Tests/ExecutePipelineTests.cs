using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.DI;
using Vela.Plugin;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// PluginBase.Execute: service registration and context construction run under the same failure contract as the
// handler (traced, wrapped in an InvalidPluginExecutionException, resolver disposed), and the depth guard default
// is one shared constant.
public sealed class ExecutePipelineTests
{
    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingConfigurePlugin(Exception toThrow) : PluginBase(string.Empty, string.Empty)
    {
        public DisposableProbe? Probe { get; private set; }

        public bool Ran { get; private set; }

        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            services.AddTransient(_ => new DisposableProbe());
            Probe = services.GetService(typeof(DisposableProbe)) as DisposableProbe;
            throw toThrow;
        }

        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx) => Ran = true;
    }

    private sealed class NoopPlugin : PluginBase
    {
        public NoopPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        public int ObservedMaxDepth => MaxDepth;

        public bool Ran { get; private set; }

        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx) => Ran = true;
    }

    private sealed class DefaultDepthEntityPlugin : EntityPluginBase<TestAccount>
    {
        public DefaultDepthEntityPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        public int ObservedMaxDepth => MaxDepth;

        protected override void RegisterPluginSteps()
        {
        }
    }

    [Fact]
    public void A_throwing_ConfigureServices_is_wrapped_traced_and_still_disposes_the_resolver()
    {
        RecordingTracingService tracing = new RecordingTracingService();
        IServiceProvider provider = new PluginContextBuilder().WithTracingService(tracing).BuildServiceProvider();
        ThrowingConfigurePlugin plugin = new ThrowingConfigurePlugin(new InvalidOperationException("registration boom"));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Contains("InvalidOperationException: registration boom", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Unhandled exception", tracing.Output);
        Assert.NotNull(plugin.Probe);
        Assert.True(plugin.Probe!.Disposed); // the finally block ran
        Assert.False(plugin.Ran);
    }

    [Fact]
    public void A_business_exception_from_ConfigureServices_surfaces_its_own_message()
    {
        IServiceProvider provider = new PluginContextBuilder().BuildServiceProvider();
        ThrowingConfigurePlugin plugin = new ThrowingConfigurePlugin(new PluginBusinessException("Configure says no."));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Equal("Configure says no.", ex.Message);
        Assert.True(plugin.Probe!.Disposed);
    }

    private sealed class ExplodingContextProvider(ITracingService tracing) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ITracingService))
            {
                return tracing;
            }

            if (typeof(IPluginExecutionContext).IsAssignableFrom(serviceType))
            {
                throw new InvalidCastException("context boom");
            }

            return null;
        }
    }

    [Fact]
    public void A_context_construction_failure_is_traced_and_wrapped()
    {
        RecordingTracingService tracing = new RecordingTracingService();
        NoopPlugin plugin = new NoopPlugin();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => plugin.Execute(new ExplodingContextProvider(tracing)));

        Assert.Contains("while building the execution context", ex.Message);
        Assert.Contains("InvalidCastException: context boom", ex.Message);
        Assert.IsType<InvalidCastException>(ex.InnerException);
        Assert.Contains("Failed to build the execution context", tracing.Output);
        Assert.False(plugin.Ran);
    }

    [Fact]
    public void A_context_construction_failure_never_lets_a_tracing_failure_mask_it()
    {
        // The tracing service itself throws: the original failure must still surface.
        Moq.Mock<ITracingService> tracing = new Moq.Mock<ITracingService>();
        tracing.Setup(t => t.Trace(Moq.It.IsAny<string>(), Moq.It.IsAny<object[]>())).Throws(new FormatException("tracer down"));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => new NoopPlugin().Execute(new ExplodingContextProvider(tracing.Object)));

        Assert.IsType<InvalidCastException>(ex.InnerException);
    }

    [Fact]
    public void A_missing_organization_service_factory_stays_an_InvalidPluginExecutionException_and_is_traced()
    {
        RecordingTracingService tracing = new RecordingTracingService();
        DictionaryServiceProviderStub provider = new DictionaryServiceProviderStub(new FakePluginExecutionContext(), tracing);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => new NoopPlugin().Execute(provider));

        Assert.Equal("IOrganizationServiceFactory is unavailable.", ex.Message);
        Assert.Contains("Failed to build the execution context", tracing.Output);
    }

    private sealed class DictionaryServiceProviderStub(IPluginExecutionContext context, ITracingService tracing) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPluginExecutionContext) ? context
            : serviceType == typeof(ITracingService) ? tracing
            : null;
    }

    [Fact]
    public void A_null_service_provider_is_rejected_as_an_InvalidPluginExecutionException()
    {
        Assert.Throws<InvalidPluginExecutionException>(() => new NoopPlugin().Execute(null!));
    }

    [Fact]
    public void Every_base_class_defaults_to_the_shared_depth_constant()
    {
        Assert.Equal(8, PluginBase.DefaultMaxDepth);
        Assert.Equal(PluginBase.DefaultMaxDepth, new NoopPlugin().ObservedMaxDepth);
        Assert.Equal(PluginBase.DefaultMaxDepth, new DefaultDepthEntityPlugin().ObservedMaxDepth);
    }

    [Fact]
    public void The_default_guard_runs_at_the_constant_and_stops_above_it()
    {
        NoopPlugin atLimit = new NoopPlugin();
        atLimit.Execute(new PluginContextBuilder().Depth(PluginBase.DefaultMaxDepth).BuildServiceProvider());
        Assert.True(atLimit.Ran);

        NoopPlugin above = new NoopPlugin();
        Assert.Throws<InvalidPluginExecutionException>(
            () => above.Execute(new PluginContextBuilder().Depth(PluginBase.DefaultMaxDepth + 1).BuildServiceProvider()));
        Assert.False(above.Ran);
    }

    [Fact]
    public void The_orphan_PluginBase_of_TConfig_is_gone()
    {
        // Removed in the breaking release: it was neither detected by the package scanner nor combinable with
        // EntityPluginBase / CustomApiBase, so a plugin deriving from it could never be registered.
        Assert.Null(typeof(PluginBase).Assembly.GetType("Vela.Plugin.PluginBase`1"));
    }

    private sealed class RetryingPlugin : PluginBase
    {
        public RetryingPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        public int Calls { get; private set; }

        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            ExecuteWithRetry<int>(ctx, () =>
            {
                Calls++;
                throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = -2147015902, Message = "throttled" });
            });
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public void The_context_aware_retry_helper_runs_once_inside_a_transaction(bool inTransaction, int expectedCalls)
    {
        PluginContextBuilder builder = new PluginContextBuilder();
        builder.Context.IsInTransaction = inTransaction;
        RetryingPlugin plugin = new RetryingPlugin();

        Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(builder.BuildServiceProvider()));

        Assert.Equal(expectedCalls, plugin.Calls);
    }

    // ---- Failure diagnostics are best-effort ---------------------------------------------------------

    private sealed class ThrowingTracer : ITracingService
    {
        public int Calls { get; private set; }

        public void Trace(string format, params object[] args)
        {
            Calls++;
            throw new FormatException("tracer down");
        }
    }

    [Fact]
    public void A_ConfigureServices_failure_survives_a_throwing_tracer()
    {
        ThrowingTracer tracer = new ThrowingTracer();
        IServiceProvider provider = new PluginContextBuilder().WithTracingService(tracer).BuildServiceProvider();
        ThrowingConfigurePlugin plugin = new ThrowingConfigurePlugin(new InvalidOperationException("registration boom"));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Contains("InvalidOperationException: registration boom", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.True(tracer.Calls > 0); // the handlers did try to trace
        Assert.True(plugin.Probe!.Disposed);
    }

    [Fact]
    public void A_business_exception_keeps_its_message_with_a_throwing_tracer()
    {
        IServiceProvider provider = new PluginContextBuilder().WithTracingService(new ThrowingTracer()).BuildServiceProvider();
        ThrowingConfigurePlugin plugin = new ThrowingConfigurePlugin(new PluginBusinessException("Configure says no."));

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Equal("Configure says no.", ex.Message);
    }

    [Fact]
    public void An_InvalidPluginExecutionException_is_rethrown_unchanged_with_a_throwing_tracer()
    {
        IServiceProvider provider = new PluginContextBuilder().WithTracingService(new ThrowingTracer()).BuildServiceProvider();
        InvalidPluginExecutionException original = new InvalidPluginExecutionException("clean failure");
        ThrowingConfigurePlugin plugin = new ThrowingConfigurePlugin(original);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

        Assert.Same(original, ex);
    }

    [Theory]
    [InlineData("boom", "InvalidOperationException: registrar boom")]
    [InlineData("business", "Registrar says no.")]
    public void An_assembly_registrar_failure_survives_a_throwing_tracer(string mode, string expectedMessage)
    {
        var builder = new PluginContextBuilder().WithTracingService(new ThrowingTracer());
        builder.WithSharedVariable(TestAssemblyRegistrar.ThrowSwitch, mode);
        NoopPlugin plugin = new NoopPlugin();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(builder.BuildServiceProvider()));

        if (mode == "business")
        {
            Assert.Equal(expectedMessage, ex.Message);
        }
        else
        {
            Assert.Contains(expectedMessage, ex.Message);
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }

        Assert.False(plugin.Ran);
    }
}
