using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class InteractiveMenuTests
{
    [Fact]
    public void ComposeModelsArgs_BySolution_UsesSolutionScopeAndInteractive()   // B8
    {
        Assert.Equal(["models", "--solution", "zava_core", "--interactive"],
            InteractiveMenu.ComposeModelsArgs(bySolution: true, "zava_core"));
    }

    [Fact]
    public void ComposeModelsArgs_ByTables_UsesEntitiesScopeAndInteractive()   // B8
    {
        Assert.Equal(["models", "--entities", "account,contact", "--interactive"],
            InteractiveMenu.ComposeModelsArgs(bySolution: false, "account,contact"));
    }

    [Fact]
    public void ComposeInitArgs_IncludesAllProjects_ByDefault()
    {
        string[] args = InteractiveMenu.ComposeInitArgs("ZavaSales", "Zava", ".", integrations: true, tests: true, console: true);

        Assert.Equal(["init", "--name", "ZavaSales", "--prefix", "Zava", "--output", "."], args);
    }

    [Fact]
    public void ComposeInitArgs_AddsSkipFlags_WhenProjectsDisabled()
    {
        string[] args = InteractiveMenu.ComposeInitArgs("X", "X", "/tmp", integrations: false, tests: false, console: false);

        Assert.Contains("--no-integrations", args);
        Assert.Contains("--no-tests", args);
        Assert.Contains("--no-console", args);
    }

    [Fact]
    public void ComposeInitArgs_RoundTripsThroughScaffoldOptions()
    {
        string[] args = InteractiveMenu.ComposeInitArgs("ZavaSales", "Zava.CRM", "/tmp/out", integrations: false, tests: false, console: true);

        bool ok = ScaffoldOptions.TryParse(args[1..], out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.Equal("ZavaSales", options.SolutionName);
        Assert.Equal("Zava.CRM", options.Prefix);
        Assert.Equal("/tmp/out", options.OutputDirectory);
        Assert.False(options.IncludeIntegrations);
        Assert.False(options.IncludeTests);
        Assert.True(options.IncludeConsole);
    }

    [Theory]
    [InlineData(true, new[] { "apply", "--interactive", "--auto-approve" })]
    [InlineData(false, new[] { "plan", "--interactive" })]
    public void ComposeApplyArgs_TogglesExecute(bool execute, string[] expected)
    {
        Assert.Equal(expected, InteractiveMenu.ComposeApplyArgs(execute));
    }

    [Fact]
    public void ComposeAddArgs_Plugin_IncludesEntityAndMessage()
    {
        string[] args = InteractiveMenu.ComposeAddArgs("plugin", "AccountValidator", "Account", "Update", null, null, function: false, filteringAttributes: "Name,Telephone1");

        Assert.Equal(["plugin", "AccountValidator", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name,Telephone1"], args);
    }

    [Fact]
    public void ComposeAddArgs_CustomApi_Function_Global_AddsFunctionFlagAndNoEntity()
    {
        string[] args = InteractiveMenu.ComposeAddArgs("customapi", "GetScore", null, null, null, null, function: true);

        Assert.Equal(["customapi", "GetScore", "--function"], args);
    }

    [Fact]
    public void ComposeAddArgs_Test_PutsKindFirst()
    {
        string[] args = InteractiveMenu.ComposeAddArgs("test", "ExportContact", "Contact", null, null, "customapi", function: false);

        Assert.Equal(["test", "ExportContact", "--kind", "customapi", "--entity", "Contact"], args);
    }

    [Fact]
    public void ComposeAddArgs_Step_IncludesHandler()
    {
        string[] args = InteractiveMenu.ComposeAddArgs("step", "AccountRouter", "Account", "Update", "HandleUpdate", null, function: false);

        Assert.Equal(["step", "AccountRouter", "--entity", "Account", "--message", "Update", "--handler", "HandleUpdate"], args);
    }

    [Fact]
    public void ToolInfo_Version_IsPopulatedWithoutBuildMetadata()
    {
        string version = ToolInfo.Version;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain("+", version);
    }
}
