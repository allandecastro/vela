using System;
using Vela.Context;
using Vela.Testing;
using Microsoft.Xrm.Sdk;
using Moq;
using Xunit;

namespace Vela.Tests;

public sealed class ManagedIdentityTests
{
    [Fact]
    public void AcquireManagedIdentityToken_ReturnsToken()
    {
        Mock<IManagedIdentityService> managedIdentity = new Mock<IManagedIdentityService>();
        managedIdentity
            .Setup(service => service.AcquireToken(It.IsAny<string[]>()))
            .Returns("token-123");

        LocalPluginContext context = new PluginContextBuilder().WithManagedIdentityService(managedIdentity.Object).Build();
        string token = context.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });

        Assert.Equal("token-123", token);
    }

    [Fact]
    public void AcquireManagedIdentityToken_ThrowsWhenUnavailable()
    {
        LocalPluginContext context = new PluginContextBuilder().Build();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => context.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" }));

        Assert.Equal(ExpectedUnavailableMessage, ex.Message);
    }

    private const string ExpectedUnavailableMessage =
        "No IManagedIdentityService was provided to this plug-in. In a test, give the harness one with WithManagedIdentityService(...). On Dataverse, the plug-in package or assembly must be linked to a managed identity record: see https://learn.microsoft.com/power-platform/admin/set-up-managed-identity.";

    [Fact]
    public void AcquireManagedIdentityToken_BothOverloadsExplainHowToProvideTheService()
    {
        LocalPluginContext context = new PluginContextBuilder().Build();
        string[] scopes = { "https://graph.microsoft.com/.default" };

        Assert.Equal(ExpectedUnavailableMessage,
            Assert.Throws<InvalidPluginExecutionException>(() => context.AcquireManagedIdentityToken(scopes)).Message);
        Assert.Equal(ExpectedUnavailableMessage,
            Assert.Throws<InvalidPluginExecutionException>(() => context.AcquireManagedIdentityToken(Guid.NewGuid(), scopes)).Message);
        Assert.Equal(LocalPluginContext.ManagedIdentityUnavailableMessage, ExpectedUnavailableMessage);
    }
}
