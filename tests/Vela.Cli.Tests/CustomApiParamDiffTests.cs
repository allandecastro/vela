using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// The per-parameter diff the plan summary shows for a changed Custom API: request inputs and response outputs
/// added, removed, or retyped are listed individually rather than dumping the whole before/after object.
/// </summary>
public class CustomApiParamDiffTests
{
    private static JsonElement Api(object[] inputs, object[] outputs) =>
        JsonSerializer.SerializeToElement(new
        {
            uniqueName = "zava_DoThing",
            isBound = false,
            inputParameters = inputs,
            outputParameters = outputs,
        });

    private static object Input(string name, string type, bool isOptional = false, bool isCollection = false, string? entity = null) =>
        new { name, isOptional, parameterTypeName = type, isCollection, logicalEntityName = entity };

    private static object Output(string name, string type, bool isCollection = false, string? entity = null) =>
        new { name, parameterTypeName = type, isCollection, logicalEntityName = entity };

    [Fact]
    public void Reports_an_added_input_parameter()
    {
        JsonElement before = Api([], []);
        JsonElement after = Api([Input("Amount", "System.Int32")], []);

        List<string> lines = OutputWriter.DescribeCustomApiParamChanges(before, after);

        string line = Assert.Single(lines);
        Assert.Contains("+ input param: Amount", line);
        Assert.Contains("System.Int32", line);
    }

    [Fact]
    public void Reports_a_removed_output_property()
    {
        JsonElement before = Api([], [Output("Result", "System.String")]);
        JsonElement after = Api([], []);

        List<string> lines = OutputWriter.DescribeCustomApiParamChanges(before, after);

        string line = Assert.Single(lines);
        Assert.Contains("- output param: Result", line);
    }

    [Fact]
    public void Reports_a_retyped_input_parameter_with_before_and_after()
    {
        JsonElement before = Api([Input("Amount", "System.Int32")], []);
        JsonElement after = Api([Input("Amount", "System.String")], []);

        List<string> lines = OutputWriter.DescribeCustomApiParamChanges(before, after);

        string line = Assert.Single(lines);
        Assert.Contains("~ input param: Amount", line);
        Assert.Contains("System.Int32", line);
        Assert.Contains("System.String", line);
    }

    [Fact]
    public void Reports_an_optionality_change()
    {
        JsonElement before = Api([Input("Amount", "System.Int32", isOptional: false)], []);
        JsonElement after = Api([Input("Amount", "System.Int32", isOptional: true)], []);

        List<string> lines = OutputWriter.DescribeCustomApiParamChanges(before, after);

        string line = Assert.Single(lines);
        Assert.Contains("~ input param: Amount", line);
        Assert.Contains("optional", line);
    }

    [Fact]
    public void Reports_nothing_when_the_parameter_sets_are_identical()
    {
        JsonElement before = Api([Input("Amount", "System.Int32")], [Output("Result", "System.String")]);
        JsonElement after = Api([Input("Amount", "System.Int32")], [Output("Result", "System.String")]);

        Assert.Empty(OutputWriter.DescribeCustomApiParamChanges(before, after));
    }

    [Fact]
    public void Surfaces_the_param_diff_in_the_plan_summary()
    {
        var before = new RegistrationItem("custom-api", "zava_DoThing", Api([Input("Amount", "System.Int32")], []));
        var after = new RegistrationItem("custom-api", "zava_DoThing", Api([Input("Amount", "System.String")], []));
        var change = new RegistrationChange("custom-api", "zava_DoThing", before.Definition, after.Definition);
        var diff = new RegistrationDiff([], [], [], [], [change], [], []);
        PlanPreview preview = OutputWriter.BuildPlanPreview("plan", "https://zava.crm.dynamics.com", diff, [], []);

        var sw = new System.IO.StringWriter();
        OutputWriter.WritePlanSummary(preview, sw, color: false);
        string output = sw.ToString();

        Assert.Contains("~ custom-api: zava_DoThing", output);
        Assert.Contains("~ input param: Amount", output);
        Assert.DoesNotContain('', output);
    }
}
