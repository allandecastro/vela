using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

public class DiffEngineTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static RegistrationItem NormalizedStep(object manifest) =>
        new RegistrationItem("step", "Zava.Plugins.AccountPlugin",
            DefinitionNormalizer.NormalizeStepDefinition(Json(manifest), Web));

    // The diff sorts every output collection by (Kind, Type) ordinal so plan/apply produce a stable shape.
    // Asserts that ordering against deliberately-scrambled input — a discriminating case (≥2 kinds, and a kind
    // with ≥2 types) so reversing either sort key would reorder the result.
    private static void AssertOrderedByKindThenType(System.Collections.Generic.IEnumerable<(string Kind, string Type)> sequence)
    {
        var actual = sequence.ToList();
        var expected = actual
            .OrderBy(x => x.Kind, System.StringComparer.Ordinal)
            .ThenBy(x => x.Type, System.StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, actual);
        Assert.True(actual.Select(x => x.Kind).Distinct().Count() >= 2, "case must span >=2 kinds to pin OrderBy");
        Assert.Contains(actual.GroupBy(x => x.Kind), g => g.Select(x => x.Type).Distinct().Count() >= 2);
    }

    [Fact]
    public void BuildDiff_throws_on_duplicate_local_step_key()   // A6
    {
        // Same plugin type + same step key => same step key identity (a plugin declaring two indistinguishable steps).
        RegistrationState local = State(
            Step(new { pluginTypeName = "Step.A", stepKey = "dup", executionOrder = 0 }, "Step.A"),
            Step(new { pluginTypeName = "Step.A", stepKey = "dup", executionOrder = 9 }, "Step.A"));
        RegistrationState current = State();

        var ex = Assert.Throws<System.InvalidOperationException>(() => DiffEngine.BuildDiff(local, current));
        Assert.Contains("Duplicate local step key", ex.Message);
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void BuildDiff_throws_on_duplicate_local_custom_api_identity()   // A5
    {
        RegistrationState local = State(
            CustomApi(new { uniqueName = "zava_Dup" }, "Api.One"),
            CustomApi(new { uniqueName = "zava_Dup" }, "Api.Two"));
        RegistrationState current = State();

        var ex = Assert.Throws<System.InvalidOperationException>(() => DiffEngine.BuildDiff(local, current));
        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("zava_Dup", ex.Message);
    }

    [Fact]
    public void Diff_collections_are_ordered_by_kind_then_type()
    {
        // Changed pairs: same identity (stepKey / uniqueName), different definition — spanning two kinds and,
        // within "step", two types, so both the OrderBy and the ThenBy of the Changed sort are exercised.
        RegistrationItem chApiLocal = CustomApi(new { uniqueName = "zava_Z", isFunction = false }, "Api.Z");
        RegistrationItem chApiCurr = CustomApi(new { uniqueName = "zava_Z", isFunction = true }, "Api.Z");
        RegistrationItem chStepALocal = Step(new { pluginTypeName = "Step.A", stepKey = "k2", executionOrder = 0 }, "Step.A");
        RegistrationItem chStepACurr = Step(new { pluginTypeName = "Step.A", stepKey = "k2", executionOrder = 9 }, "Step.A");
        RegistrationItem chStepCLocal = Step(new { pluginTypeName = "Step.C", stepKey = "k5", executionOrder = 0 }, "Step.C");
        RegistrationItem chStepCCurr = Step(new { pluginTypeName = "Step.C", stepKey = "k5", executionOrder = 9 }, "Step.C");

        // Only-in-local => Added; only-in-current => Removed. Same two-kind / same-kind-two-types shape.
        RegistrationItem addApi = CustomApi(new { uniqueName = "zava_A" }, "Api.A");
        RegistrationItem addStepB = Step(new { pluginTypeName = "Step.B", stepKey = "k1" }, "Step.B");
        RegistrationItem addStepD = Step(new { pluginTypeName = "Step.D", stepKey = "k6" }, "Step.D");
        RegistrationItem remApi = CustomApi(new { uniqueName = "zava_R" }, "Api.R");
        RegistrationItem remStepE = Step(new { pluginTypeName = "Step.E", stepKey = "k3" }, "Step.E");
        RegistrationItem remStepG = Step(new { pluginTypeName = "Step.G", stepKey = "k7" }, "Step.G");

        // Insert in scrambled order so a stable result can only come from the sort.
        RegistrationState local = State(addStepD, chStepCLocal, addApi, chApiLocal, addStepB, chStepALocal);
        RegistrationState current = State(remStepG, chStepCCurr, remApi, chApiCurr, remStepE, chStepACurr);

        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        Assert.Equal(3, diff.Added.Length);
        Assert.Equal(3, diff.Removed.Length);
        Assert.Equal(3, diff.Changed.Length);

        AssertOrderedByKindThenType(diff.Added.Select(x => (x.Kind, x.Type)));
        AssertOrderedByKindThenType(diff.Removed.Select(x => (x.Kind, x.Type)));
        AssertOrderedByKindThenType(diff.Changed.Select(x => (x.Kind, x.Type)));
        AssertOrderedByKindThenType(diff.LocalItems.Select(x => (x.Kind, x.Type)));
        AssertOrderedByKindThenType(diff.CurrentItems.Select(x => (x.Kind, x.Type)));
    }

    [Fact]
    public void Async_step_declared_at_preoperation_does_not_churn_against_live_postoperation()
    {
        // Regression for the plan/apply idempotence bug: an async step is DECLARED at PreOperation (stage 20)
        // with executionOrder 0, but Dataverse persists it at PostOperation (stage 40) with rank 1. The local
        // shape must apply the same coercions so the diff is empty — otherwise plan reports a permanent change
        // and apply re-writes the step on every run.
        RegistrationState local = State(NormalizedStep(new
        {
            pluginTypeName = "Zava.Plugins.AccountPlugin",
            message = "Update",
            entityLogicalName = "account",
            stepKey = "dv-abc123",
            stage = 20,
            mode = 1,
            executionOrder = 0,
            filteringAttributes = new[] { "name" },
        }));

        // Models the read-back (GetStepItems) values: stage already 40, rank already 1.
        RegistrationState current = State(NormalizedStep(new
        {
            pluginTypeName = "Zava.Plugins.AccountPlugin",
            message = "Update",
            entityLogicalName = "account",
            stepKey = "dv-abc123",
            stage = 40,
            mode = 1,
            executionOrder = 1,
            filteringAttributes = new[] { "name" },
        }));

        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void CustomApi_only_in_local_is_added()
    {
        RegistrationState local = State(CustomApi(new { uniqueName = "zava_DoThing", isFunction = false }));
        RegistrationState current = State();

        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        Assert.Single(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
        Assert.Equal("custom-api", diff.Added[0].Kind);
    }

    [Fact]
    public void CustomApi_only_in_current_is_removed()
    {
        RegistrationState local = State();
        RegistrationState current = State(CustomApi(new { uniqueName = "zava_DoThing" }));

        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        Assert.Empty(diff.Added);
        Assert.Single(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void CustomApi_with_same_name_but_different_definition_is_changed()
    {
        RegistrationState local = State(CustomApi(new { uniqueName = "zava_DoThing", isFunction = true }));
        RegistrationState current = State(CustomApi(new { uniqueName = "zava_DoThing", isFunction = false }));

        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Single(diff.Changed);
        Assert.Equal("zava_DoThing", JsonReaderTestHelper.UniqueName(diff.Changed[0]));
    }

    [Fact]
    public void CustomApi_identical_definition_produces_no_change()
    {
        var def = new { uniqueName = "zava_DoThing", isFunction = false };
        RegistrationDiff diff = DiffEngine.BuildDiff(State(CustomApi(def)), State(CustomApi(def)));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Step_matched_by_stepkey_with_same_definition_is_unchanged()
    {
        var def = new { pluginTypeName = "Zava.AccountPlugin", stepKey = "dv-abc123", message = "Update", stage = 40, mode = 0 };
        RegistrationDiff diff = DiffEngine.BuildDiff(State(Step(def)), State(Step(def)));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Step_matched_by_stepkey_with_different_definition_is_changed()
    {
        var local = new { pluginTypeName = "Zava.AccountPlugin", stepKey = "dv-abc123", message = "Update", stage = 40, mode = 0 };
        var current = new { pluginTypeName = "Zava.AccountPlugin", stepKey = "dv-abc123", message = "Update", stage = 20, mode = 0 };

        RegistrationDiff diff = DiffEngine.BuildDiff(State(Step(local)), State(Step(current)));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Single(diff.Changed);
    }

    [Fact]
    public void Step_without_stepkey_is_matched_by_legacy_identity()
    {
        // Same plugin type / message / stage / mode / entity but different filtering attributes => changed, not add+remove.
        var local = new { pluginTypeName = "Zava.AccountPlugin", message = "Update", stage = 40, mode = 0, entityLogicalName = "account", filteringAttributes = new[] { "name" } };
        var current = new { pluginTypeName = "Zava.AccountPlugin", message = "Update", stage = 40, mode = 0, entityLogicalName = "account", filteringAttributes = new[] { "name", "telephone1" } };

        RegistrationDiff diff = DiffEngine.BuildDiff(State(Step(local)), State(Step(current)));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Single(diff.Changed);
    }

    [Fact]
    public void Step_with_no_match_is_added()
    {
        var local = new { pluginTypeName = "Zava.AccountPlugin", stepKey = "dv-new", message = "Create", stage = 20, mode = 0 };
        RegistrationDiff diff = DiffEngine.BuildDiff(State(Step(local)), State());

        Assert.Single(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Added_items_are_ordered_by_kind_then_type()
    {
        RegistrationState local = State(
            Step(new { pluginTypeName = "Z.Plugin", stepKey = "z" }, type: "Z.Plugin"),
            CustomApi(new { uniqueName = "a_api" }, type: "A.Api"),
            Step(new { pluginTypeName = "A.Plugin", stepKey = "a" }, type: "A.Plugin"));

        RegistrationDiff diff = DiffEngine.BuildDiff(local, State());

        // Kind ordinal: "custom-api" < "step"; within step, type "A.Plugin" < "Z.Plugin".
        Assert.Equal(new[] { "custom-api", "step", "step" }, diff.Added.Select(x => x.Kind).ToArray());
        Assert.Equal(new[] { "A.Api", "A.Plugin", "Z.Plugin" }, diff.Added.Select(x => x.Type).ToArray());
    }

    [Fact]
    public void Empty_states_produce_empty_diff()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(State(), State());

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
        Assert.Equal(0, diff.Summary.Added);
        Assert.Equal(0, diff.Summary.Removed);
        Assert.Equal(0, diff.Summary.Changed);
    }

    // ---- #305: casing must not churn (diff folds message / uniqueName / name; apply keeps authored casing) ----

    private static RegistrationItem NormalizedCustomApi(object manifest, string type = "Zava.Plugins.MyApi") =>
        new RegistrationItem("custom-api", type,
            DefinitionNormalizer.NormalizeCustomApiDefinition(Json(manifest), Web));

    [Fact]
    public void BuildDiff_does_not_report_a_step_changed_on_message_casing_drift_only()
    {
        RegistrationItem local = NormalizedStep(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Create", stage = 40, mode = 0, entityLogicalName = "account" });
        RegistrationItem live = NormalizedStep(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "create", stage = 40, mode = 0, entityLogicalName = "account" });

        RegistrationDiff diff = DiffEngine.BuildDiff(State(local), State(live));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void BuildDiff_still_reports_a_step_changed_on_a_non_casing_difference()
    {
        RegistrationItem local = NormalizedStep(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Create", stage = 40, mode = 0, entityLogicalName = "account", stepKey = "k1", executionOrder = 5 });
        RegistrationItem live = NormalizedStep(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Create", stage = 40, mode = 0, entityLogicalName = "account", stepKey = "k1", executionOrder = 9 });

        RegistrationDiff diff = DiffEngine.BuildDiff(State(local), State(live));

        Assert.Single(diff.Changed);
    }

    [Fact]
    public void BuildDiff_does_not_report_a_custom_api_changed_on_unique_name_casing_drift_only()
    {
        RegistrationItem local = NormalizedCustomApi(new { uniqueName = "zava_DoThing", isFunction = false });
        RegistrationItem live = NormalizedCustomApi(new { uniqueName = "zava_dothing", isFunction = false });

        RegistrationDiff diff = DiffEngine.BuildDiff(State(local), State(live));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void A_real_custom_api_change_keeps_the_authored_unique_name_casing_for_apply_to_write()
    {
        RegistrationItem local = NormalizedCustomApi(new { uniqueName = "zava_DoThing", isFunction = true });
        RegistrationItem live = NormalizedCustomApi(new { uniqueName = "zava_dothing", isFunction = false });

        RegistrationDiff diff = DiffEngine.BuildDiff(State(local), State(live));

        RegistrationChange change = Assert.Single(diff.Changed);
        // The stored payload apply writes keeps the authored casing (only the comparison folded case).
        Assert.Equal("zava_DoThing", JsonReaderTestHelper.UniqueName(change));
    }
}

internal static class JsonReaderTestHelper
{
    public static string? UniqueName(RegistrationChange change)
        => change.After.TryGetProperty("uniqueName", out var v) ? v.GetString() : null;
}
