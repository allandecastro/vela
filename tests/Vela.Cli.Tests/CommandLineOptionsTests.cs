using System;
using System.Collections.Generic;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class CommandLineOptionsTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TryTokenize_expands_inline_equals_value_form()   // B2
    {
        bool ok = CommandLineOptions.TryTokenize(
            ["--out=gen", "--strict"], Set("--out"), Set("--strict"), out string[] normalized, out int exit);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exit);
        Assert.Equal(["--out", "gen", "--strict"], normalized);
    }

    [Fact]
    public void TryTokenize_errors_when_a_required_value_option_has_no_value()   // B1
    {
        Assert.False(CommandLineOptions.TryTokenize(["--out"], Set("--out"), Set(), out _, out int exit));
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void TryTokenize_errors_when_a_value_option_is_followed_by_a_known_option()   // A9
    {
        // --url has no value: the next token is a known option, not a value.
        Assert.False(CommandLineOptions.TryTokenize(
            ["--url", "--tenant-id", "t"], Set("--url", "--tenant-id"), Set(), out _, out int exit));
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void TryTokenize_allows_a_bare_optional_value_option()   // --messages = all
    {
        bool ok = CommandLineOptions.TryTokenize(
            ["--messages"], Set("--messages"), Set(), Set("--messages"), out _, out int exit);
        Assert.True(ok);
        Assert.Equal(ExitSuccess, exit);
    }

    [Fact]
    public void TryTokenize_allows_a_dash_looking_value()
    {
        // -weird is not a known option, so it is accepted as --out's value (a name/connection string may start with '-').
        bool ok = CommandLineOptions.TryTokenize(["--out", "-weird"], Set("--out"), Set(), out string[] normalized, out _);
        Assert.True(ok);
        Assert.Equal(["--out", "-weird"], normalized);
    }

    [Fact]
    public void TryTokenize_rejects_an_unknown_option()
    {
        Assert.False(CommandLineOptions.TryTokenize(["--bogus"], Set("--out"), Set("--strict"), out _, out int exit));
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void TryReadFirstPositional_BooleanFlag_DoesNotSwallowPositional()   // A11
    {
        System.Collections.Generic.HashSet<string> valueOpts =
            new(System.StringComparer.OrdinalIgnoreCase) { "--out" };

        // --strict is not a value option, so the positional after it must survive.
        Assert.Equal("foo.nupkg", CommandLineOptions.TryReadFirstPositional(["--strict", "foo.nupkg"], valueOpts));
        // --out is a value option, so its value is skipped and the later token is the positional.
        Assert.Equal("bar.nupkg", CommandLineOptions.TryReadFirstPositional(["--out", "gen", "bar.nupkg"], valueOpts));
        // Legacy overload (no set) keeps its heuristic.
        Assert.Equal("baz.nupkg", CommandLineOptions.TryReadFirstPositional(["baz.nupkg"]));
    }


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
    public void TryValidateKnownOptions_Allows_FreshLogin()
    {
        Assert.True(CommandLineOptions.TryValidateKnownOptions(["--fresh-login"], out int exitCode));
        Assert.Equal(ExitSuccess, exitCode);
    }

    [Fact]
    public void TryResolveConnection_ConnectionString_Succeeds()
    {
        string[] args = ["--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"];

        bool ok = CommandLineOptions.TryResolveConnection(
            args, interactive: false, out string? cs, out _, out _, out _, out int exitCode);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exitCode);
        Assert.Contains("AuthType=OAuth", cs);
    }

    [Fact]
    public void TryResolveConnection_PartialClientSecretCreds_FailsWithInvalidArguments()
    {
        // --url + --client-id but no secret/tenant, non-interactive: still an argument error. The ambient
        // Azure-credential path only kicks in when ONLY --url is provided (no client-id/secret/tenant), so a
        // half-specified app registration must not silently fall back to the ambient credential.
        string[] args = ["--url", "https://zava.crm.dynamics.com", "--client-id", "app-id"];

        bool ok = CommandLineOptions.TryResolveConnection(
            args, interactive: false, out _, out _, out _, out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
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

    // ---- --timeout (connection timeout in minutes) ------------------------------------------------

    [Fact]
    public void TryReadTimeoutMinutes_absent_leaves_null_and_succeeds()
    {
        bool ok = CommandLineOptions.TryReadTimeoutMinutes(["--url", "https://x.crm.dynamics.com"], out int? minutes, out int exit);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exit);
        Assert.Null(minutes);
    }

    [Fact]
    public void TryReadTimeoutMinutes_parses_a_valid_value()
    {
        bool ok = CommandLineOptions.TryReadTimeoutMinutes(["--timeout", "15"], out int? minutes, out int exit);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exit);
        Assert.Equal(15, minutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    public void TryReadTimeoutMinutes_rejects_out_of_range_or_non_numeric_values(string value)
    {
        bool ok = CommandLineOptions.TryReadTimeoutMinutes(["--timeout", value], out int? minutes, out int exit);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exit);
        Assert.Null(minutes);
    }

    [Fact]
    public void TryParseSharedOptions_wires_timeout_onto_shared_options()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vela-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Sample.Plugins.1.0.0.nupkg"), [0x50, 0x4b]);
        try
        {
            string[] args =
            [
                "--packages-dir", dir, "--timeout", "15",
                "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"
            ];
            bool ok = CommandLineOptions.TryParseSharedOptions(args, out SharedOptions options, out int exit);

            Assert.True(ok);
            Assert.Equal(ExitSuccess, exit);
            Assert.Equal(15, options.TimeoutMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryParseSharedOptions_leaves_timeout_null_when_absent()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vela-timeout-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Sample.Plugins.1.0.0.nupkg"), [0x50, 0x4b]);
        try
        {
            string[] args =
            [
                "--packages-dir", dir,
                "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"
            ];
            bool ok = CommandLineOptions.TryParseSharedOptions(args, out SharedOptions options, out int exit);

            Assert.True(ok);
            Assert.Equal(ExitSuccess, exit);
            Assert.Null(options.TimeoutMinutes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryParseSharedOptions_rejects_an_invalid_timeout()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vela-timeout-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Sample.Plugins.1.0.0.nupkg"), [0x50, 0x4b]);
        try
        {
            string[] args =
            [
                "--packages-dir", dir, "--timeout", "99",
                "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"
            ];
            bool ok = CommandLineOptions.TryParseSharedOptions(args, out _, out int exit);

            Assert.False(ok);
            Assert.Equal(ExitInvalidArguments, exit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- --batch-size (ExecuteMultiple chunk size) ------------------------------------------------

    [Fact]
    public void TryReadBatchChunkSize_absent_leaves_null_and_succeeds()
    {
        bool ok = CommandLineOptions.TryReadBatchChunkSize(["--url", "https://x.crm.dynamics.com"], out int? size, out int exit);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exit);
        Assert.Null(size);
    }

    [Fact]
    public void TryReadBatchChunkSize_parses_a_valid_value()
    {
        bool ok = CommandLineOptions.TryReadBatchChunkSize(["--batch-size", "250"], out int? size, out int exit);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exit);
        Assert.Equal(250, size);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("abc")]
    public void TryReadBatchChunkSize_rejects_out_of_range_or_non_numeric_values(string value)
    {
        bool ok = CommandLineOptions.TryReadBatchChunkSize(["--batch-size", value], out int? size, out int exit);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exit);
        Assert.Null(size);
    }

    [Fact]
    public void TryParseSharedOptions_wires_batch_size_onto_shared_options()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vela-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Sample.Plugins.1.0.0.nupkg"), [0x50, 0x4b]);
        try
        {
            string[] args =
            [
                "--packages-dir", dir, "--batch-size", "50",
                "--connection-string", "AuthType=OAuth;Url=https://x.crm.dynamics.com"
            ];
            bool ok = CommandLineOptions.TryParseSharedOptions(args, out SharedOptions options, out int exit);

            Assert.True(ok);
            Assert.Equal(ExitSuccess, exit);
            Assert.Equal(50, options.BatchChunkSize);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// E1 regression: <c>--interactive</c> must not fire a blocking "Package assembly name" prompt under redirected
/// stdin (CI/agents/tests), which hung plan/apply/inspect on an open-but-empty pipe. Uses the serial
/// console-redirect collection because it swaps the process-global <see cref="Console.In"/>/<see cref="Console.Out"/>.
/// </summary>
[Collection("ConsoleRedirect")]
public class CommandLineOptionsInteractivePromptTests
{
    [Fact]
    public void TryParseSharedOptions_interactive_does_not_prompt_for_package_assembly_under_redirected_stdin()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vela-e1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Sample.Plugins.1.0.0.nupkg"), [0x50, 0x4b]);

        TextReader originalIn = Console.In;
        TextWriter originalOut = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(string.Empty));
            Console.SetOut(captured);

            string[] args =
            [
                "--packages-dir", dir, "--interactive",
                "--connection-string", "AuthType=ClientSecret;Url=https://x.crm.dynamics.com;TenantId=t;ClientId=c;ClientSecret=s"
            ];
            bool ok = CommandLineOptions.TryParseSharedOptions(args, out SharedOptions options, out int exit);

            Assert.True(ok);
            Assert.Equal(ExitSuccess, exit);
            Assert.Null(options.PackageAssembly);
            Assert.DoesNotContain("Package assembly name", captured.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- ValidateClientSecretConnectionInputs (#307 L3) ----

    [Fact]
    public void ValidateClientSecretConnectionInputs_AcceptsAValidHttpsUrlAndBase64Secret()
    {
        // '=' in the secret (base64 padding) is fine - a connection-string value is everything after the first '='.
        Assert.Null(CommandLineOptions.ValidateClientSecretConnectionInputs(
            "https://org.crm.dynamics.com", "tenant", "client", "abc123=="));
    }

    [Theory]
    [InlineData("http://org.crm.dynamics.com")]  // not https
    [InlineData("org.crm.dynamics.com")]         // not absolute
    [InlineData("")]                             // empty
    [InlineData("not a url")]
    public void ValidateClientSecretConnectionInputs_RejectsANonHttpsOrRelativeUrl(string url)
    {
        string? error = CommandLineOptions.ValidateClientSecretConnectionInputs(url, "tenant", "client", "secret");
        Assert.NotNull(error);
        Assert.Contains("--url", error);
    }

    [Theory]
    [InlineData("https://x;AuthType=Foo", "tenant", "client", "secret", "--url")]
    [InlineData("https://x", "ten;ant", "client", "secret", "--tenant-id")]
    [InlineData("https://x", "tenant", "cli;ent", "secret", "--client-id")]
    [InlineData("https://x", "tenant", "client", "sec;ret", "--client-secret")]
    public void ValidateClientSecretConnectionInputs_RejectsSemicolonInAnyValue(string url, string tenant, string client, string secret, string expectedOption)
    {
        string? error = CommandLineOptions.ValidateClientSecretConnectionInputs(url, tenant, client, secret);
        Assert.NotNull(error);
        Assert.Contains(expectedOption, error);
        Assert.Contains(";", error);
        Assert.DoesNotContain("sec;ret", error);  // the secret value is never echoed
    }
}
