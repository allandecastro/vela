using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// W1 at the builder level: the step identity (rank outside the key, several steps per trigger), asyncautodelete and
// the relationship scope.
public sealed class StepIdentityBuilderTests
{
    private static readonly Assembly Plugins = typeof(PluginMessages).Assembly;

    private static StepDefinition Def(string[] filters, int order = 1) => new()
    {
        Message = PluginMessages.Update,
        Stage = PluginStages.PostOperation,
        Mode = ExecutionMode.Synchronous,
        EntityLogicalName = "account",
        FilteringAttributes = filters,
        ExecutionOrder = order,
    };

    // ---- A-25: identity ------------------------------------------------------------------------------

    [Fact]
    public void The_rank_is_not_part_of_the_automatic_step_key()
    {
        StepDefinition[] atRank1 = { Def(new[] { "name" }), Def(new[] { "accountid" }) };
        StepDefinition[] atRank9 = { Def(new[] { "name" }, order: 9), Def(new[] { "accountid" }, order: 7) };
        StepIdentity.AssignStepKeys(atRank1, "P");
        StepIdentity.AssignStepKeys(atRank9, "P");

        Assert.Equal(atRank1.Select(d => d.StepKey), atRank9.Select(d => d.StepKey));
        Assert.NotEqual(atRank1[0].StepKey, atRank1[1].StepKey);
    }

    [Fact]
    public void Steps_on_one_trigger_with_the_same_filters_and_images_are_rejected_unless_keyed()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => StepIdentity.AssignStepKeys(new[] { Def(new[] { "name" }), Def(new[] { "name" }) }, "P"));
        Assert.Contains("WithStepKey", ex.Message);

        StepDefinition keyed = Def(new[] { "name" });
        keyed.StepKey = "explicit";
        StepIdentity.AssignStepKeys(new[] { Def(new[] { "name" }), keyed }, "P");
        Assert.Equal("explicit", keyed.StepKey);
    }

    [Fact]
    public void Two_steps_with_the_same_explicit_key_are_rejected()
    {
        StepDefinition a = Def(new[] { "name" });
        StepDefinition b = Def(new[] { "accountid" });
        b.Message = PluginMessages.Create;
        a.StepKey = b.StepKey = "same";
        Assert.Contains("unique", Assert.Throws<InvalidOperationException>(() => StepIdentity.AssignStepKeys(new[] { a, b }, "P")).Message);
    }

    // ---- A-23 / A-26 / A-24: fields ---------------------------------------------------------------------

    [Fact]
    public void Async_auto_delete_and_relationship_scope_reach_the_definition()
    {
        StepBuilder<TestAccount> asyncStep = new();
        ((IStepTrigger<TestAccount>)asyncStep).OnUpdate().PostOperation().Async(deleteJobOnSuccess: true)
            .FilterOn(a => a.Name).Handle(_ => { });
        StepDefinition built = asyncStep.Build();
        Assert.Equal(ExecutionMode.Asynchronous, built.Mode);
        Assert.True(built.AsyncAutoDelete);

        StepBuilder<TestAccount> plainAsync = new();
        ((IStepTrigger<TestAccount>)plainAsync).OnUpdate().PostOperation().Async().FilterOn(a => a.Name).Handle(_ => { });
        Assert.False(plainAsync.Build().AsyncAutoDelete);

        StepBuilder<TestAccount> associate = new();
        ((IStepTrigger<TestAccount>)associate).OnAssociate().PostOperation().Sync().ForRelationship("account_contacts", "account_partners").Handle(_ => { });
        Assert.Equal(new[] { "account_contacts", "account_partners" }, associate.Build().Relationships);
    }

    [Fact]
    public void ForRelationship_requires_names_and_is_set_once()
    {
        StepBuilder<TestAccount> empty = new();
        Assert.Throws<ArgumentException>(() => ((IStepTrigger<TestAccount>)empty).OnAssociate().PostOperation().Sync().ForRelationship(" "));

        StepBuilder<TestAccount> twice = new();
        IAssociatePostOperationConfig<TestAccount> config = ((IStepTrigger<TestAccount>)twice).OnAssociate().PostOperation().Sync().ForRelationship("a");
        Assert.Throws<InvalidOperationException>(() => config.ForRelationship("b"));
    }

}
