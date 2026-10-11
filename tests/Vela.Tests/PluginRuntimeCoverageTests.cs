using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Caching;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.DI;
using Vela.Localization;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers the standalone Vela.Plugins utility classes that had no direct tests: the step-definition reader,
// the step-definition collection helper, and the Microsoft.Extensions.DependencyInjection registration.
public sealed class PluginRuntimeCoverageTests
{
    // ---- doubles -------------------------------------------------------------------------------------

    private sealed class SampleStepPlugin(string unsecure, string secure) : EntityPluginBase<TestAccount>(unsecure, secure)
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(OnUpdate);

        private void OnUpdate(IUpdatePostContext<TestAccount> ctx) { }
    }

    private sealed class NotAPlugin
    {
    }

    // ---- StepDefinitionReader ------------------------------------------------------------------------

    [Fact]
    public void StepDefinitionReader_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => StepDefinitionReader.GetStepDefinitions(null!));
        Assert.Throws<ArgumentNullException>(() => StepDefinitionReader.GetStepDefinitionFromType(null!));
    }

    [Fact]
    public void StepDefinitionReader_returns_null_for_a_non_plugin_type()
    {
        Assert.Null(StepDefinitionReader.GetStepDefinitionFromType(typeof(NotAPlugin)));
    }

    [Fact]
    public void StepDefinitionReader_reads_the_steps_off_an_entity_plugin()
    {
        StepDefinition[]? steps = StepDefinitionReader.GetStepDefinitionFromType(typeof(SampleStepPlugin));

        StepDefinition step = Assert.Single(steps!);
        Assert.Equal(PluginMessages.Update, step.Message);
        Assert.Equal(PluginStages.PostOperation, step.Stage);
    }

    [Fact]
    public void StepDefinitionReader_scans_an_assembly_for_every_plugin()
    {
        List<Vela.Manifest.FrameworkManifestDiagnostic> diagnostics = new List<Vela.Manifest.FrameworkManifestDiagnostic>();
        IEnumerable<StepDefinition> all = StepDefinitionReader.GetStepDefinitions(typeof(PluginRuntimeCoverageTests).Assembly, diagnostics);

        Assert.Contains(all, s => s.Message == PluginMessages.Update && s.Stage == PluginStages.PostOperation);
    }

    // ---- StepDefinitionCollectionExtensions ----------------------------------------------------------

    [Fact]
    public void AddRange_appends_all_definitions_and_guards_null()
    {
        List<StepDefinition> steps = new List<StepDefinition>();
        Assert.Throws<ArgumentNullException>(() => ((ICollection<StepDefinition>)null!).AddRange(steps));
        Assert.Throws<ArgumentNullException>(() => steps.AddRange(null!));

        steps.AddRange(new[] { new StepDefinition(), new StepDefinition() });
        Assert.Equal(2, steps.Count);
    }

    // ---- MicrosoftDiExtensions -----------------------------------------------------------------------

    private static Mock<ILocalPluginContext> ContextMock(bool withManagedIdentity, bool withLogger)
    {
        Mock<ILocalPluginContext> ctx = new Mock<ILocalPluginContext>();
        ctx.SetupGet(c => c.PluginExecutionContext).Returns(Mock.Of<IPluginExecutionContext>());
        ctx.SetupGet(c => c.TracingService).Returns(Mock.Of<ITracingService>());
        ctx.SetupGet(c => c.Service).Returns(Mock.Of<IOrganizationService>());
        ctx.SetupGet(c => c.SystemService).Returns(Mock.Of<IOrganizationService>());
        ctx.SetupGet(c => c.Logger).Returns(withLogger ? Mock.Of<ILogger>() : null!);
        ctx.SetupGet(c => c.ManagedIdentityService).Returns(withManagedIdentity ? Mock.Of<IManagedIdentityService>() : null);
        return ctx;
    }

    [Fact]
    public void AddPluginRuntime_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => MicrosoftDiExtensions.AddPluginRuntime(null!, ContextMock(false, false).Object));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddPluginRuntime(null!));
    }

    [Fact]
    public void AddPluginRuntime_registers_the_runtime_services_including_managed_identity()
    {
        Mock<ILocalPluginContext> context = ContextMock(withManagedIdentity: true, withLogger: true);
        ServiceProvider provider = new ServiceCollection()
            .AddPluginRuntime(context.Object)
            .BuildServiceProvider();

        Assert.Same(context.Object, provider.GetService<ILocalPluginContext>());
        Assert.Same(context.Object.PluginExecutionContext, provider.GetService<IPluginExecutionContext>());
        Assert.Same(context.Object.TracingService, provider.GetService<ITracingService>());
        // The injected service is the current user's, never SYSTEM (both used to be registered; the last one won).
        Assert.Same(context.Object.Service, provider.GetService<IOrganizationService>());
        Assert.NotSame(context.Object.SystemService, provider.GetService<IOrganizationService>());
        Assert.NotNull(provider.GetService<ILogger>());
        Assert.NotNull(provider.GetService<IManagedIdentityService>());
        Assert.NotNull(provider.GetService<IPluginCacheService>());
        Assert.NotNull(provider.GetService<IEnvironmentVariableReader>());
        Assert.NotNull(provider.GetService<IMessageService>());
    }

    [Fact]
    public void AddPluginRuntime_and_the_built_in_resolver_register_the_same_services()
    {
        ILocalPluginContext context = ContextMock(withManagedIdentity: true, withLogger: true).Object;
        ServiceProvider provider = new ServiceCollection().AddPluginRuntime(context).BuildServiceProvider();
        using SimpleServiceResolver resolver = SimpleServiceResolver.CreateDefault(context);

        foreach (KeyValuePair<Type, Func<object>> registration in SimpleServiceResolver.DefaultServices(context))
        {
            Assert.NotNull(provider.GetService(registration.Key));
            Assert.NotNull(resolver.GetService(registration.Key));
        }

        Assert.Same(context.Service, resolver.GetService(typeof(IOrganizationService)));
    }

    [Fact]
    public void AddPluginRuntime_omits_managed_identity_and_falls_back_to_null_logger_when_absent()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddPluginRuntime(ContextMock(withManagedIdentity: false, withLogger: false).Object)
            .BuildServiceProvider();

        Assert.Null(provider.GetService<IManagedIdentityService>());
        Assert.Same(NullPluginTelemetryLogger.Instance, provider.GetRequiredService<ILogger>());
    }
}
