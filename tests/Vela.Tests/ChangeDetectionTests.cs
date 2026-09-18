using System;
using Vela.Constants;
using Vela.Context;
using Vela.Querying;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class ChangeDetectionTests
{
    private static LocalPluginContext UpdateContext(TestAccount target, TestAccount? preImage)
    {
        var builder = PluginContext.For<TestAccount>()
            .OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithTarget(target);

        if (preImage != null)
        {
            builder = builder.WithPreImage(preImage);
        }

        return builder.Build();
    }

    [Fact]
    public void HasChanged_TrueWhenTargetDiffersFromPreImage()
    {
        LocalPluginContext ctx = UpdateContext(new TestAccount { Name = "New" }, new TestAccount { Name = "Old" });

        Assert.True(ctx.HasChanged<TestAccount>(x => x.Name));
    }

    [Fact]
    public void HasChanged_FalseWhenValueIsUnchanged()
    {
        LocalPluginContext ctx = UpdateContext(new TestAccount { Name = "Same" }, new TestAccount { Name = "Same" });

        Assert.False(ctx.HasChanged<TestAccount>(x => x.Name));
    }

    [Fact]
    public void HasChanged_FalseWhenAttributeNotInTarget()
    {
        // Name wasn't part of this update → not a change attributable to this operation.
        LocalPluginContext ctx = UpdateContext(new TestAccount { AccountId = Guid.NewGuid() }, new TestAccount { Name = "Old" });

        Assert.False(ctx.HasChanged<TestAccount>(x => x.Name));
    }

    [Fact]
    public void HasChanged_TrueWhenPartylistCollectionDiffers()
    {
        // Regression: partylist columns are EntityCollection. Change detection must not compare them via
        // ToString() (which returns the type name for both sides and would always report "unchanged").
        LocalPluginContext ctx = UpdateContext(
            new TestAccount { Customers = Party(Guid.NewGuid()) },
            new TestAccount { Customers = Party(Guid.NewGuid()) });

        Assert.True(ctx.HasChanged<TestAccount>(x => x.Customers));
    }

    [Fact]
    public void HasChanged_FalseWhenPartylistCollectionEqual()
    {
        Guid partyId = Guid.NewGuid();
        LocalPluginContext ctx = UpdateContext(
            new TestAccount { Customers = Party(partyId) },
            new TestAccount { Customers = Party(partyId) });

        Assert.False(ctx.HasChanged<TestAccount>(x => x.Customers));
    }

    [Fact]
    public void HasChanged_ThrowsWhenNoPreImageRegistered()
    {
        LocalPluginContext ctx = UpdateContext(new TestAccount { Name = "New" }, preImage: null);

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => ctx.HasChanged<TestAccount>(x => x.Name));
        Assert.Contains("pre-image", ex.Message);
    }

    [Fact]
    public void HasChanged_ThrowsWhenTargetIsEntityReference()
    {
        // Delete/Assign-style: the Target is an EntityReference, so there are no values to compare.
        LocalPluginContext ctx = PluginContext.For<TestAccount>()
            .OnDelete()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithInputParameter("Target", new EntityReference("account", Guid.NewGuid()))
            .WithPreImage(new TestAccount { Name = "Old" })
            .Build();

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
            () => ctx.HasChanged<TestAccount>(x => x.Name));
        Assert.Contains("EntityReference", ex.Message);
    }

    private static EntityCollection Party(Guid partyId)
    {
        Entity party = new Entity("activityparty");
        party["partyid"] = new EntityReference("contact", partyId);
        return new EntityCollection(new System.Collections.Generic.List<Entity> { party }) { EntityName = "activityparty" };
    }

    [Fact]
    public void HasAnyChanged_TrueWhenAtLeastOneChanged()
    {
        LocalPluginContext ctx = UpdateContext(new TestAccount { Name = "New" }, new TestAccount { Name = "Old" });

        Assert.True(ctx.HasAnyChanged<TestAccount>(x => x.AccountId, x => x.Name));
    }

    [Fact]
    public void HasAnyChanged_FalseWhenNoneChanged()
    {
        LocalPluginContext ctx = UpdateContext(new TestAccount { Name = "Same" }, new TestAccount { Name = "Same" });

        Assert.False(ctx.HasAnyChanged<TestAccount>(x => x.AccountId, x => x.Name));
    }

    [Fact]
    public void GetFormattedValue_ReturnsLabelOrNull()
    {
        TestAccount account = new TestAccount();
        account.FormattedValues["name"] = "Zava (display)";

        Assert.Equal("Zava (display)", account.GetFormattedValue(x => x.Name));
        Assert.Null(account.GetFormattedValue(x => x.AccountId));
    }
}
