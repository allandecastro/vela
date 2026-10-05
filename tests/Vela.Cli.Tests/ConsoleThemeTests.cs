using System.IO;
using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Locks in the terminal-styling contract: color helpers no-op to plain text when color is disabled (the
/// state every pipe / CI / test capture sees), and the human summary renderers produce the expected plain
/// layout so scripted output and assertions stay stable regardless of the color decision.
/// </summary>
public class ConsoleThemeTests
{
    [Fact]
    public void Color_helpers_return_plain_text_when_color_disabled()
    {
        Assert.Equal("hello", ConsoleTheme.Added("hello", color: false));
        Assert.Equal("hello", ConsoleTheme.Removed("hello", color: false));
        Assert.Equal("hello", ConsoleTheme.Changed("hello", color: false));
        Assert.Equal("hello", ConsoleTheme.Heading("hello", color: false));
        Assert.Equal("hello", ConsoleTheme.Muted("hello", color: false));
    }

    [Fact]
    public void Color_helpers_wrap_in_ansi_when_color_enabled()
    {
        string painted = ConsoleTheme.Added("hello", color: true);

        Assert.StartsWith("\x1b[", painted);
        Assert.EndsWith("\x1b[0m", painted);
        Assert.Contains("hello", painted);
    }

    [Fact]
    public void Success_and_failure_prefix_their_glyphs()
    {
        Assert.Equal($"{ConsoleTheme.Check} done", ConsoleTheme.Success("done", color: false));
        Assert.Equal($"{ConsoleTheme.Cross} nope", ConsoleTheme.Failure("nope", color: false));
    }

    [Fact]
    public void WritePlanSummary_renders_plain_diff_layout()
    {
        RegistrationItem added = Item("step", "Zava.Plugins.Foo", "{\"name\":\"Foo\"}");
        var diff = new RegistrationDiff([], [], [added], [], [], [added], [])
        {
            Summary = new DiffSummary(1, 0, 0),
        };
        PlanPreview preview = OutputWriter.BuildPlanPreview("plan", "https://zava.crm.dynamics.com", diff, [], []);

        var sw = new StringWriter();
        OutputWriter.WritePlanSummary(preview, sw, color: false);
        string output = sw.ToString();

        Assert.Contains("plan https://zava.crm.dynamics.com", output);
        Assert.Contains("summary: +1 -0 ~0", output);
        Assert.Contains("operations:", output);
        Assert.Contains("create step: Zava.Plugins.Foo", output);
        Assert.Contains("added:", output);
        Assert.Contains("+ step: Foo", output);
        Assert.DoesNotContain("\x1b[", output); // no ANSI when color is off
    }

    [Fact]
    public void BuildPlanPreview_surfaces_a_package_republish_when_the_step_diff_is_empty()
    {
        // A code-only rebuild: unchanged steps/custom APIs (empty diff) but a rebuilt assembly whose package
        // content differs. Plan must still report a change and list the package operation, or an agent reading
        // plan would conclude there is nothing to deploy while apply would republish the package.
        var diff = new RegistrationDiff([], [], [], [], [], [], [])
        {
            Summary = new DiffSummary(0, 0, 0),
        };
        PackagePublishAction[] packageActions = [new PackagePublishAction("acme_Plugins", IsCreate: false)];

        PlanPreview preview = OutputWriter.BuildPlanPreview(
            "plan", "https://zava.crm.dynamics.com", diff, [], [], packageActions);

        Assert.True(preview.HasChanges);
        ApplyOperation op = Assert.Single(preview.Operations);
        Assert.Equal("update", op.Action);
        Assert.Equal("package", op.Kind);
        Assert.Equal("acme_Plugins", op.Type);

        var sw = new StringWriter();
        OutputWriter.WritePlanSummary(preview, sw, color: false);
        Assert.Contains("update package: acme_Plugins", sw.ToString());
    }

    [Fact]
    public void WritePlanSummary_surfaces_diagnostics_as_a_warnings_block()
    {
        var diff = new RegistrationDiff([], [], [], [], [], [], [])
        {
            Summary = new DiffSummary(0, 0, 0),
        };
        PlanPreview preview = OutputWriter.BuildPlanPreview(
            "plan", "https://zava.crm.dynamics.com", diff,
            ["Execution-order collision: 2 steps share rank 1 on Update of account at PostOpSync."],
            []);

        var sw = new StringWriter();
        OutputWriter.WritePlanSummary(preview, sw, color: false);
        string output = sw.ToString();

        Assert.Contains("warnings (1):", output);
        Assert.Contains("Execution-order collision: 2 steps share rank 1", output);
        Assert.DoesNotContain("\x1b[", output);
    }

    [Fact]
    public void WriteDiagnostics_writes_nothing_when_there_are_no_diagnostics()
    {
        var sw = new StringWriter();
        OutputWriter.WriteDiagnostics([], sw, color: false);

        Assert.Equal(string.Empty, sw.ToString());
    }

    [Fact]
    public void WriteStateSummary_lists_steps_and_counts_in_plain_text()
    {
        RegistrationItem step = Item("step", "Zava.Plugins.Foo", "{\"name\":\"Foo\"}");
        var state = new RegistrationState(["Zava.Plugins"], [], [], System.DateTime.UtcNow, [step], []);

        var sw = new StringWriter();
        OutputWriter.WriteStateSummary("local", state, sw, color: false);
        string output = sw.ToString();

        Assert.Contains("local assemblies: Zava.Plugins", output);
        Assert.Contains("local steps: 1", output);
        Assert.Contains("- Foo", output);
        Assert.DoesNotContain("\x1b[", output);
    }

    private static RegistrationItem Item(string kind, string type, string definitionJson) =>
        new(kind, type, JsonDocument.Parse(definitionJson).RootElement.Clone());
}
