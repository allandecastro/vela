using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

// A-21..A-26: several steps per trigger (rank outside the identity), the author's description, asyncautodelete and
// the per-message image property, end to end through the diff shape, the diff pairing and the apply writes.
public class StepIdentityAndFieldsTests
{
    private const string Plugin = "Zava.Plugins.AccountPlugin";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static RegistrationItem LocalStep(string stepKey, int order = 1, string[]? filters = null, bool asyncAutoDelete = false, int mode = 0)
        => new("step", Plugin, DefinitionNormalizer.NormalizeStepDefinition(Json(new
        {
            pluginTypeName = Plugin,
            message = "Update",
            entityLogicalName = "account",
            stepKey,
            stage = 40,
            mode,
            executionOrder = order,
            filteringAttributes = filters ?? new[] { "name" },
            asyncAutoDelete,
        }), Options));

    private static RegistrationItem LiveStep(string stepKey, int order = 1, string[]? filters = null, bool asyncAutoDelete = false, int mode = 0)
    {
        string name = $"{Plugin} - {DefinitionNormalizer.BuildStepLifecycleLabel(40, mode)}: {stepKey}";
        string description = DefinitionParser.StripStepKeyMarker($"{name}\n{DefinitionParser.StepKeyMarker(stepKey)}");
        return new("step", Plugin, DefinitionNormalizer.BuildStepShape(
            Plugin, "Update", "account", stepKey, name, description, 40, mode, order, filters ?? new[] { "name" },
            Array.Empty<StepImage>(), Array.Empty<StepImage>(), true, "", "", null, 0, Options, asyncAutoDelete));
    }

    // ---- Identity -----------------------------------------------------------------------------------

    [Fact]
    public void A_rank_change_is_an_update_in_place_not_a_delete_and_create()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(State(LocalStep("k1", order: 5)), State(LiveStep("k1", order: 1)));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        RegistrationChange change = Assert.Single(diff.Changed);
        Assert.Equal(5, change.After.GetProperty("executionOrder").GetInt32());
        // Same key: apply finds the step by its key, so no pairing hint is needed.
        Assert.False(change.After.TryGetProperty(DiffEngine.LiveStepKeyField, out _));
    }

    [Fact]
    public void Two_steps_on_one_trigger_pair_with_their_own_live_steps()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State(LocalStep("k1"), LocalStep("k2", filters: new[] { "telephone1" })),
            State(LiveStep("k2", filters: new[] { "telephone1" }), LiveStep("k1")));

        Assert.False(diff.Summary.Added + diff.Summary.Removed + diff.Summary.Changed > 0);
    }

    [Fact]
    public void A_second_step_on_a_trigger_is_added_next_to_the_first()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State(LocalStep("k1"), LocalStep("k2", filters: new[] { "telephone1" })),
            State(LiveStep("k1")));

        RegistrationItem added = Assert.Single(diff.Added);
        Assert.Equal("k2", added.Definition.GetProperty("stepKey").GetString());
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void A_step_paired_under_a_changed_key_carries_the_live_key_for_apply()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(State(LocalStep("new-key")), State(LiveStep("old-key")));

        RegistrationChange change = Assert.Single(diff.Changed);
        Assert.Equal("old-key", change.After.GetProperty(DiffEngine.LiveStepKeyField).GetString());
        Assert.Equal("old-key", DefinitionParser.ParseStepDefinition(change.After).LiveStepKey);
    }

    // ---- Description (WithDescription) ----------------------------------------------------------------

    // ---- asyncautodelete ------------------------------------------------------------------------------

    [Fact]
    public void AsyncAutoDelete_is_diffed_on_async_steps_and_ignored_on_sync_steps()
    {
        Assert.Equal(1, DiffEngine.BuildDiff(State(LocalStep("k1", mode: 1, asyncAutoDelete: true)), State(LiveStep("k1", mode: 1))).Summary.Changed);
        Assert.Equal(0, DiffEngine.BuildDiff(State(LocalStep("k1", mode: 1, asyncAutoDelete: true)), State(LiveStep("k1", mode: 1, asyncAutoDelete: true))).Summary.Changed);
        Assert.False(LocalStep("k1", mode: 0, asyncAutoDelete: true).Definition.GetProperty("asyncAutoDelete").GetBoolean());
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public void UpsertStep_writes_asyncautodelete_only_for_an_async_step(int mode, bool requested, bool expected)
    {
        Guid pluginTypeId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessage"
                ? TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid()))
                : new EntityCollection(),
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };

        JsonElement def = Json(new { pluginTypeName = Plugin, message = "Update", entityLogicalName = "", stepKey = "k1", stage = 40, mode, asyncAutoDelete = requested, description = "Why" });
        DataverseRegistration.UpsertStep(svc, new Dictionary<string, Guid> { [Plugin] = pluginTypeId }, def, verbose: false);

        Entity step = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.Equal(expected, step.GetAttributeValue<bool>("asyncautodelete"));
        Assert.StartsWith("Why\n", step.GetAttributeValue<string>("description"));
    }

    // ---- Image messagepropertyname ----------------------------------------------------------------------

}
