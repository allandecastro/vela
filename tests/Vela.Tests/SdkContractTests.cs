using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Context;
using Vela.Testing;
using Xunit;
using TelemetryLogger = Microsoft.Xrm.Sdk.PluginTelemetry.ILogger;
using TelemetryLogLevel = Microsoft.Xrm.Sdk.PluginTelemetry.LogLevel;

namespace Vela.Tests;

/// <summary>
/// Contract tests (#448): the runtime against the real <c>Microsoft.Xrm.Sdk</c> interfaces, the way the platform
/// serves them. Every service goes through a raw <see cref="IServiceProvider"/> keyed by the SDK type (not the
/// harness builder), and the execution context is a fake whose SDK members are all implemented explicitly, so a read
/// that only works against a public property of a test double (reflection on the concrete type) fails here. The bugs
/// in #434 (telemetry through the wrong <c>ILogger</c>) and #437 (<c>EnvironmentId</c> read as a Guid) both passed the
/// suite because it only exercised harness-shaped services.
/// </summary>
public sealed class SdkContractTests
{
    private static Mock<IServiceProvider> Provider(Type contextKey, object context)
    {
        Mock<IOrganizationServiceFactory> factory = new Mock<IOrganizationServiceFactory>();
        factory.Setup(f => f.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Mock.Of<IOrganizationService>());

        Mock<IServiceProvider> provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(contextKey)).Returns(context);
        provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(factory.Object);
        provider.Setup(p => p.GetService(typeof(ITracingService))).Returns(Mock.Of<ITracingService>());
        return provider;
    }

    [Theory]
    [InlineData(typeof(IPluginExecutionContext))]
    [InlineData(typeof(IPluginExecutionContext7))]
    public void Extended_context_members_are_read_through_the_SDK_interfaces(Type servedAs)
    {
        Guid tenant = Guid.NewGuid();
        Guid portalsContact = Guid.NewGuid();
        EntityImageCollection pre = new EntityImageCollection { ["pre"] = new Entity("account", Guid.NewGuid()) };
        EntityImageCollection post = new EntityImageCollection { ["post"] = new Entity("account", Guid.NewGuid()) };
        FakePluginExecutionContext values = new FakePluginExecutionContext
        {
            MessageName = "UpdateMultiple",
            EnvironmentId = "8f3c2b1e-env",
            TenantId = tenant,
            InitiatingUserAgent = "vela-agent/1.0",
            IsPortalsClientCall = true,
            PortalsContactId = portalsContact,
            IsApplicationUser = true,
            PreEntityImagesCollection = new[] { pre },
            PostEntityImagesCollection = new[] { post },
        };
        values.InputParameters["Targets"] = new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()) });

        LocalPluginContext ctx = new LocalPluginContext(Provider(servedAs, new ExplicitPluginExecutionContext(values)).Object);

        Assert.Equal("8f3c2b1e-env", ctx.EnvironmentId); // IPluginExecutionContext6.EnvironmentId is a string (#437)
        Assert.Equal(tenant, ctx.TenantId);
        Assert.Equal("vela-agent/1.0", ctx.InitiatingUserAgent);
        Assert.True(ctx.IsPortalsClientCall);
        Assert.Equal(portalsContact, ctx.PortalsContactId);
        Assert.True(ctx.IsApplicationUser);
        Assert.True(ctx.IsBulkOperation);
        Assert.Same(pre, Assert.Single(ctx.BulkPreEntityImagesCollection));
        Assert.Same(post, Assert.Single(ctx.BulkPostEntityImagesCollection));
    }

    [Fact]
    public void Empty_extended_context_members_resolve_to_null()
    {
        FakePluginExecutionContext values = new FakePluginExecutionContext
        {
            EnvironmentId = string.Empty,
            TenantId = Guid.Empty,
            PortalsContactId = Guid.Empty,
            InitiatingUserAgent = null!,
            PreEntityImagesCollection = null!,
        };

        LocalPluginContext ctx = new LocalPluginContext(
            Provider(typeof(IPluginExecutionContext), new ExplicitPluginExecutionContext(values)).Object);

        Assert.Null(ctx.EnvironmentId);
        Assert.Null(ctx.TenantId);
        Assert.Null(ctx.PortalsContactId);
        Assert.Equal(string.Empty, ctx.InitiatingUserAgent);
        Assert.Empty(ctx.BulkPreEntityImagesCollection);
    }

    [Fact]
    public void Telemetry_reaches_the_logger_the_platform_serves_as_PluginTelemetry_ILogger()
    {
        // #434: the platform serves Microsoft.Xrm.Sdk.PluginTelemetry.ILogger, never the MEL ILogger.
        Mock<TelemetryLogger> logger = new Mock<TelemetryLogger>();
        Mock<IServiceProvider> provider = Provider(typeof(IPluginExecutionContext), new ExplicitPluginExecutionContext(new FakePluginExecutionContext()));
        provider.Setup(p => p.GetService(typeof(TelemetryLogger))).Returns(logger.Object);
        LocalPluginContext ctx = new LocalPluginContext(provider.Object);

        using (ctx.BeginLogScope(("OrderId", 7)))
        {
            ctx.LogInformation("hello {0}", "world");
        }

        ctx.LogMetric("Orders", 4, new Dictionary<string, object?> { ["Region"] = "EU" });

        Assert.Same(logger.Object, ctx.Logger);
        logger.Verify(l => l.Log(TelemetryLogLevel.Information, (Exception)null!, "{0}", It.Is<object[]>(a => (string)a[0] == "hello world")), Times.Once);
        logger.Verify(l => l.BeginScope(It.Is<IReadOnlyDictionary<string, object?>>(d => d.ContainsKey("OrderId") && (int?)d["OrderId"] == 7)), Times.Once);
        logger.Verify(l => l.LogMetric("Orders", It.Is<IDictionary<string, string>>(d => d["Region"] == "EU"), 4L), Times.Once);
    }

    [Fact]
    public void Managed_identity_tokens_come_from_the_SDK_service_with_both_overloads()
    {
        Guid applicationUser = Guid.NewGuid();
        string[] scopes = { "https://graph.microsoft.com/.default" };
        RecordingManagedIdentityService identity = new RecordingManagedIdentityService();
        Mock<IServiceProvider> provider = Provider(typeof(IPluginExecutionContext), new ExplicitPluginExecutionContext(new FakePluginExecutionContext()));
        provider.Setup(p => p.GetService(typeof(IManagedIdentityService))).Returns(identity);
        LocalPluginContext ctx = new LocalPluginContext(provider.Object);

        Assert.Equal("token:" + scopes[0], ctx.AcquireManagedIdentityToken(scopes));
        Assert.Equal("token:" + applicationUser + ":" + scopes[0], ctx.AcquireManagedIdentityToken(applicationUser, scopes));
        Assert.Same(identity, ctx.ManagedIdentityService);
    }

    /// <summary>An <see cref="IManagedIdentityService"/> implemented explicitly, as an opaque platform type would be.</summary>
    private sealed class RecordingManagedIdentityService : IManagedIdentityService
    {
        string IManagedIdentityService.AcquireToken(IEnumerable<string> scopes) => "token:" + string.Join(",", scopes);

        string IManagedIdentityService.AcquireToken(Guid applicationUserId, IEnumerable<string> scopes)
            => "token:" + applicationUserId + ":" + string.Join(",", scopes);
    }

    /// <summary>
    /// The full <see cref="IPluginExecutionContext7"/> chain with every member implemented explicitly: the concrete
    /// type has no public property, so only a read through the SDK interface sees a value. The platform's own context
    /// class is opaque; nothing guarantees it exposes these members publicly.
    /// </summary>
    private sealed class ExplicitPluginExecutionContext : IPluginExecutionContext7
    {
        private readonly FakePluginExecutionContext _v;

        public ExplicitPluginExecutionContext(FakePluginExecutionContext values) => _v = values;

        int IExecutionContext.Mode => _v.Mode;
        int IExecutionContext.IsolationMode => _v.IsolationMode;
        int IExecutionContext.Depth => _v.Depth;
        string IExecutionContext.MessageName => _v.MessageName;
        string IExecutionContext.PrimaryEntityName => _v.PrimaryEntityName;
        Guid? IExecutionContext.RequestId => _v.RequestId;
        string IExecutionContext.SecondaryEntityName => _v.SecondaryEntityName;
        ParameterCollection IExecutionContext.InputParameters => _v.InputParameters;
        ParameterCollection IExecutionContext.OutputParameters => _v.OutputParameters;
        ParameterCollection IExecutionContext.SharedVariables => _v.SharedVariables;
        Guid IExecutionContext.UserId => _v.UserId;
        Guid IExecutionContext.InitiatingUserId => _v.InitiatingUserId;
        Guid IExecutionContext.BusinessUnitId => _v.BusinessUnitId;
        Guid IExecutionContext.OrganizationId => _v.OrganizationId;
        string IExecutionContext.OrganizationName => _v.OrganizationName;
        Guid IExecutionContext.PrimaryEntityId => _v.PrimaryEntityId;
        EntityImageCollection IExecutionContext.PreEntityImages => _v.PreEntityImages;
        EntityImageCollection IExecutionContext.PostEntityImages => _v.PostEntityImages;
        EntityReference IExecutionContext.OwningExtension => _v.OwningExtension!;
        Guid IExecutionContext.CorrelationId => _v.CorrelationId;
        bool IExecutionContext.IsExecutingOffline => _v.IsExecutingOffline;
        bool IExecutionContext.IsOfflinePlayback => _v.IsOfflinePlayback;
        bool IExecutionContext.IsInTransaction => _v.IsInTransaction;
        Guid IExecutionContext.OperationId => _v.OperationId;
        DateTime IExecutionContext.OperationCreatedOn => _v.OperationCreatedOn;
        int IPluginExecutionContext.Stage => _v.Stage;
        IPluginExecutionContext IPluginExecutionContext.ParentContext => _v.ParentContext!;
        Guid IPluginExecutionContext2.UserAzureActiveDirectoryObjectId => _v.UserAzureActiveDirectoryObjectId;
        Guid IPluginExecutionContext2.InitiatingUserAzureActiveDirectoryObjectId => _v.InitiatingUserAzureActiveDirectoryObjectId;
        Guid IPluginExecutionContext2.InitiatingUserApplicationId => _v.InitiatingUserApplicationId;
        Guid IPluginExecutionContext2.PortalsContactId => _v.PortalsContactId;
        bool IPluginExecutionContext2.IsPortalsClientCall => _v.IsPortalsClientCall;
        Guid IPluginExecutionContext3.AuthenticatedUserId => _v.AuthenticatedUserId;
        EntityImageCollection[] IPluginExecutionContext4.PreEntityImagesCollection => _v.PreEntityImagesCollection;
        EntityImageCollection[] IPluginExecutionContext4.PostEntityImagesCollection => _v.PostEntityImagesCollection;
        string IPluginExecutionContext5.InitiatingUserAgent => _v.InitiatingUserAgent;
        string IPluginExecutionContext6.EnvironmentId => _v.EnvironmentId;
        Guid IPluginExecutionContext6.TenantId => _v.TenantId;
        bool IPluginExecutionContext7.IsApplicationUser => _v.IsApplicationUser;
    }
}
