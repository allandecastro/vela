using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Tests for the execution-order (rank) collision advisory: the pure <see cref="RankCollisionDetector"/>,
/// the local/environment projections, and the end-to-end <c>BuildRankCollisionDiagnostics</c> orchestration.
/// </summary>
public sealed class RankCollisionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static RankStep Step(string message, string entity, int stage, int mode, int rank, string label)
        => new(message, entity, stage, mode, rank, label);

    // ---- RankCollisionDetector (pure) --------------------------------------------------------------

    [Fact]
    public void Detect_flags_two_steps_that_share_a_rank_on_the_same_coordinate()
    {
        IReadOnlyList<string> warnings = RankCollisionDetector.Detect(
        [
            Step("Update", "account", 40, 0, 1, "PluginA"),
            Step("Update", "account", 40, 0, 1, "PluginB"),
        ]);

        string warning = Assert.Single(warnings);
        Assert.Contains("rank 1", warning);
        Assert.Contains("Update of account", warning);
        Assert.Contains("PostOpSync", warning);
        Assert.Contains("PluginA", warning);
        Assert.Contains("PluginB", warning);
    }

    [Fact]
    public void Detect_is_silent_when_ranks_differ()
    {
        IReadOnlyList<string> warnings = RankCollisionDetector.Detect(
        [
            Step("Update", "account", 40, 0, 1, "PluginA"),
            Step("Update", "account", 40, 0, 2, "PluginB"),
        ]);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Detect_treats_different_stage_or_mode_as_different_coordinates()
    {
        IReadOnlyList<string> warnings = RankCollisionDetector.Detect(
        [
            Step("Update", "account", 40, 0, 1, "PostSync"),
            Step("Update", "account", 20, 0, 1, "PreSync"),   // different stage
            Step("Update", "account", 40, 1, 1, "PostAsync"), // different mode
        ]);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Detect_collapses_identical_steps_so_the_same_registration_twice_is_not_a_collision()
    {
        IReadOnlyList<string> warnings = RankCollisionDetector.Detect(
        [
            Step("Update", "account", 40, 0, 1, "PluginA"),
            Step("Update", "account", 40, 0, 1, "PluginA"),
        ]);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Detect_reports_the_count_for_three_or_more_colliding_steps()
    {
        IReadOnlyList<string> warnings = RankCollisionDetector.Detect(
        [
            Step("Create", "contact", 20, 0, 3, "A"),
            Step("Create", "contact", 20, 0, 3, "B"),
            Step("Create", "contact", 20, 0, 3, "C"),
        ]);

        string warning = Assert.Single(warnings);
        Assert.Contains("3 steps share rank 3", warning);
    }

    [Fact]
    public void Detect_groups_global_message_steps_with_empty_entity()
    {
        IReadOnlyList<string> warnings = RankCollisionDetector.Detect(
        [
            Step("Associate", "", 40, 0, 1, "A"),
            Step("Associate", "", 40, 0, 1, "B"),
        ]);

        Assert.Single(warnings);
    }

    // ---- ExtractLocalRankSteps ---------------------------------------------------------------------

    private static RegistrationItem LocalStep(string message, string entity, int stage, int mode, int order, bool enabled, string name)
    {
        JsonElement shape = DefinitionNormalizer.BuildStepShape(
            "Zava.Plugins.AccountPlugin", message, entity, "key", name, stage, mode, order,
            [], [], [], enabled, string.Empty, string.Empty, Json);
        return new RegistrationItem("step", "Zava.Plugins.AccountPlugin", shape);
    }

    [Fact]
    public void ExtractLocalRankSteps_projects_enabled_steps_and_skips_disabled_and_non_steps()
    {
        var apiShape = JsonSerializer.SerializeToElement(new { uniqueName = "zava_DoThing" }, Json);
        var state = new RegistrationState(
            SourceAssemblies: ["Zava.Plugins"],
            SourceAssemblyPayloads: [],
            PackagePayloads: [],
            GeneratedAtUtc: DateTime.UtcNow,
            Items:
            [
                LocalStep("Update", "account", 40, 0, 7, enabled: true, name: "Enabled"),
                LocalStep("Update", "account", 40, 0, 9, enabled: false, name: "Disabled"),
                new RegistrationItem("custom-api", "zava_DoThing", apiShape),
            ],
            Diagnostics: []);

        List<RankStep> steps = DataverseRegistration.ExtractLocalRankSteps(state);

        RankStep only = Assert.Single(steps);
        Assert.Equal("Update", only.Message);
        Assert.Equal("account", only.EntityLogicalName);
        Assert.Equal(40, only.Stage);
        Assert.Equal(7, only.Rank);
        Assert.Contains("Enabled", only.Label);
    }

    // ---- GetEnvironmentRankSteps -------------------------------------------------------------------

    private static Entity EnvStep(Guid handler, string message, string entity, int stage, int mode, int rank, string name)
    {
        var e = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", handler),
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["rank"] = rank,
            ["name"] = name,
        };
        e["msg.name"] = new AliasedValue("sdkmessage", "name", message);
        e["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", entity);
        return e;
    }

    [Fact]
    public void GetEnvironmentRankSteps_excludes_our_plugin_types_and_maps_the_rest()
    {
        Guid ours = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        var service = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(
                EnvStep(ours, "Update", "account", 40, 0, 1, "OurStep"),
                EnvStep(other, "Update", "account", 40, 0, 1, "OtherStep")),
        };

        List<RankStep> steps = DataverseRegistration.GetEnvironmentRankSteps(
            service, ["Update"], new HashSet<Guid> { ours }, ["account"], hasGlobalCoordinate: false);

        RankStep only = Assert.Single(steps);
        Assert.Equal("OtherStep", only.Label);
        Assert.Equal(1, only.Rank);
    }

    [Fact]
    public void GetEnvironmentRankSteps_returns_empty_without_querying_when_no_messages()
    {
        var service = new FakeOrgService();
        List<RankStep> steps = DataverseRegistration.GetEnvironmentRankSteps(service, [], new HashSet<Guid>(), [], hasGlobalCoordinate: false);

        Assert.Empty(steps);
        Assert.Empty(service.Queries);
    }

    // ---- GetEnvironmentRankSteps query SHAPE (row-scoping optimization) ----------------------------

    private static LinkEntity FilterLink(QueryExpression query)
        => query.LinkEntities.Single(l => l.LinkToEntityName == "sdkmessagefilter");

    [Fact]
    public void GetEnvironmentRankSteps_scopes_the_filter_link_to_touched_entities_when_no_global_coordinate()
    {
        var service = new FakeOrgService();

        DataverseRegistration.GetEnvironmentRankSteps(
            service, ["Update", "Create"], new HashSet<Guid>(), ["account", "contact"], hasGlobalCoordinate: false);

        QueryExpression query = Assert.Single(service.Queries);
        LinkEntity filterLink = FilterLink(query);

        Assert.Equal(JoinOperator.Inner, filterLink.JoinOperator);
        ConditionExpression condition = Assert.Single(
            filterLink.LinkCriteria.Conditions,
            c => c.AttributeName == "primaryobjecttypecode");
        Assert.Equal(ConditionOperator.In, condition.Operator);
        Assert.Equal(new object[] { "account", "contact" }, condition.Values);
    }

    [Fact]
    public void GetEnvironmentRankSteps_keeps_leftouter_unrestricted_when_a_global_coordinate_is_present()
    {
        var service = new FakeOrgService();

        DataverseRegistration.GetEnvironmentRankSteps(
            service, ["Update"], new HashSet<Guid>(), ["account"], hasGlobalCoordinate: true);

        QueryExpression query = Assert.Single(service.Queries);
        LinkEntity filterLink = FilterLink(query);

        Assert.Equal(JoinOperator.LeftOuter, filterLink.JoinOperator);
        Assert.DoesNotContain(filterLink.LinkCriteria.Conditions, c => c.AttributeName == "primaryobjecttypecode");
    }

    [Fact]
    public void GetEnvironmentRankSteps_keeps_leftouter_unrestricted_when_no_touched_entities()
    {
        var service = new FakeOrgService();

        DataverseRegistration.GetEnvironmentRankSteps(
            service, ["Update"], new HashSet<Guid>(), [], hasGlobalCoordinate: false);

        QueryExpression query = Assert.Single(service.Queries);
        LinkEntity filterLink = FilterLink(query);

        Assert.Equal(JoinOperator.LeftOuter, filterLink.JoinOperator);
        Assert.DoesNotContain(filterLink.LinkCriteria.Conditions, c => c.AttributeName == "primaryobjecttypecode");
    }

    // ---- BuildRankCollisionDiagnostics (orchestration) ---------------------------------------------

    [Fact]
    public void BuildRankCollisionDiagnostics_warns_when_a_local_step_collides_with_a_foreign_env_step()
    {
        Guid other = Guid.NewGuid();
        var service = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(
                EnvStep(other, "Update", "account", 40, 0, 1, "ForeignStep")),
        };

        IReadOnlyList<string> warnings = DataverseRegistration.BuildRankCollisionDiagnostics(
            service,
            [Step("Update", "account", 40, 0, 1, "LocalStep")],
            new HashSet<Guid>());

        string warning = Assert.Single(warnings);
        Assert.Contains("ForeignStep", warning);
        Assert.Contains("LocalStep", warning);
    }

    [Fact]
    public void BuildRankCollisionDiagnostics_stays_silent_for_untouched_coordinates()
    {
        Guid other = Guid.NewGuid();
        // Two foreign steps collide on a coordinate the deployment does not touch (different stage) -> silent.
        var service = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(
                EnvStep(other, "Update", "account", 20, 0, 1, "ForeignA"),
                EnvStep(other, "Update", "account", 20, 0, 1, "ForeignB")),
        };

        IReadOnlyList<string> warnings = DataverseRegistration.BuildRankCollisionDiagnostics(
            service,
            [Step("Update", "account", 40, 0, 1, "LocalStep")],
            new HashSet<Guid>());

        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildRankCollisionDiagnostics_returns_empty_when_nothing_local()
    {
        var service = new FakeOrgService();
        IReadOnlyList<string> warnings = DataverseRegistration.BuildRankCollisionDiagnostics(
            service, [], new HashSet<Guid>());

        Assert.Empty(warnings);
        Assert.Empty(service.Queries);
    }

    [Fact]
    public void BuildRankCollisionDiagnostics_scopes_the_env_read_to_touched_entities_when_all_local_steps_are_entity_bound()
    {
        var service = new FakeOrgService();

        DataverseRegistration.BuildRankCollisionDiagnostics(
            service,
            [Step("Update", "account", 40, 0, 1, "A"), Step("Create", "contact", 20, 0, 1, "B")],
            new HashSet<Guid>());

        QueryExpression query = Assert.Single(service.Queries);
        LinkEntity filterLink = FilterLink(query);

        Assert.Equal(JoinOperator.Inner, filterLink.JoinOperator);
        ConditionExpression condition = Assert.Single(
            filterLink.LinkCriteria.Conditions,
            c => c.AttributeName == "primaryobjecttypecode");
        Assert.Equal(ConditionOperator.In, condition.Operator);
        Assert.Equal(new[] { "account", "contact" }, condition.Values.Cast<string>().OrderBy(x => x));
    }

    [Fact]
    public void BuildRankCollisionDiagnostics_keeps_unrestricted_read_when_a_global_coordinate_is_present()
    {
        var service = new FakeOrgService();

        // "Associate" is a global message (no primary entity), so scoping by entity would be lossy: keep LeftOuter.
        DataverseRegistration.BuildRankCollisionDiagnostics(
            service,
            [Step("Update", "account", 40, 0, 1, "A"), Step("Associate", "", 40, 0, 1, "B")],
            new HashSet<Guid>());

        QueryExpression query = Assert.Single(service.Queries);
        LinkEntity filterLink = FilterLink(query);

        Assert.Equal(JoinOperator.LeftOuter, filterLink.JoinOperator);
        Assert.DoesNotContain(filterLink.LinkCriteria.Conditions, c => c.AttributeName == "primaryobjecttypecode");
    }
}
