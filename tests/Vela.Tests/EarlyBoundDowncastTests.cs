using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using Vela.Context;
using Xunit;

namespace Vela.Tests;

// TracingOrganizationService must downcast early-bound entities to the base Entity before delegating, so that
// ctx.UserService.Create/Update/Execute works on a real environment even when the plugin assembly is not marked
// [assembly: ProxyTypesAssembly] (otherwise the sandbox throws a SerializationException on the generated subclass).
public sealed class EarlyBoundDowncastTests
{
    [EntityLogicalName("account")]
    private sealed class FakeAccount : Entity
    {
        public FakeAccount()
            : base("account")
        {
        }

        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }
    }

    private static (TracingOrganizationService Svc, Mock<IOrganizationService> Inner) Build()
    {
        Mock<ILocalPluginContext> ctx = new Mock<ILocalPluginContext>();
        ctx.SetupGet(c => c.MessageName).Returns("Update");
        ctx.SetupGet(c => c.Stage).Returns(40);
        ctx.SetupGet(c => c.Mode).Returns(0);
        ctx.SetupGet(c => c.Depth).Returns(1);
        ctx.SetupGet(c => c.CorrelationId).Returns(Guid.NewGuid());
        Mock<IOrganizationService> inner = new Mock<IOrganizationService>();
        return (new TracingOrganizationService(inner.Object, ctx.Object, "S"), inner);
    }

    [Fact]
    public void Create_downcasts_early_bound_entity_to_base_Entity()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        Entity? captured = null;
        inner.Setup(s => s.Create(It.IsAny<Entity>())).Callback<Entity>(e => captured = e).Returns(Guid.NewGuid());

        svc.Create(new FakeAccount { Name = "X" });

        Assert.NotNull(captured);
        Assert.Equal(typeof(Entity), captured!.GetType());
        Assert.Equal("account", captured.LogicalName);
        Assert.Equal("X", captured.GetAttributeValue<string>("name"));
    }

    [Fact]
    public void Update_downcasts_early_bound_entity_to_base_Entity()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        Entity? captured = null;
        inner.Setup(s => s.Update(It.IsAny<Entity>())).Callback<Entity>(e => captured = e);

        Guid id = Guid.NewGuid();
        svc.Update(new FakeAccount { Id = id, Name = "Y" });

        Assert.NotNull(captured);
        Assert.Equal(typeof(Entity), captured!.GetType());
        Assert.Equal(id, captured.Id);
        Assert.Equal("Y", captured.GetAttributeValue<string>("name"));
    }

    [Fact]
    public void Execute_downcasts_early_bound_entities_in_request_parameters()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        OrganizationRequest? captured = null;
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => captured = r)
            .Returns(new OrganizationResponse());

        EntityCollection targets = new EntityCollection { EntityName = "account" };
        targets.Entities.Add(new FakeAccount { Name = "A" });
        targets.Entities.Add(new FakeAccount { Name = "B" });
        svc.Execute(new CreateMultipleRequest { Targets = targets });

        Assert.NotNull(captured);
        EntityCollection sent = (EntityCollection)captured!["Targets"];
        Assert.All(sent.Entities, e => Assert.Equal(typeof(Entity), e.GetType()));
        Assert.Equal("A", sent.Entities[0].GetAttributeValue<string>("name"));
        Assert.Equal("B", sent.Entities[1].GetAttributeValue<string>("name"));
    }

    [Fact]
    public void Create_leaves_a_base_Entity_untouched()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        Entity original = new Entity("account");
        Entity? captured = null;
        inner.Setup(s => s.Create(It.IsAny<Entity>())).Callback<Entity>(e => captured = e).Returns(Guid.NewGuid());

        svc.Create(original);

        Assert.Same(original, captured);
    }
}
