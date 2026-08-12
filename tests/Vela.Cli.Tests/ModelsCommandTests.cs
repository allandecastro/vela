using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class ModelsCommandTests
{
    [Fact]
    public void Models_Help_Succeeds()
    {
        Assert.Equal(ExitSuccess, ModelsCommand.Run(["--help"]));
    }

    [Fact]
    public void Models_WithoutScope_Fails_BeforeConnecting()
    {
        // No --solution / --entities → refuses up front (never touches the connection).
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--connection-string", "irrelevant"]));
    }

    [Fact]
    public void Models_CustomApisCountAsScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --custom-apis is a valid scope; the run only fails here because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--custom-apis", "zava_DoThing", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_WithScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // Scope is valid, but the namespace can't be inferred (bad --project) → fails before connecting.
        int exit = ModelsCommand.Run(["--entities", "account", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_RolesCountAsScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --roles is a valid scope; the run only fails here because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--roles", "Salesperson", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_SolutionRoles_WithoutSolution_Fails_BeforeConnecting()
    {
        // --solution-roles has nothing to enumerate without --solution → refused up front.
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--solution-roles", "--connection-string", "irrelevant"]));
    }

    [Fact]
    public void Models_SolutionRoles_WithSolution_IsAScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --solution-roles + --solution is a valid scope; it only fails because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--solution-roles", "--solution", "MySolution", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }
}
