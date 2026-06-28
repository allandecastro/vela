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

        Assert.Contains("IManagedIdentityService is unavailable", ex.Message);
    }
}
