using System;
using System.IO;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// #307 M1: a connection string embeds ClientSecret=... / Password=..., and the SDK (or client.LastError) can echo
// it verbatim in an exception. OutputWriter.Fail / FailWithException must scrub those before they reach stderr / CI
// logs. RedactSecrets is pure; the two Fail paths write to the process-global Console.Error, so those cases run in
// the serialized ConsoleRedirect collection.
public sealed class OutputWriterRedactionTests
{
    [Theory]
    [InlineData("AuthType=ClientSecret;Url=https://x;ClientId=abc;ClientSecret=SuperSecret123;", "SuperSecret123")]
    [InlineData("Password=hunter2;User=admin", "hunter2")]
    [InlineData("connection failed: secret=topsecret", "topsecret")]
    public void RedactSecrets_removes_the_credential_value(string text, string secret)
    {
        string redacted = OutputWriter.RedactSecrets(text);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("=***", redacted);
    }

    // A quoted value may contain ';' (and a doubled quote as its escape): the whole quoted value is masked, and the
    // keys after it survive. An unterminated quote masks the rest of the text rather than leaking a suffix.
    [Theory]
    [InlineData("ClientId=abc;ClientSecret=\"abc;def\";Url=https://x", "ClientId=abc;ClientSecret=***;Url=https://x")]
    [InlineData("ClientId=abc;ClientSecret='abc;def';Url=https://x", "ClientId=abc;ClientSecret=***;Url=https://x")]
    [InlineData("Password=\"a\"\"b;c\";User=admin", "Password=***;User=admin")]
    [InlineData("Password='a''b;c';User=admin", "Password=***;User=admin")]
    [InlineData("failed: ClientSecret=\"abc;def", "failed: ClientSecret=***")]
    [InlineData("failed: ClientSecret='abc;def", "failed: ClientSecret=***")]
    public void RedactSecrets_masks_the_whole_quoted_value(string text, string expected)
    {
        Assert.Equal(expected, OutputWriter.RedactSecrets(text));
    }

    [Fact]
    public void RedactSecrets_keeps_non_secret_text_and_other_key_values()
    {
        const string text = "Url=https://org.crm.dynamics.com;ClientId=00000000-0000-0000-0000-000000000000";
        Assert.Equal(text, OutputWriter.RedactSecrets(text));
    }

    [Fact]
    public void RedactSecrets_handles_null_and_empty()
    {
        Assert.Equal(string.Empty, OutputWriter.RedactSecrets(null));
        Assert.Equal(string.Empty, OutputWriter.RedactSecrets(string.Empty));
    }
}

[Collection("ConsoleRedirect")]
public sealed class OutputWriterRedactionStderrTests
{
    [Fact]
    public void Fail_scrubs_the_client_secret_from_stderr()
    {
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            Console.SetError(capture);
            OutputWriter.Fail("connect failed: AuthType=ClientSecret;ClientSecret=SuperSecret123;", 1, includeUsage: false);
        }
        finally
        {
            Console.SetError(original);
        }

        string err = capture.ToString();
        Assert.DoesNotContain("SuperSecret123", err);
        Assert.Contains("ClientSecret=***", err);
    }

    [Fact]
    public void FailWithException_scrubs_the_secret_even_under_verbose()
    {
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            Console.SetError(capture);
            Exception ex = new InvalidOperationException("SDK error for ClientSecret=SuperSecret123;Url=https://x");
            OutputWriter.FailWithException(ex, 1, verbose: true);
        }
        finally
        {
            Console.SetError(original);
        }

        string err = capture.ToString();
        Assert.DoesNotContain("SuperSecret123", err);   // neither the message line nor the verbose ToString() dump
        Assert.Contains("ClientSecret=***", err);
    }
}
