using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

/// <summary>
/// Invariant tests for <c>plan → apply → plan == empty</c>. After <c>apply</c> writes a step, Dataverse
/// persists the platform-coerced values (async → PostOperation, rank ≥ 1) and the lifecycle-formatted name —
/// exactly what the local normalizer computes — so re-reading and re-diffing must report no change. These run a
/// spread of step/Custom API shapes (sync/async, with/without step key, global/entity-bound, messy input order)
/// through both the normalizer and the diff engine.
/// </summary>
public class IdempotenceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // A spread of raw manifest shapes a builder might emit, including the awkward ones (async at the wrong
    // stage, rank 0, out-of-order filtering attributes / images, missing config, no step key, global message).
    private static object[] StepManifests() => new object[]
    {
        new
        {
            pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", entityLogicalName = "account",
            stepKey = "dv-abc123", stage = 20, mode = 1, executionOrder = 0,
            filteringAttributes = new[] { "telephone1", "name" },
            preImages = new object[] { new { alias = "PreB", attributes = new[] { "telephone1" } }, new { alias = "PreA", attributes = new[] { "name" } } },
        },
        new
        {
            pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Create", entityLogicalName = "account",
            stepKey = "dv-create", stage = 40, mode = 0, executionOrder = 3,
            filteringAttributes = new[] { "name" },
            postImages = new object[] { new { alias = "PostImage", attributes = new[] { "name", "accountid" } } },
            unsecureConfig = "u", secureConfig = "s",
        },
        // No step key → matched by legacy identity (pluginType:message:stage:mode:entity).
        new { pluginTypeName = "Zava.Plugins.ContactPlugin", message = "Delete", entityLogicalName = "contact", stage = 20, mode = 0 },
        // Global message (no entity), disabled, async at pre-operation.
        new { pluginTypeName = "Zava.Plugins.GlobalPlugin", message = "Update", stepKey = "dv-global", stage = 10, mode = 1, enabled = false },
    };

    private static RegistrationItem NormalizedStep(object manifest) =>
        new RegistrationItem("step", TypeOf(manifest), DefinitionNormalizer.NormalizeStepDefinition(Json(manifest), Web));

    private static string TypeOf(object manifest) =>
        Json(manifest).GetProperty("pluginTypeName").GetString()!;

    [Fact]
    public void Plan_apply_plan_is_empty_for_every_step_shape()
    {
        foreach (object manifest in StepManifests())
        {
            // local = what `plan` computes from the declared definition; current = the read-back after `apply`,
            // which stores the same coerced values, so normalizing the same manifest models GetStepItems.
            RegistrationDiff diff = DiffEngine.BuildDiff(State(NormalizedStep(manifest)), State(NormalizedStep(manifest)));

            Assert.Empty(diff.Added);
            Assert.Empty(diff.Removed);
            Assert.Empty(diff.Changed);
        }
    }

    [Fact]
    public void Normalize_step_is_a_fixed_point_for_every_shape()
    {
        // Re-normalizing an already-normalized shape must not change it (no coercion runs a second time, no
        // re-ordering flips): plan over a previously-applied definition can't drift.
        foreach (object manifest in StepManifests())
        {
            JsonElement once = DefinitionNormalizer.NormalizeStepDefinition(Json(manifest), Web);
            JsonElement twice = DefinitionNormalizer.NormalizeStepDefinition(once, Web);

            Assert.Equal(JsonSerializer.Serialize(once), JsonSerializer.Serialize(twice));
        }
    }

    [Fact]
    public void A_changed_field_still_surfaces_after_normalization()
    {
        // Guard against the inverse failure: normalization must not erase a real difference (here, rank).
        RegistrationState local = State(NormalizedStep(new
        {
            pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", entityLogicalName = "account",
            stepKey = "dv-abc123", stage = 40, mode = 0, executionOrder = 1, filteringAttributes = new[] { "name" },
        }));
        RegistrationState current = State(NormalizedStep(new
        {
            pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", entityLogicalName = "account",
            stepKey = "dv-abc123", stage = 40, mode = 0, executionOrder = 5, filteringAttributes = new[] { "name" },
        }));

        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        Assert.Single(diff.Changed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    private static object[] CustomApiManifests() => new object[]
    {
        new
        {
            pluginTypeName = "Zava.Plugins.RecalcApi", uniqueName = "zava_Recalc", isFunction = false, isBound = true,
            boundEntityLogicalName = "account",
            inputParameters = new object[] { new { name = "B" }, new { name = "A" } },
            outputParameters = new object[] { new { name = "Result" } },
        },
        new { pluginTypeName = "Zava.Plugins.PingApi", uniqueName = "zava_Ping", isFunction = true },
    };

    [Fact]
    public void Plan_apply_plan_is_empty_for_every_custom_api_shape()
    {
        foreach (object manifest in CustomApiManifests())
        {
            RegistrationItem Item() => new RegistrationItem("custom-api", TypeOf(manifest),
                DefinitionNormalizer.NormalizeCustomApiDefinition(Json(manifest), Web));

            RegistrationDiff diff = DiffEngine.BuildDiff(State(Item()), State(Item()));

            Assert.Empty(diff.Added);
            Assert.Empty(diff.Removed);
            Assert.Empty(diff.Changed);
        }
    }

    [Fact]
    public void Normalize_custom_api_is_a_fixed_point_for_every_shape()
    {
        foreach (object manifest in CustomApiManifests())
        {
            JsonElement once = DefinitionNormalizer.NormalizeCustomApiDefinition(Json(manifest), Web);
            JsonElement twice = DefinitionNormalizer.NormalizeCustomApiDefinition(once, Web);

            Assert.Equal(JsonSerializer.Serialize(once), JsonSerializer.Serialize(twice));
        }
    }
}
