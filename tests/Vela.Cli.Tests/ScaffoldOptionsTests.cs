using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class ScaffoldOptionsTests
{
    [Theory]
    [InlineData("..")]     // A1: path traversal - would delete outside the target tree on --force
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("-x")]
    [InlineData(".hidden")]
    public void IsValidSolutionName_RejectsTraversalAndLeadingPunctuation(string name) =>
        Assert.False(ScaffoldOptions.IsValidSolutionName(name));

    [Theory]
    [InlineData("ZavaSales")]
    [InlineData("Zava.Sales")]
    [InlineData("zava-sales_1")]
    public void IsValidSolutionName_AcceptsNormalNames(string name) =>
        Assert.True(ScaffoldOptions.IsValidSolutionName(name));

    [Theory]
    [InlineData("CON")]        // B9: Windows reserved device names are unusable as folder/file names
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("CON.txt")]    // reserved even with an extension
    public void IsValidSolutionName_RejectsWindowsReservedNames(string name) =>
        Assert.False(ScaffoldOptions.IsValidSolutionName(name));

    [Fact]
    public void TryParse_RejectsInvalidCoreVersion()   // A13
    {
        Assert.False(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--core-version", "abc\" /><Foo"], out _, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Theory]   // CLI-001: the non-interactive path must reject Windows reserved device names, not just the regex.
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("NUL.txt")]
    public void TryParse_RejectsWindowsReservedSolutionName(string name)
    {
        Assert.False(ScaffoldOptions.TryParse([name, "--prefix", "Zava"], out _, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParse_RejectsUnknownOption()
    {
        // A misspelled --prefix must fail fast, not be silently ignored (and then scaffold with a wrong prefix).
        Assert.False(ScaffoldOptions.TryParse(["ZavaSales", "--prefx", "Zava"], out _, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Theory]
    [InlineData("Vela")]
    [InlineData("vela")]
    [InlineData("VELA")]
    public void TryParse_RejectsVelaPrefix_WhichCollidesWithFrameworkPackages(string prefix)
    {
        // {prefix}.Plugins == Vela.Plugins (case-insensitive) -> a project sharing the id of the package it
        // references -> NuGet NU1108 "Cycle detected". Must be rejected up front.
        Assert.False(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", prefix], out _, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParse_AcceptsKnownOptions_AndBareDoubleDash()
    {
        Assert.True(ScaffoldOptions.TryParse(["ZavaSales", "--prefix", "Zava", "--", "--force"], out _, out int exitCode));
        Assert.Equal(ExitSuccess, exitCode);
    }

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
    public void TryParse_WebResources_OffByDefault()
    {
        Assert.True(ScaffoldOptions.TryParse(["MySolution"], out ScaffoldOptions options, out _));
        Assert.False(options.IncludeWebResources);
    }

    [Fact]
    public void TryParse_WebResourcesFlag_EnablesWithDefaults()
    {
        Assert.True(ScaffoldOptions.TryParse(["MySolution", "--web-resources"], out ScaffoldOptions options, out _));

        Assert.True(options.IncludeWebResources);
        Assert.Equal(ScaffoldOptions.SuggestGlobalName("MySolution"), options.WebResourceNamespace); // "mysolution"
        Assert.Equal("dist", options.WebResourcesFolder);
    }

    [Fact]
    public void TryParse_WebResources_ReadsNamespaceAndFolder()
    {
        Assert.True(ScaffoldOptions.TryParse(
            ["MySolution", "--web-resources", "--web-resources-namespace", "adc.sales", "--web-resources-folder", "dist"],
            out ScaffoldOptions options, out _));

        Assert.Equal("adc.sales", options.WebResourceNamespace);
        Assert.Equal("dist", options.WebResourcesFolder);
    }

    [Fact]
    public void TryParse_InvalidWebResourcesNamespace_Fails()
    {
        bool ok = ScaffoldOptions.TryParse(
            ["MySolution", "--web-resources", "--web-resources-namespace", "9bad"], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParse_InvalidWebResourcesFolder_Fails()
    {
        bool ok = ScaffoldOptions.TryParse(
            ["MySolution", "--web-resources", "--web-resources-folder", "a/b"], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
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
