using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class SignCommandTests
{
    [Fact]
    public void Run_RejectsUnknownOption()
    {
        // --certificate-fingerprint is a `dotnet nuget sign` arg, NOT a `vela sign` option: it must be rejected.
        Assert.Equal(ExitInvalidArguments, SignCommand.Run(["--package", "x.nupkg", "--certificate-fingerprint", "ABCD"]));
    }

    [Fact]
    public void Run_Help_PrintsUsage_AndSucceeds()
    {
        Assert.Equal(ExitSuccess, SignCommand.Run(["--help"]));
    }

    [Fact]
    public void TryParseOptions_ReadsJsonFlag()
    {
        Assert.True(SignCommand.TryParseOptions(["--package", "x.nupkg", "--fingerprint", "ABCD", "--json"], out SignOptions options, out _));
        Assert.True(options.Json);
    }

    [Fact]
    public void BuildSignReport_Reports_Package_And_Method()
    {
        SignOptions fingerprint = new SignOptions("pkg.nupkg", "ABCD", null, null, null, null, "http://ts", Json: true);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(SignCommand.BuildSignReport(fingerprint));
        Assert.Equal("pkg.nupkg", doc.RootElement.GetProperty("package").GetString());
        Assert.True(doc.RootElement.GetProperty("signed").GetBoolean());
        Assert.Equal("fingerprint", doc.RootElement.GetProperty("method").GetString());

        SignOptions pfx = new SignOptions("pkg.nupkg", null, "cert.pfx", null, "pw", null, "http://ts");
        using System.Text.Json.JsonDocument doc2 = System.Text.Json.JsonDocument.Parse(SignCommand.BuildSignReport(pfx));
        Assert.Equal("pfx", doc2.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public void TryParseOptions_Fingerprint_IsValid_AndDefaultsTimestamp()
    {
        bool ok = SignCommand.TryParseOptions(
            ["--package", "Zava.Integrations.1.0.0.nupkg", "--fingerprint", "ABC123"],
            out SignOptions options,
            out int exitCode);

        Assert.True(ok);
        Assert.Equal(ExitSuccess, exitCode);
        Assert.Equal("Zava.Integrations.1.0.0.nupkg", options.PackagePath);
        Assert.Equal("ABC123", options.Fingerprint);
        Assert.Equal(SignOptions.DefaultTimestampUrl, options.TimestampUrl);
    }

    [Fact]
    public void TryParseOptions_Pfx_WithPassword_IsValid()
    {
        bool ok = SignCommand.TryParseOptions(
            ["--package", "a.nupkg", "--pfx", "cert.pfx", "--pfx-password", "secret"],
            out SignOptions options,
            out _);

        Assert.True(ok);
        Assert.Equal("cert.pfx", options.PfxPath);
        Assert.Equal("secret", options.PfxPassword);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenAssemblyMissing()
    {
        bool ok = SignCommand.TryParseOptions(["--fingerprint", "ABC"], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_PfxBase64_WithPassword_IsValid()
    {
        bool ok = SignCommand.TryParseOptions(
            ["--package", "a.nupkg", "--pfx-base64", "QkFTRTY0", "--pfx-password", "secret"],
            out SignOptions options,
            out _);

        Assert.True(ok);
        Assert.Equal("QkFTRTY0", options.PfxBase64);
        Assert.Equal("secret", options.PfxPassword);
    }

    [Fact]
    public void TryParseOptions_PfxBase64_WithoutPassword_Fails()
    {
        bool ok = SignCommand.TryParseOptions(["--package", "a.nupkg", "--pfx-base64", "QkFTRTY0"], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenMultipleCertificateSources()
    {
        bool ok = SignCommand.TryParseOptions(
            ["--package", "a.nupkg", "--fingerprint", "ABC", "--pfx-base64", "QkFTRTY0", "--pfx-password", "x"],
            out _,
            out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenNoCertificateSource()
    {
        bool ok = SignCommand.TryParseOptions(["--package", "a.nupkg"], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenPfxWithoutPasswordOrCredential()
    {
        bool ok = SignCommand.TryParseOptions(["--package", "a.nupkg", "--pfx", "cert.pfx"], out _, out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenBothFingerprintAndPfx()
    {
        bool ok = SignCommand.TryParseOptions(
            ["--package", "a.nupkg", "--fingerprint", "ABC", "--pfx", "cert.pfx", "--pfx-password", "x"],
            out _,
            out int exitCode);

        Assert.False(ok);
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_AcceptsCredentialTarget_InsteadOfPassword()
    {
        bool ok = SignCommand.TryParseOptions(
            ["--package", "a.nupkg", "--pfx", "cert.pfx", "--pfx-credential", "MyCert"],
            out SignOptions options,
            out _);

        Assert.True(ok);
        Assert.Equal("MyCert", options.PfxCredentialTarget);
    }

    [Fact]
    public void TryParseOptions_ReadsBothSecrets_FromNamedEnvironmentVariables()   // A-65
    {
        string base64Var = "VELA_TEST_PFX_B64_" + Guid.NewGuid().ToString("N");
        string passwordVar = "VELA_TEST_PFX_PW_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(base64Var, "QkFTRTY0");
        Environment.SetEnvironmentVariable(passwordVar, "s3cret;with;semicolons");
        try
        {
            Assert.True(SignCommand.TryParseOptions(
                ["--package", "a.nupkg", "--pfx-base64-env", base64Var, "--pfx-password-env", passwordVar],
                out SignOptions options,
                out _));

            Assert.Equal("QkFTRTY0", options.PfxBase64);
            Assert.Equal("s3cret;with;semicolons", options.PfxPassword);
            Assert.False(options.SecretsOnCommandLine);
        }
        finally
        {
            Environment.SetEnvironmentVariable(base64Var, null);
            Environment.SetEnvironmentVariable(passwordVar, null);
        }
    }

    [Fact]
    public void TryParseOptions_FlagsLiteralSecretsOnTheCommandLine()
    {
        Assert.True(SignCommand.TryParseOptions(["--package", "a.nupkg", "--pfx", "c.pfx", "--pfx-password", "pw"], out SignOptions options, out _));
        Assert.True(options.SecretsOnCommandLine);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenTheNamedVariableIsUnset()
    {
        string unset = "VELA_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        Assert.False(SignCommand.TryParseOptions(["--package", "a.nupkg", "--pfx", "c.pfx", "--pfx-password-env", unset], out _, out int exitCode));
        Assert.Equal(ExitInvalidArguments, exitCode);
    }

    [Fact]
    public void TryParseOptions_Fails_WhenASecretIsGivenBothWays()
    {
        string variable = "VELA_TEST_PW_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "pw");
        try
        {
            Assert.False(SignCommand.TryParseOptions(
                ["--package", "a.nupkg", "--pfx", "c.pfx", "--pfx-password", "pw", "--pfx-password-env", variable],
                out _,
                out int exitCode));
            Assert.Equal(ExitInvalidArguments, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void WriteOwnerOnlyFile_CreatesANewFile_ReadableOnlyByItsOwner()   // A-65: no write-then-chmod window
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-sign-test-{Guid.NewGuid():N}.pfx");
        try
        {
            SignCommand.WriteOwnerOnlyFile(path, [1, 2, 3]);

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            // CreateNew: an existing file (e.g. one planted to be read back) is never reused.
            Assert.Throws<IOException>(() => SignCommand.WriteOwnerOnlyFile(path, [4]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
