using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.DI;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers the standalone Vela.Plugins utility classes that had no direct tests: the manifest readers/comparer/
// writer, the step-definition collection helper, and the Microsoft.Extensions.DependencyInjection registration.
public sealed class PluginRuntimeCoverageTests
{
    // ---- doubles -------------------------------------------------------------------------------------

    private sealed class SampleStepPlugin(string unsecure, string secure) : EntityPluginBase<TestAccount>(unsecure, secure)
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .WithFiltering(a => a.Name)
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
        IEnumerable<StepDefinition> all = StepDefinitionReader.GetStepDefinitions(typeof(PluginRuntimeCoverageTests).Assembly);

        Assert.Contains(all, s => s.Message == PluginMessages.Update && s.Stage == PluginStages.PostOperation);
    }

    // ---- CustomApiDefinitionReader -------------------------------------------------------------------

    private sealed class SampleApi(string unsecure, string secure) : CustomApiBase(unsecure, secure)
    {
        public const string Name = "vela_RuntimeCoverageSample";

        protected override void RegisterCustomApi() => RegisterCustomApi(Name, nameof(Handle));

        private void Handle(ILocalPluginContext ctx) { }
    }

    [Fact]
    public void CustomApiDefinitionReader_rejects_a_null_assembly()
    {
        Assert.Throws<ArgumentNullException>(() => CustomApiDefinitionReader.GetCustomApiDefinitions(null!));
    }

    [Fact]
    public void CustomApiDefinitionReader_discovers_custom_apis_in_an_assembly()
    {
        IReadOnlyList<CustomApiDefinition> apis = CustomApiDefinitionReader.GetCustomApiDefinitions(typeof(PluginRuntimeCoverageTests).Assembly);

        Assert.Contains(apis, a => a.UniqueName == SampleApi.Name);
    }

    // ---- CustomApiManifestComparer -------------------------------------------------------------------

    private static CustomApiDefinition Api(string name, string display = "D", bool isFunction = false)
        => new CustomApiDefinition { UniqueName = name, DisplayName = display, IsFunction = isFunction };

    [Fact]
    public void CustomApiManifestComparer_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => CustomApiManifestComparer.Compare(null!, Array.Empty<CustomApiDefinition>()));
        Assert.Throws<ArgumentNullException>(() => CustomApiManifestComparer.Compare(Array.Empty<CustomApiDefinition>(), null!));
    }

    [Fact]
    public void CustomApiManifestComparer_classifies_added_removed_and_changed()
    {
        CustomApiDefinition[] desired =
        {
            Api("vela_keep"),
            Api("vela_added"),
            Api("vela_changed", display: "New"),
        };
        CustomApiDefinition[] current =
        {
            Api("vela_keep"),
            Api("vela_removed"),
            Api("vela_changed", display: "Old"),
        };

        CustomApiManifestDiff diff = CustomApiManifestComparer.Compare(desired, current);

        Assert.Equal("vela_added", Assert.Single(diff.Added).UniqueName);
        Assert.Equal("vela_removed", Assert.Single(diff.Removed).UniqueName);
        CustomApiDefinitionChange change = Assert.Single(diff.Changed);
        Assert.Equal("vela_changed", change.UniqueName);
        CustomApiPropertyChange prop = Assert.Single(change.PropertyChanges);
        Assert.Equal(nameof(CustomApiDefinition.DisplayName), prop.PropertyName);
        Assert.Equal("Old", prop.Before);
        Assert.Equal("New", prop.After);
    }

    [Fact]
    public void CustomApiManifestComparer_detects_non_string_and_parameter_changes()
    {
        CustomApiDefinition desired = Api("vela_api", isFunction: true);
        desired.InputParameters = new[] { new CustomApiParameterDefinition { Name = "Amount", ParameterTypeName = "Integer" } };
        CustomApiDefinition current = Api("vela_api", isFunction: false);

        CustomApiDefinitionChange change = Assert.Single(CustomApiManifestComparer.Compare(new[] { desired }, new[] { current }).Changed);

        Assert.Contains(change.PropertyChanges, p => p.PropertyName == nameof(CustomApiDefinition.IsFunction));
        Assert.Contains(change.PropertyChanges, p => p.PropertyName == nameof(CustomApiDefinition.InputParameters));
    }

    [Fact]
    public void CustomApiManifestComparer_reports_no_changes_for_identical_definitions()
    {
        CustomApiManifestDiff diff = CustomApiManifestComparer.Compare(new[] { Api("vela_same") }, new[] { Api("vela_same") });

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    // ---- CustomApiManifestWriter ---------------------------------------------------------------------

    [Fact]
    public void CustomApiManifestWriter_serializes_and_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => CustomApiManifestWriter.ToJson(null!));

        string json = CustomApiManifestWriter.ToJson(new CustomApiManifest
        {
            SourceAssemblyName = "Vela.Sample",
            CustomApis = new[] { Api("vela_api") },
        });

        Assert.Contains("vela_api", json);
        Assert.Contains("Vela.Sample", json);
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
        ctx.SetupGet(c => c.UserService).Returns(Mock.Of<IOrganizationService>());
        ctx.SetupGet(c => c.SystemService).Returns(Mock.Of<IOrganizationService>());
        ctx.SetupGet(c => c.Logger).Returns(withLogger ? Mock.Of<ILogger>() : null!);
        ctx.SetupGet(c => c.ManagedIdentityService).Returns(withManagedIdentity ? Mock.Of<IManagedIdentityService>() : null);
        ctx.Setup(c => c.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Mock.Of<IOrganizationService>());
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
        ServiceProvider provider = new ServiceCollection()
            .AddPluginRuntime(ContextMock(withManagedIdentity: true, withLogger: true).Object)
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<ILocalPluginContext>());
        Assert.NotNull(provider.GetService<IPluginExecutionContext>());
        Assert.NotNull(provider.GetService<ITracingService>());
        Assert.NotNull(provider.GetService<IOrganizationService>());
        Assert.NotNull(provider.GetService<ILogger>());
        Assert.NotNull(provider.GetService<IManagedIdentityService>());

        Func<Guid?, IOrganizationService> factory = provider.GetRequiredService<Func<Guid?, IOrganizationService>>();
        Assert.NotNull(factory(Guid.NewGuid()));
    }

    [Fact]
    public void AddPluginRuntime_omits_managed_identity_and_falls_back_to_null_logger_when_absent()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddPluginRuntime(ContextMock(withManagedIdentity: false, withLogger: false).Object)
            .BuildServiceProvider();

        Assert.Null(provider.GetService<IManagedIdentityService>());
        Assert.Same(NullLogger.Instance, provider.GetRequiredService<ILogger>());
    }
}
