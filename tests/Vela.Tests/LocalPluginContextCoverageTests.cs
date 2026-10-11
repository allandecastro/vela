using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Exercises LocalPluginContext branches the helper-focused suites don't reach: the constructor service
// fallbacks/guards, the shared-variable + service-scoping methods, Trace short-circuits, managed-identity
// token acquisition, and the reflection-based context-property resolvers (tenant id, bulk images).
public sealed class LocalPluginContextCoverageTests
{
    private static IServiceProvider Provider(
        IPluginExecutionContext? context,
        IOrganizationServiceFactory? factory,
        ITracingService? tracing)
    {
        Mock<IServiceProvider> provider = new Mock<IServiceProvider>(); // returns null for unconfigured services
        if (context != null)
        {
            provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(context);
        }

        if (factory != null)
        {
            provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(factory);
        }

        if (tracing != null)
        {
            provider.Setup(p => p.GetService(typeof(ITracingService))).Returns(tracing);
        }

        return provider.Object;
    }

    private static IOrganizationServiceFactory StubFactory()
    {
        Mock<IOrganizationServiceFactory> factory = new Mock<IOrganizationServiceFactory>();
        factory.Setup(f => f.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Mock.Of<IOrganizationService>());
        return factory.Object;
    }

    // ---- Constructor guards / fallbacks --------------------------------------------------------------

    [Fact]
    public void Constructor_rejects_a_null_service_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalPluginContext(null!));
    }

    [Fact]
    public void Constructor_throws_when_the_organization_service_factory_is_missing()
    {
        IServiceProvider provider = Provider(new FakePluginExecutionContext(), factory: null, tracing: Mock.Of<ITracingService>());
        Assert.Throws<InvalidPluginExecutionException>(() => new LocalPluginContext(provider));
    }

    [Fact]
    public void Constructor_throws_when_the_execution_context_is_missing()
    {
        IServiceProvider provider = Provider(context: null, factory: StubFactory(), tracing: Mock.Of<ITracingService>());
        Assert.Throws<InvalidPluginExecutionException>(() => new LocalPluginContext(provider));
    }

    [Fact]
    public void Constructor_falls_back_to_a_null_tracing_service_when_absent()
    {
        IServiceProvider provider = Provider(new FakePluginExecutionContext(), StubFactory(), tracing: null);
        LocalPluginContext context = new LocalPluginContext(provider);

        Assert.NotNull(context.TracingService); // NullTracingService fallback
        context.Trace("anything"); // must not throw with the fallback tracer
    }

    // ---- Shared variables ----------------------------------------------------------------------------

    [Fact]
    public void GetSharedVariable_returns_null_for_empty_or_missing_keys()
    {
        LocalPluginContext context = new PluginContextBuilder().WithSharedVariable("known", "v").Build();

        Assert.Null(context.GetSharedVariable(string.Empty));
        Assert.Null(context.GetSharedVariable("absent"));
        Assert.Equal("v", context.GetSharedVariable("known"));
    }

    [Fact]
    public void GetSharedVariable_walks_parent_contexts()
    {
        FakePluginExecutionContext parent = new FakePluginExecutionContext();
        parent.SharedVariables["fromParent"] = "p";

        LocalPluginContext context = new PluginContextBuilder().WithParentContext(parent).Build();
        Assert.Equal("p", context.GetSharedVariable("fromParent"));
    }

    [Fact]
    public void TryGetSharedVariable_returns_typed_value_or_false()
    {
        LocalPluginContext context = new PluginContextBuilder().WithSharedVariable("count", 5).WithTag("hello").Build();

        Assert.True(context.TryGetSharedVariable("count", out int count));
        Assert.Equal(5, count);
        Assert.False(context.TryGetSharedVariable("count", out string _)); // wrong type
        Assert.Equal("hello", context.Tag);
    }

    // ---- Service scoping -----------------------------------------------------------------------------

    [Fact]
    public void Service_acts_as_the_current_user_not_the_initiating_user()
    {
        // A step impersonating (or a CallerId call) makes UserId differ from InitiatingUserId: Service must follow
        // UserId, so the impersonation is honoured; SystemService is the null (SYSTEM) service; AsUser is the given id.
        Guid initiating = Guid.NewGuid(), current = Guid.NewGuid(), other = Guid.NewGuid();
        FakePluginExecutionContext execution = new FakePluginExecutionContext { InitiatingUserId = initiating, UserId = current };
        Mock<IOrganizationService> userService = new Mock<IOrganizationService>();
        Mock<IOrganizationService> systemService = new Mock<IOrganizationService>();
        Mock<IOrganizationService> otherService = new Mock<IOrganizationService>();
        Mock<IOrganizationServiceFactory> factory = new Mock<IOrganizationServiceFactory>();
        factory.Setup(f => f.CreateOrganizationService(current)).Returns(userService.Object);
        factory.Setup(f => f.CreateOrganizationService(null)).Returns(systemService.Object);
        factory.Setup(f => f.CreateOrganizationService(other)).Returns(otherService.Object);

        LocalPluginContext context = new LocalPluginContext(Provider(execution, factory.Object, tracing: null));
        context.Service.Delete("account", Guid.Empty);
        context.SystemService.Delete("account", Guid.Empty);
        context.AsUser(other).Delete("account", Guid.Empty);

        userService.Verify(s => s.Delete("account", Guid.Empty), Times.Once);
        systemService.Verify(s => s.Delete("account", Guid.Empty), Times.Once);
        otherService.Verify(s => s.Delete("account", Guid.Empty), Times.Once);
        factory.Verify(f => f.CreateOrganizationService(initiating), Times.Never);
        Assert.Throws<ArgumentException>(() => context.AsUser(Guid.Empty));
    }

    // ---- Trace ---------------------------------------------------------------------------------------

    [Fact]
    public void Trace_ignores_blank_formats_and_formats_arguments()
    {
        RecordingTracingService recorder = new RecordingTracingService();
        LocalPluginContext context = new PluginContextBuilder().WithTracingService(recorder).Build();

        context.Trace("   ");                 // blank -> ignored
        context.Trace("hello {0}", "world");  // formatted

        string only = Assert.Single(recorder.Traces);
        Assert.Equal("hello world", only);
    }

    // ---- Managed identity ----------------------------------------------------------------------------

    [Fact]
    public void AcquireManagedIdentityToken_two_arg_overload_returns_a_token()
    {
        Mock<IManagedIdentityService> managedIdentity = new Mock<IManagedIdentityService>();
        managedIdentity.Setup(s => s.AcquireToken(It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>())).Returns("tok");

        LocalPluginContext context = new PluginContextBuilder().WithManagedIdentityService(managedIdentity.Object).Build();
        Assert.Equal("tok", context.AcquireManagedIdentityToken(Guid.NewGuid(), new[] { "scope" }));
    }

    [Fact]
    public void AcquireManagedIdentityToken_guards_null_scopes_and_missing_service()
    {
        LocalPluginContext context = new PluginContextBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => context.AcquireManagedIdentityToken(null!));
        Assert.Throws<ArgumentNullException>(() => context.AcquireManagedIdentityToken(Guid.NewGuid(), null!));
        Assert.Throws<InvalidPluginExecutionException>(() => context.AcquireManagedIdentityToken(Guid.NewGuid(), new[] { "scope" }));
    }

    // ---- Reflection-resolved context properties ------------------------------------------------------

    [Fact]
    public void Tenant_id_is_resolved_from_the_context_and_empty_maps_to_null()
    {
        Guid tenant = Guid.NewGuid();
        Assert.Equal(tenant, new PluginContextBuilder().WithTenantId(tenant).Build().TenantId);
        Assert.Null(new PluginContextBuilder().Build().TenantId); // default Guid.Empty -> null
    }

    [Fact]
    public void Environment_id_is_read_as_the_platform_string_and_empty_maps_to_null()
    {
        // IPluginExecutionContext6.EnvironmentId is a string, not a Guid.
        const string environment = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        Assert.Equal(environment, new PluginContextBuilder().WithEnvironmentId(environment).Build().EnvironmentId);
        Assert.Null(new PluginContextBuilder().Build().EnvironmentId); // default string.Empty -> null
    }

    [Fact]
    public void Bulk_operation_flag_and_image_collections_reflect_the_context()
    {
        LocalPluginContext plain = new PluginContextBuilder().Build();
        Assert.False(plain.IsBulkOperation);
        Assert.Empty(plain.BulkPreEntityImagesCollection);

        EntityImageCollection images = new EntityImageCollection();
        images["PreImage"] = new Entity("account", Guid.NewGuid());
        LocalPluginContext bulk = new PluginContextBuilder()
            .OnMessage("UpdateMultiple")
            .WithTargets(new EntityCollection())
            .WithBulkPreImages(images)
            .Build();

        Assert.True(bulk.IsBulkOperation);
        Assert.Single(bulk.BulkPreEntityImagesCollection);
    }

    [Theory]
    [InlineData("CreateMultiple", true)]
    [InlineData("UpdateMultiple", true)]
    [InlineData("UpsertMultiple", true)]
    [InlineData("vela_ImportRows", false)]   // a Custom API whose EntityCollection input is named Targets
    [InlineData("Update", false)]
    public void Only_the_bulk_messages_are_bulk_operations(string message, bool bulk)
    {
        LocalPluginContext ctx = new PluginContextBuilder()
            .OnMessage(message)
            .WithTargets(new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()) }))
            .Build();

        Assert.Equal(bulk, ctx.IsBulkOperation);
        Assert.Equal(bulk, ctx.BulkTargets != null);
    }
}
