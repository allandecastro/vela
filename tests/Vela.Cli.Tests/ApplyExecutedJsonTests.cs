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
            added: [Step(new { }), Step(new { }, "Zava.Plugins.B")],
            changed: [new RegistrationChange("step", "Zava.Plugins.C", Json(new { }), Json(new { }))],
            removed: [Step(new { }, "Zava.Plugins.D"), Step(new { }, "Zava.Plugins.E"), Step(new { }, "Zava.Plugins.F")]);

        // What a successful apply serializes: Summary from the POST-apply diff (empty = nothing remains), Executed
        // from the PRE-apply diff (what was actually done).
        PlanPreview result = OutputWriter.BuildPlanPreview("apply-execute", "env", Diff([], [], []), [], []) with
        {
            Executed = new ExecutedCounts(preApply.Added.Length, preApply.Changed.Length, preApply.Removed.Length, PackageRepublished: true),
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
        Assert.Equal(3, executed.GetProperty("deleted").GetInt32());
        Assert.True(executed.GetProperty("packageRepublished").GetBoolean());
    }

    [Fact]
    public void Plan_json_has_no_executed_block()
    {
        PlanPreview plan = OutputWriter.BuildPlanPreview("plan", "env", Diff([], [], []), [], []);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(plan, Options));

        Assert.False(doc.RootElement.TryGetProperty("executed", out _));
    }
}
