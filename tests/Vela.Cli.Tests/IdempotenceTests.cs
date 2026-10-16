using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

/// <summary>
/// Invariant tests for <c>plan → apply → plan == empty</c>. After <c>apply</c> writes a step, Dataverse
/// persists the platform-coerced values (async → PostOperation, rank ≥ 1) and the lifecycle-formatted name —
/// exactly what the local normalizer computes — so re-reading and re-diffing must report no change. These run a
/// spread of step/Custom API shapes (sync/async, with/without step key, global/entity-bound, messy input order)
/// through the apply write path and the live read of a stateful fake (<see cref="StatefulOrgService"/>). The full
/// command round trip on a real plugin package is <see cref="PlanApplyRoundTripTests"/>.
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
        // Associate scoped with .ForRelationship: the scope is runtime-only, so the live read never carries it.
        new
        {
            pluginTypeName = "Zava.Plugins.ContactPlugin", message = "Associate", entityLogicalName = "contact",
            stepKey = "contact-associate", stage = 40, mode = 0, relationships = new[] { "contact_customer_accounts", "account_contacts" },
        },
    };

    private static RegistrationItem NormalizedStep(object manifest) =>
        new RegistrationItem("step", TypeOf(manifest), DefinitionNormalizer.NormalizeStepDefinition(Json(manifest), Web));

    private static string TypeOf(object manifest) =>
        Json(manifest).GetProperty("pluginTypeName").GetString()!;

    [Fact]
    public void Plan_apply_plan_is_empty_for_every_step_shape()
    {
        // local = what `plan` computes from the declared definition; current = what the live read returns after the
        // apply write path stored it. Both go through the stateful fake, so a field the writer coerces differently
        // from the normalizer (or one the reader drops) shows up as a change.
        StatefulOrgService org = Environment(out Dictionary<string, Guid> typeIds);
        RegistrationItem[] local = StepManifests().Select(NormalizedStep).ToArray();
        foreach (RegistrationItem step in local)
        {
            DataverseRegistration.UpsertStep(org, typeIds, step.Definition, verbose: false);
        }

        RegistrationDiff diff = DiffEngine.BuildDiff(State(local), State([.. ReadBack(org)]));

        Assert.Equal(local.Length, diff.CurrentItems.Length);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
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
        StatefulOrgService org = Environment(out Dictionary<string, Guid> typeIds);
        RegistrationItem[] local = CustomApiManifests()
            .Select(m => new RegistrationItem("custom-api", TypeOf(m), DefinitionNormalizer.NormalizeCustomApiDefinition(Json(m), Web)))
            .ToArray();
        foreach (RegistrationItem api in local)
        {
            DataverseRegistration.UpsertCustomApi(org, typeIds, api.Definition, verbose: false);
        }

        RegistrationDiff diff = DiffEngine.BuildDiff(State(local), State([.. ReadBack(org)]));

        Assert.Equal(local.Length, diff.CurrentItems.Length);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    private const string Assembly = "Zava.Plugins";

    // An environment where the Zava.Plugins package is imported (its plugin types exist) and the messages used above
    // have their filters.
    private static StatefulOrgService Environment(out Dictionary<string, Guid> typeIds)
    {
        StatefulOrgService org = new();
        foreach (string message in new[] { "Create", "Update", "Delete" })
        {
            org.SeedMessage(message, "account", "contact");
        }

        org.SeedMessage("Associate"); // global: no entity filter

        org.DefinePackage("zava_Plugins", Assembly,
            "Zava.Plugins.AccountPlugin", "Zava.Plugins.ContactPlugin", "Zava.Plugins.GlobalPlugin", "Zava.Plugins.RecalcApi", "Zava.Plugins.PingApi");
        org.Create(new Microsoft.Xrm.Sdk.Entity("pluginpackage") { ["name"] = "zava_Plugins", ["version"] = "1.0.0", ["content"] = "" });
        typeIds = DataverseRegistration.GetPluginTypeIdByNameForAssemblies(org, [Assembly]);
        return org;
    }

    private static IEnumerable<RegistrationItem> ReadBack(StatefulOrgService org) =>
        DataverseRegistration.ReadAssembliesState(org, [Assembly], Web).Items;

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
