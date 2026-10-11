using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Context;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Constructs LocalPluginContext directly over a fully-populated execution context so the extended context members
// (read through the IPluginExecutionContext2..7 interfaces, plus bulk targets/images) hit their value-present
// branches. SdkContractTests covers the same reads against an explicitly implemented context.
public sealed class LocalPluginContextReflectionCoverageTests
{
    private static IServiceProvider Provider(IPluginExecutionContext context)
    {
        Mock<IOrganizationServiceFactory> factory = new Mock<IOrganizationServiceFactory>();
        factory.Setup(f => f.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Mock.Of<IOrganizationService>());

        Mock<IServiceProvider> provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(context);
        provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(factory.Object);
        provider.Setup(p => p.GetService(typeof(ITracingService))).Returns(Mock.Of<ITracingService>());
        return provider.Object;
    }

    [Fact]
    public void Extended_members_read_present_values_from_the_context()
    {
        Guid tenant = Guid.NewGuid();
        Guid portalsContact = Guid.NewGuid();
        EntityImageCollection preImages = new EntityImageCollection();
        preImages["PreImage"] = new Entity("account", Guid.NewGuid());

        FakePluginExecutionContext fake = new FakePluginExecutionContext
        {
            MessageName = "UpdateMultiple",
            IsPortalsClientCall = true,
            PortalsContactId = portalsContact,
            InitiatingUserAgent = "vela-agent/1.0",
            IsApplicationUser = true,
            TenantId = tenant,
            EnvironmentId = "env-42",
            PreEntityImagesCollection = new[] { preImages },
        };
        fake.InputParameters["Targets"] = new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()) });

        LocalPluginContext ctx = new LocalPluginContext(Provider(fake));

        Assert.True(ctx.IsPortalsClientCall);
        Assert.Equal(portalsContact, ctx.PortalsContactId);
        Assert.Equal("vela-agent/1.0", ctx.InitiatingUserAgent);
        Assert.True(ctx.IsApplicationUser);
        Assert.Equal(tenant, ctx.TenantId);
        Assert.Equal("env-42", ctx.EnvironmentId);
        Assert.True(ctx.IsBulkOperation);                          // ResolveBulkTargets -> Targets present
        Assert.Single(ctx.BulkPreEntityImagesCollection);
    }

    [Fact]
    public void Extended_members_fall_back_when_the_context_lacks_the_newer_interfaces()
    {
        // A base IPluginExecutionContext mock implements none of IPluginExecutionContext2..7, so every extended
        // member returns its default.
        Mock<IPluginExecutionContext> bare = new Mock<IPluginExecutionContext>();
        bare.SetupGet(c => c.InputParameters).Returns(new ParameterCollection()); // no "Targets"

        LocalPluginContext ctx = new LocalPluginContext(Provider(bare.Object));

        Assert.Null(ctx.TenantId);
        Assert.Null(ctx.EnvironmentId);
        Assert.Null(ctx.PortalsContactId);
        Assert.False(ctx.IsPortalsClientCall);
        Assert.False(ctx.IsApplicationUser);
        Assert.Equal(string.Empty, ctx.InitiatingUserAgent);
        Assert.False(ctx.IsBulkOperation);
        Assert.Empty(ctx.BulkPreEntityImagesCollection);
    }

    [Fact]
    public void Trace_writes_through_to_the_tracing_service()
    {
        RecordingTracingService recorder = new RecordingTracingService();
        Mock<IOrganizationServiceFactory> factory = new Mock<IOrganizationServiceFactory>();
        factory.Setup(f => f.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Mock.Of<IOrganizationService>());
        Mock<IServiceProvider> provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(new FakePluginExecutionContext());
        provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(factory.Object);
        provider.Setup(p => p.GetService(typeof(ITracingService))).Returns(recorder);

        LocalPluginContext ctx = new LocalPluginContext(provider.Object);
        ctx.Trace("hello {0}", "world");

        Assert.Contains("hello world", recorder.Traces);
    }
}
