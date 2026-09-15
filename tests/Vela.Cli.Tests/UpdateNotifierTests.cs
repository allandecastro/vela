using System;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class UpdateNotifierTests
{
    [Theory]
    [InlineData("0.1.26", "0.1.27", true)]
    [InlineData("0.1.26", "1.0.0", true)]
    [InlineData("0.1.26", "0.1.26", false)]
    [InlineData("0.1.26", "0.1.25", false)]
    [InlineData("0.1.26", null, false)]
    [InlineData("0.1.26", "not-a-version", false)]
    public void ShouldNotify_only_for_a_strictly_newer_parseable_version(string current, string? latest, bool expected) =>
        Assert.Equal(expected, UpdateNotifier.ShouldNotify(Version.Parse(current), latest));

    [Fact]
    public void FormatMessage_names_both_versions_and_the_update_command()
    {
        string msg = UpdateNotifier.FormatMessage("0.1.26", "0.1.27");
        Assert.Contains("0.1.27", msg);
        Assert.Contains("0.1.26", msg);
        Assert.Contains("vela update", msg);
        Assert.Contains("dotnet tool update -g Vela.Cli", msg);
    }

    [Fact]
    public void UpdateCommand_BuildDotnetArgs_targets_the_global_tool()
    {
        Assert.Equal(["tool", "update", "--global", "Vela.Cli"], UpdateCommand.BuildDotnetArgs(null));
        Assert.Equal(["tool", "update", "--global", "Vela.Cli"], UpdateCommand.BuildDotnetArgs("  "));
        Assert.Equal(["tool", "update", "--global", "Vela.Cli", "--version", "0.6.1"], UpdateCommand.BuildDotnetArgs("0.6.1"));
    }
}
