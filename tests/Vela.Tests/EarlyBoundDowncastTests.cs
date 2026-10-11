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

    [EntityLogicalName("contact")]
    private sealed class FakeContact : Entity
    {
        public FakeContact()
            : base("contact")
        {
        }
    }

    [EntityLogicalName("activityparty")]
    private sealed class FakeParty : Entity
    {
        public FakeParty()
            : base("activityparty")
        {
        }
    }

    [Fact]
    public void Create_downcasts_early_bound_entities_nested_in_related_entities()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        Entity? captured = null;
        inner.Setup(s => s.Create(It.IsAny<Entity>())).Callback<Entity>(e => captured = e).Returns(Guid.NewGuid());

        // A base root carrying early-bound children (deep insert) must still be sanitized.
        Entity account = new Entity("account");
        FakeContact child = new FakeContact();
        child["firstname"] = "Ada";
        account.RelatedEntities[new Relationship("contact_customer_accounts")] = new EntityCollection { Entities = { child } };

        svc.Create(account);

        Entity sentChild = captured!.RelatedEntities[new Relationship("contact_customer_accounts")].Entities[0];
        Assert.Equal(typeof(Entity), sentChild.GetType());
        Assert.Equal("Ada", sentChild.GetAttributeValue<string>("firstname"));
        // The caller's graph is untouched.
        Assert.Same(child, account.RelatedEntities[new Relationship("contact_customer_accounts")].Entities[0]);
    }

    [Fact]
    public void Create_downcasts_early_bound_entities_in_an_entity_collection_attribute()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        Entity? captured = null;
        inner.Setup(s => s.Create(It.IsAny<Entity>())).Callback<Entity>(e => captured = e).Returns(Guid.NewGuid());

        FakeParty party = new FakeParty();
        party["partyid"] = new EntityReference("contact", Guid.NewGuid());
        EntityCollection to = new EntityCollection { Entities = { party } };
        Entity email = new Entity("email") { ["to"] = to, ["subject"] = "Hi" };

        svc.Create(email);

        EntityCollection sentTo = captured!.GetAttributeValue<EntityCollection>("to");
        Assert.Equal(typeof(Entity), sentTo.Entities[0].GetType());
        Assert.Equal("Hi", captured.GetAttributeValue<string>("subject"));
        Assert.Same(to, email["to"]);
        Assert.Same(party, to.Entities[0]);
    }

    [Fact]
    public void Execute_leaves_the_callers_request_untouched_and_keeps_its_type()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        OrganizationRequest? captured = null;
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => captured = r)
            .Returns(new OrganizationResponse());

        FakeAccount account = new FakeAccount { Name = "A" };
        UpsertRequest request = new UpsertRequest { Target = account };
        svc.Execute(request);

        Assert.IsType<UpsertRequest>(captured);
        Assert.NotSame(request, captured);
        Assert.Equal(typeof(Entity), ((UpsertRequest)captured!).Target.GetType());
        Assert.Same(account, request.Target);
    }

    [Fact]
    public void Execute_passes_a_request_without_early_bound_values_through()
    {
        (TracingOrganizationService svc, Mock<IOrganizationService> inner) = Build();
        OrganizationRequest? captured = null;
        inner.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => captured = r)
            .Returns(new OrganizationResponse());

        UpsertRequest request = new UpsertRequest { Target = new Entity("account") };
        svc.Execute(request);

        Assert.Same(request, captured);
    }
}
