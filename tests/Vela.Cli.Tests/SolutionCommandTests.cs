using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

// #321: vela solution show <uniquename> argument handling. The connected path (reading the publisher prefix) is
// covered by DataverseRegistrationApplyTests via ResolveSolutionCustomizationPrefix; here we cover the arg parsing
// that runs before any connection is attempted.
public sealed class SolutionCommandTests
{
    [Fact]
    public void Help_returns_success()
    {
        Assert.Equal(ExitSuccess, SolutionCommand.Run(new[] { "help" }));
        Assert.Equal(ExitSuccess, SolutionCommand.Run(new[] { "--help" }));
    }

    [Fact]
    public void Missing_subcommand_fails()
    {
        Assert.Equal(ExitInvalidArguments, SolutionCommand.Run(new[] { "-i" }));
    }

    [Fact]
    public void Unknown_subcommand_fails()
    {
        Assert.Equal(ExitInvalidArguments, SolutionCommand.Run(new[] { "list" }));
    }

    [Fact]
    public void Show_without_a_unique_name_fails_before_connecting()
    {
        Assert.Equal(ExitInvalidArguments, SolutionCommand.Run(new[] { "show", "-i" }));
    }
}
