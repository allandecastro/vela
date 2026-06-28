using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class ScaffoldOptionsTests
{
    [Fact]
    public void TryParse_AppliesDefaults_WhenOnlyNameProvided()
    {
        bool ok = ScaffoldOptions.TryParse(["--name", "ZavaSales"], out ScaffoldOptions options, out int exitCode);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exitCode);
        Assert.Equal("ZavaSales", options.SolutionName);
        Assert.Equal("ZavaSales", options.Prefix);
        Assert.Equal(".", options.OutputDirectory);
        Assert.True(options.IncludeConsole);
        Assert.True(options.IncludeIntegrations);
        Assert.Equal(ScaffoldOptions.DefaultCoreVersion, options.CoreVersion);
        Assert.False(options.Force);
    }

    [Fact]
    public void TryParse_ReadsNameFromPositional()
    {
        bool ok = ScaffoldOptions.TryParse(["ZavaSales"], out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.Equal("ZavaSales", options.SolutionName);
    }

    [Fact]
    public void TryParse_ManagedIdentityDefaultsFalse_AndIsSetByFlagOrAlias()
    {
        Assert.True(ScaffoldOptions.TryParse(["MySolution"], out ScaffoldOptions plain, out _));
        Assert.False(plain.ManagedIdentity);

        Assert.True(ScaffoldOptions.TryParse(["MySolution", "--managed-identity"], out ScaffoldOptions mi, out _));
        Assert.True(mi.ManagedIdentity);

        Assert.True(ScaffoldOptions.TryParse(["MySolution", "--mi"], out ScaffoldOptions alias, out _));
        Assert.True(alias.ManagedIdentity);
    }

    [Fact]
    public void TryParse_ResolvesPositionalName_AfterForceFlag()
    {
        // Boolean flags must not swallow a positional name (regression for the
        // TryReadFirstPositional value-skipping heuristic).
        bool ok = ScaffoldOptions.TryParse(["--force", "ZavaSales"], out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.Equal("ZavaSales", options.SolutionName);
        Assert.True(options.Force);
    }

    [Fact]
    public void TryParse_ResolvesPositionalName_AfterNoConsoleFlag()
    {
        bool ok = ScaffoldOptions.TryParse(["--no-console", "ZavaSales"], out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.Equal("ZavaSales", options.SolutionName);
        Assert.False(options.IncludeConsole);
    }

    [Fact]
    public void TryParse_DoesNotTreatOptionValueAsPositionalName()
    {
        // `--output build` must not let "build" become the solution name.
        bool ok = ScaffoldOptions.TryParse(["--prefix", "Zava", "--output", "build", "ZavaSales"], out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.Equal("ZavaSales", options.SolutionName);
        Assert.Equal("build", options.OutputDirectory);
    }

    [Fact]
    public void TryParse_ParsesAllOptions()
    {
        string[] args =
        [
            "--name", "ZavaSales",
            "--prefix", "Zava.CRM",
            "--output", "/tmp/out",
            "--core-version", "2.1.0",
            "--force",
        ];

        bool ok = ScaffoldOptions.TryParse(args, out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.Equal("Zava.CRM", options.Prefix);
        Assert.Equal("/tmp/out", options.OutputDirectory);
        Assert.Equal("2.1.0", options.CoreVersion);
        Assert.True(options.Force);
    }

    [Fact]
    public void TryParse_NoConsoleAndNoIntegrations_FlipFlags()
    {
        bool ok = ScaffoldOptions.TryParse(["--name", "X", "--no-console", "--no-integrations"], out ScaffoldOptions options, out _);

        Assert.True(ok);
        Assert.False(options.IncludeConsole);
        Assert.False(options.IncludeIntegrations);
    }

    [Fact]
    public void TryParse_Fails_WhenNameMissing()
    {
        bool ok = ScaffoldOptions.TryParse([], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Theory]
    [InlineData("9bad")]
    [InlineData("has space")]
    [InlineData(".leadingdot")]
    public void TryParse_Fails_OnInvalidPrefix(string prefix)
    {
        bool ok = ScaffoldOptions.TryParse(["--name", "Valid", "--prefix", prefix], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("bad/name")]
    public void TryParse_Fails_OnInvalidSolutionName(string name)
    {
        bool ok = ScaffoldOptions.TryParse(["--name", name], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void SuggestPrefix_StripsInvalidCharacters()
    {
        Assert.Equal("ZavaSales", ScaffoldOptions.SuggestPrefix("Zava Sales"));
    }

    [Fact]
    public void SuggestPrefix_PrependsApp_WhenStartingWithDigit()
    {
        Assert.Equal("App360", ScaffoldOptions.SuggestPrefix("360"));
    }

    [Theory]
    [InlineData("My..App", "My.App")]
    [InlineData("My.App.", "My.App")]
    [InlineData(".My.App", "My.App")]
    [InlineData("Sales.2024", "Sales.App2024")]
    [InlineData("Zava.365", "Zava.App365")]
    public void SuggestPrefix_ProducesValidDottedPrefix(string name, string expected)
    {
        string prefix = ScaffoldOptions.SuggestPrefix(name);

        Assert.Equal(expected, prefix);
        Assert.True(ScaffoldOptions.IsValidPrefix(prefix));
    }
}
