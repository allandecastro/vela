using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

public sealed class ApplyExecutedJsonTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static RegistrationDiff Diff(RegistrationItem[] added, RegistrationChange[] changed, RegistrationItem[] removed)
        => new RegistrationDiff([], [], added, removed, changed, [], []);

    [Fact]
    public void Apply_execute_json_reports_executed_counts_distinct_from_the_remaining_summary()
    {
        RegistrationDiff preApply = Diff(
            added: [Step(new { }), CustomApi(new { })],
            changed: [new RegistrationChange("step", "Zava.Plugins.C", Json(new { }), Json(new { }))],
            removed: [Step(new { }, "Zava.Plugins.D"), Step(new { }, "Zava.Plugins.E")]);

        // What a successful apply serializes: Summary from the POST-apply diff (empty = nothing remains), Executed
        // from the PRE-apply diff (what was actually done), totals plus a per-kind breakdown.
        PlanPreview result = OutputWriter.BuildPlanPreview("apply-execute", "env", Diff([], [], []), [], []) with
        {
            Executed = ExecutedCounts.FromDiff(preApply, packageRepublished: true),
        };

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result, Options));
        JsonElement root = doc.RootElement;

        JsonElement summary = root.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("added").GetInt32());
        Assert.Equal(0, summary.GetProperty("removed").GetInt32());
        Assert.Equal(0, summary.GetProperty("changed").GetInt32());

        JsonElement executed = root.GetProperty("executed");
        Assert.Equal(2, executed.GetProperty("created").GetInt32());
        Assert.Equal(1, executed.GetProperty("updated").GetInt32());
        Assert.Equal(2, executed.GetProperty("deleted").GetInt32());
        Assert.True(executed.GetProperty("packageRepublished").GetBoolean());

        // Per-kind breakdown: steps 1 created / 1 updated / 2 deleted; custom-api 1 created.
        JsonElement[] byKind = executed.GetProperty("byKind").EnumerateArray().ToArray();
        JsonElement step = byKind.Single(k => k.GetProperty("kind").GetString() == "step");
        Assert.Equal(1, step.GetProperty("created").GetInt32());
        Assert.Equal(1, step.GetProperty("updated").GetInt32());
        Assert.Equal(2, step.GetProperty("deleted").GetInt32());
        JsonElement api = byKind.Single(k => k.GetProperty("kind").GetString() == "custom-api");
        Assert.Equal(1, api.GetProperty("created").GetInt32());
        Assert.Equal(0, api.GetProperty("deleted").GetInt32());
    }

    [Fact]
    public void Plan_json_has_no_executed_block()
    {
        PlanPreview plan = OutputWriter.BuildPlanPreview("plan", "env", Diff([], [], []), [], []);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(plan, Options));

        Assert.False(doc.RootElement.TryGetProperty("executed", out _));
    }
}
