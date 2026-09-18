using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Constants;
using Vela.Context;
using Vela.Querying;
using Vela.Steps;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Drives AttributeValueComparer (through HasChanged) over every Normalize branch — option set, lookup, money,
// partylist collections — and covers the EntityAttributeExtensions logical-name fallback / non-property guard.
public sealed class ChangeComparerAndAttributeCoverageTests
{
    [EntityLogicalName("rich")]
    private sealed class RichEntity : Entity
    {
        public RichEntity() : base("rich") { }

        [AttributeLogicalName("statuscode")]
        public OptionSetValue Status { get => GetAttributeValue<OptionSetValue>("statuscode"); set => SetAttributeValue("statuscode", value); }

        [AttributeLogicalName("amount")]
        public Money Amount { get => GetAttributeValue<Money>("amount"); set => SetAttributeValue("amount", value); }

        [AttributeLogicalName("ownerid")]
        public EntityReference Owner { get => GetAttributeValue<EntityReference>("ownerid"); set => SetAttributeValue("ownerid", value); }

        [AttributeLogicalName("parties")]
        public EntityCollection Parties { get => GetAttributeValue<EntityCollection>("parties"); set => SetAttributeValue("parties", value); }

        // No [AttributeLogicalName] -> exercises the property-name fallback.
        public string PlainProp { get => GetAttributeValue<string>("plainprop"); set => SetAttributeValue("plainprop", value); }
    }

    private static LocalPluginContext Update(RichEntity target, RichEntity preImage)
        => PluginContext.For<RichEntity>()
            .OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
            .WithTarget(target).WithPreImage(preImage).Build();

    private static EntityCollection Party(params Guid[] partyIds)
    {
        EntityCollection collection = new EntityCollection();
        foreach (Guid id in partyIds)
        {
            collection.Entities.Add(new Entity("activityparty") { ["partyid"] = new EntityReference("contact", id) });
        }

        return collection;
    }

    [Fact]
    public void OptionSet_lookup_and_money_changes_are_detected_by_value()
    {
        Guid owner1 = Guid.NewGuid(), owner2 = Guid.NewGuid();
        RichEntity target = new RichEntity { Status = new OptionSetValue(1), Amount = new Money(10m), Owner = new EntityReference("systemuser", owner1) };
        RichEntity pre = new RichEntity { Status = new OptionSetValue(2), Amount = new Money(20m), Owner = new EntityReference("systemuser", owner2) };

        LocalPluginContext ctx = Update(target, pre);
        Assert.True(ctx.HasChanged<RichEntity>(x => x.Status));
        Assert.True(ctx.HasChanged<RichEntity>(x => x.Amount));
        Assert.True(ctx.HasChanged<RichEntity>(x => x.Owner));
    }

    [Fact]
    public void Lookup_table_change_with_same_id_is_a_change()
    {
        Guid id = Guid.NewGuid();
        // ownerid is polymorphic (systemuser vs team): the same id in a different table must count as a change,
        // even though a lookup normalizes to its id alone.
        RichEntity target = new RichEntity { Owner = new EntityReference("team", id) };
        RichEntity pre = new RichEntity { Owner = new EntityReference("systemuser", id) };

        Assert.True(Update(target, pre).HasChanged<RichEntity>(x => x.Owner));
    }

    [Fact]
    public void Equal_normalized_values_are_not_changes()
    {
        Guid owner = Guid.NewGuid();
        RichEntity target = new RichEntity { Status = new OptionSetValue(5), Owner = new EntityReference("systemuser", owner) };
        RichEntity pre = new RichEntity { Status = new OptionSetValue(5), Owner = new EntityReference("systemuser", owner) };

        LocalPluginContext ctx = Update(target, pre);
        Assert.False(ctx.HasChanged<RichEntity>(x => x.Status));
        Assert.False(ctx.HasChanged<RichEntity>(x => x.Owner));
    }

    [Fact]
    public void Partylist_collections_compare_by_membership()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        // Same members, different order -> not changed.
        Assert.False(Update(new RichEntity { Parties = Party(a, b) }, new RichEntity { Parties = Party(b, a) })
            .HasChanged<RichEntity>(x => x.Parties));

        // Different count -> changed.
        Assert.True(Update(new RichEntity { Parties = Party(a, b) }, new RichEntity { Parties = Party(a) })
            .HasChanged<RichEntity>(x => x.Parties));

        // Different members (same count) -> changed.
        Assert.True(Update(new RichEntity { Parties = Party(a) }, new RichEntity { Parties = Party(b) })
            .HasChanged<RichEntity>(x => x.Parties));
    }

    [Fact]
    public void A_collection_against_a_missing_image_value_is_a_change()
    {
        // Target has the partylist; the pre-image does not -> one side null -> changed.
        RichEntity target = new RichEntity { Parties = Party(Guid.NewGuid()) };
        RichEntity pre = new RichEntity(); // no parties attribute
        Assert.True(Update(target, pre).HasChanged<RichEntity>(x => x.Parties));
    }

    [Fact]
    public void Contains_uses_the_property_name_when_no_logical_name_attribute_is_present()
    {
        RichEntity present = new RichEntity { PlainProp = "v" };
        Assert.True(EntityAttributeExtensions.Contains(present, x => x.PlainProp)); // maps to "plainprop"
        Assert.False(EntityAttributeExtensions.Contains(new RichEntity(), x => x.PlainProp));

        // A non-property expression is rejected.
        Assert.Throws<ArgumentException>(() => EntityAttributeExtensions.Contains(present, x => "literal"));
    }
}
