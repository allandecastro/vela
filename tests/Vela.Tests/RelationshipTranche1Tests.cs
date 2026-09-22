using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

public sealed class RelationshipTranche1Tests
{
    [Fact]
    public void GetAttributeLogicalName_throws_on_a_1N_relationship_nav()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ExpressionHelper.GetAttributeLogicalName<RelAccount>(a => a.Contacts));
        Assert.Contains("relationship", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAttributeLogicalName_throws_on_an_N1_relationship_nav()
    {
        Assert.Throws<ArgumentException>(
            () => ExpressionHelper.GetAttributeLogicalName<RelAccount>(a => a.PrimaryContact));
    }

    [Fact]
    public void GetAttributeName_throws_on_a_relationship_nav()
    {
        // The convention-fallback path (used by FilterOn/WithPreImage/etc.) must also reject a nav property rather
        // than silently building the bogus column "contacts".
        Assert.Throws<ArgumentException>(
            () => ExpressionHelper.GetAttributeName<RelAccount>(a => a.Contacts));
    }

    [Fact]
    public void Column_selectors_still_resolve_real_columns()
    {
        Assert.Equal("name", ExpressionHelper.GetAttributeLogicalName<RelAccount>(a => a.Name));
        Assert.Equal("primarycontactid", ExpressionHelper.GetAttributeLogicalName<RelAccount>(a => a.PrimaryContactId));
    }

    [Fact]
    public void RelationshipProjector_moves_a_populated_1N_collection_into_RelatedEntities()
    {
        RelAccount account = new RelAccount { Name = "Contoso" };
        account.Contacts.Add(new RelContact { FirstName = "Ada" });
        account.Contacts.Add(new RelContact { FirstName = "Grace" });

        RelationshipProjector.PopulateRelatedEntities(account);

        EntityCollection related = account.RelatedEntities[new Relationship("contact_customer_accounts")];
        Assert.Equal(2, related.Entities.Count);
        Assert.Equal("Ada", related.Entities[0].GetAttributeValue<string>("firstname"));
    }

    [Fact]
    public void RelationshipProjector_skips_an_empty_collection()
    {
        RelAccount account = new RelAccount { Name = "Empty" };

        RelationshipProjector.PopulateRelatedEntities(account);

        Assert.False(account.RelatedEntities.Contains(new Relationship("contact_customer_accounts")));
    }

    [Fact]
    public void RelationshipProjector_does_not_overwrite_an_explicit_RelatedEntities_assignment()
    {
        RelAccount account = new RelAccount { Name = "Manual" };
        EntityCollection manual = new EntityCollection(new[] { (Entity)new RelContact { FirstName = "Manual" } });
        account.RelatedEntities[new Relationship("contact_customer_accounts")] = manual;
        account.Contacts.Add(new RelContact { FirstName = "FromNav" });

        RelationshipProjector.PopulateRelatedEntities(account);

        Assert.Same(manual, account.RelatedEntities[new Relationship("contact_customer_accounts")]);
    }
}
