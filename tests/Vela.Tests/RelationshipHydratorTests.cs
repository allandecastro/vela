using System;
using Microsoft.Xrm.Sdk;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

public sealed class RelationshipHydratorTests
{
    [Fact]
    public void Hydrate_fills_a_1N_collection_from_RelatedEntities()
    {
        Entity source = new Entity("account") { Id = Guid.NewGuid() };
        source["name"] = "Contoso";
        source.RelatedEntities[new Relationship("contact_customer_accounts")] = new EntityCollection(new[]
        {
            new Entity("contact") { ["firstname"] = "Ada" },
            new Entity("contact") { ["firstname"] = "Grace" },
        });

        RelAccount target = source.ToEntity<RelAccount>();
        RelationshipHydrator.Hydrate(source, target);

        Assert.Equal(2, target.Contacts.Count);
        Assert.Equal("Ada", target.Contacts[0].FirstName);
    }

    [Fact]
    public void Hydrate_fills_an_N1_parent_from_RelatedEntities()
    {
        Entity source = new Entity("account") { Id = Guid.NewGuid() };
        source.RelatedEntities[new Relationship("account_primary_contact")] = new EntityCollection(new[]
        {
            new Entity("contact") { ["firstname"] = "Grace" },
        });

        RelAccount target = source.ToEntity<RelAccount>();
        RelationshipHydrator.Hydrate(source, target);

        Assert.NotNull(target.PrimaryContact);
        Assert.Equal("Grace", target.PrimaryContact!.FirstName);
    }

    [Fact]
    public void Hydrate_leaves_navs_at_default_when_the_relationship_is_absent()
    {
        Entity source = new Entity("account") { Id = Guid.NewGuid() };

        RelAccount target = source.ToEntity<RelAccount>();
        RelationshipHydrator.Hydrate(source, target);

        Assert.Empty(target.Contacts);
        Assert.Null(target.PrimaryContact);
    }
}
