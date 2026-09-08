using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void TryValidateKnownOptions_Rejects_UnknownOption()
    {
        // The reported typo: --packages-dur instead of --packages-dir must fail fast, not be silently ignored.
        string[] args = ["--packages-dur", ".\\artifacts\\", "--interactive"];

        Assert.False(CommandLineOptions.TryValidateKnownOptions(args, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void IsHelpRequested_True_ForHelpTokenAnywhere(string token)
    {
        Assert.True(CommandLineOptions.IsHelpRequested(["plugin", "Foo", token]));
    }

    [Fact]
    public void IsHelpRequested_False_WhenNoHelpToken()
    {
        Assert.False(CommandLineOptions.IsHelpRequested(["plugin", "Foo", "--entity", "Account"]));
    }

    [Fact]
    public void TryValidateKnownOptions_Generic_SkipsBareDoubleDash_And_ValuesAndPositionals()
    {
        HashSet<string> valueOpts = new(StringComparer.OrdinalIgnoreCase) { "--name" };
        HashSet<string> flagOpts = new(StringComparer.OrdinalIgnoreCase) { "--force" };
        // positional 'Foo', a bare '--' separator, a value option + its (dash-looking) value, and a known flag.
        string[] args = ["Foo", "--", "--name", "-weird", "--force"];

        Assert.True(CommandLineOptions.TryValidateKnownOptions(args, valueOpts, flagOpts, out int exitCode));
        Assert.Equal(ExitSuccess, exitCode);
    }

    [Fact]
    public void TryValidateKnownOptions_Generic_RejectsUnknown()
    {
        HashSet<string> valueOpts = new(StringComparer.OrdinalIgnoreCase) { "--name" };
        HashSet<string> flagOpts = new(StringComparer.OrdinalIgnoreCase) { "--force" };

        Assert.False(CommandLineOptions.TryValidateKnownOptions(["--nam", "Foo"], valueOpts, flagOpts, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void AddWizardComposedArgs_PassTheAddAllowlist()
    {
        // Guard against a regression where the interactive add wizard emits an option the guard would reject.
        string[] composed = InteractiveMenu.ComposeAddArgs("plugin", "MyPlugin", "Account", "Update", "OnUpdate", "customapi", function: true);

        Assert.True(CommandLineOptions.TryValidateKnownOptions(composed, AddCommand.ValueOptions, AddCommand.FlagOptions, out int exitCode));
        Assert.Equal(ExitSuccess, exitCode);
    }

    [Fact]
    public void TryValidateKnownOptions_Accepts_KnownOptions_And_Positional()
    {
        // Value options consume their next token; flags stand alone; the .nupkg is a positional.
        string[] args = ["--packages-dir", ".\\artifacts\\", "--solution", "Zava", "--strict", "pkg.nupkg", "-v"];

        Assert.True(CommandLineOptions.TryValidateKnownOptions(args, out int exitCode));
        Assert.Equal(ExitSuccess, exitCode);
    }

    [Fact]
    public void TryValidateKnownOptions_DoesNotFlag_ValueThatLooksLikeOption()
    {
        // A value that itself starts with '-' is skipped because it follows a known value option.
        string[] args = ["--out", "-weird-name", "--package", "a.nupkg"];

        Assert.True(CommandLineOptions.TryValidateKnownOptions(args, out _));
    }


    [Fact]
    public void TryReadOption_ReturnsValue_AfterMatchingFlag()
    {
        string[] args = ["plan", "--url", "https://zava.crm.dynamics.com", "--strict"];

        Assert.Equal("https://zava.crm.dynamics.com", CommandLineOptions.TryReadOption(args, "--url"));
    }

    [Fact]
    public void TryReadOption_IsCaseInsensitive_OnFlagName()
    {
        string[] args = ["--Tenant-Id", "abc"];

        Assert.Equal("abc", CommandLineOptions.TryReadOption(args, "--tenant-id"));
    }

    [Fact]
    public void TryReadOption_ReturnsNull_WhenFlagMissing()
    {
        string[] args = ["--url", "x"];

        Assert.Null(CommandLineOptions.TryReadOption(args, "--client-id"));
    }

    [Fact]
    public void TryReadOption_ReturnsNull_WhenFlagIsLastArgWithoutValue()
    {
        string[] args = ["--url"];

        Assert.Null(CommandLineOptions.TryReadOption(args, "--url"));
    }

    [Fact]
    public void TryReadOptionValue_ReturnsValue_WhenNextTokenIsAValue()
    {
        // Targeted --messages: the following token is a real value, so it is returned.
        string[] args = ["--messages", "QualifyLead,AddToQueue", "--out", "gen"];

        Assert.Equal("QualifyLead,AddToQueue", CommandLineOptions.TryReadOptionValue(args, "--messages"));
        Assert.True(CommandLineOptions.HasFlag(args, "--messages"));
    }

    [Fact]
    public void TryReadOptionValue_ReturnsNull_WhenBareFlagIsLastArg()
    {
        // ALL mode: bare --messages with nothing after it. Present (HasFlag) but no value.
        string[] args = ["--messages"];

        Assert.Null(CommandLineOptions.TryReadOptionValue(args, "--messages"));
        Assert.True(CommandLineOptions.HasFlag(args, "--messages"));
    }

    [Fact]
    public void TryReadOptionValue_ReturnsNull_WhenNextTokenIsAnotherOption()
    {
        // ALL mode: bare --messages followed by another option must not swallow that option as its value.
        string[] args = ["--messages", "--out", "gen"];

        Assert.Null(CommandLineOptions.TryReadOptionValue(args, "--messages"));
        Assert.True(CommandLineOptions.HasFlag(args, "--messages"));
    }

    [Fact]
    public void TryReadOptionValue_ReturnsNull_WhenFlagAbsent()
    {
        string[] args = ["--out", "gen"];

        Assert.Null(CommandLineOptions.TryReadOptionValue(args, "--messages"));
        Assert.False(CommandLineOptions.HasFlag(args, "--messages"));
    }

    [Fact]
    public void ReadMultiOption_CollectsAllOccurrences()
    {
        string[] args = ["--package", "a.nupkg", "--package", "b.nupkg", "--strict"];

        List<string> values = CommandLineOptions.ReadMultiOption(args, "--package");

        Assert.Equal(["a.nupkg", "b.nupkg"], values);
    }

    [Fact]
    public void ReadMultiOption_ReturnsEmpty_WhenAbsent()
    {
        string[] args = ["--strict"];

        Assert.Empty(CommandLineOptions.ReadMultiOption(args, "--package"));
    }

    [Fact]
    public void TryReadFirstPositional_ReturnsFirstNonFlagArgument()
    {
        // --url consumes its value; the first standalone token is the positional package path.
        string[] args = ["--url", "https://x", "package.nupkg"];

        Assert.Equal("package.nupkg", CommandLineOptions.TryReadFirstPositional(args));
    }

    [Fact]
    public void TryReadFirstPositional_ReturnsNull_WhenOnlyFlagsAndValues()
    {
        string[] args = ["--url", "https://x", "--strict"];

        Assert.Null(CommandLineOptions.TryReadFirstPositional(args));
    }

    [Fact]
    public void BuildOAuthConnectionString_UsesTokenCache_AndDefaultSampleApp()
    {
        string cs = CommandLineOptions.BuildOAuthConnectionString(
            "https://zava.crm.dynamics.com", appId: null, tokenCachePath: "/home/u/.vela/token.dat", freshLogin: false);

        Assert.Contains("AuthType=OAuth", cs);
        Assert.Contains("Url=https://zava.crm.dynamics.com", cs);
        Assert.Contains($"AppId={CommandLineOptions.DefaultInteractiveClientId}", cs);
        Assert.Contains("RedirectUri=app://", cs);
        Assert.Contains("TokenCacheStorePath=/home/u/.vela/token.dat", cs);
        Assert.Contains("LoginPrompt=Auto", cs);
    }

    [Fact]
    public void BuildOAuthConnectionString_FreshLogin_ForcesAlwaysPrompt_AndHonorsCustomAppId()
    {
        string cs = CommandLineOptions.BuildOAuthConnectionString(
            "https://zava.crm.dynamics.com", appId: "my-app-id", tokenCachePath: "/t/token.dat", freshLogin: true);

        Assert.Contains("LoginPrompt=Always", cs);
        Assert.Contains("AppId=my-app-id", cs);
        Assert.DoesNotContain("LoginPrompt=Auto", cs);
    }

    [Fact]
    public void TryValidateKnownOptions_Allows_FreshLogin()
    {
        Assert.True(CommandLineOptions.TryValidateKnownOptions(["--fresh-login"], out int exitCode));
        Assert.Equal(ExitSuccess, exitCode);
    }

    [Fact]
    public void BuildClientSecretConnectionString_FormatsAllFields()
    {
        string cs = CommandLineOptions.BuildClientSecretConnectionString(
            "https://zava.crm.dynamics.com", "tenant", "client", "secret");

        Assert.Contains("AuthType=ClientSecret", cs);
        Assert.Contains("Url=https://zava.crm.dynamics.com", cs);
        Assert.Contains("TenantId=tenant", cs);
        Assert.Contains("ClientId=client", cs);
        Assert.Contains("ClientSecret=secret", cs);
        Assert.Contains("RequireNewInstance=true", cs);
    }
}
