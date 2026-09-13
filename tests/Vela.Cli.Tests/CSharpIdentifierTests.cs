using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// Covers the shared identifier/keyword/numeric helpers behind the message & Custom API generators (A16, A17).
public class CSharpIdentifierTests
{
    [Theory]
    [InlineData(-1, "n1")]        // A16: a negative option value must not yield "_-1"
    [InlineData(-42, "n42")]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    public void NumericToken_negatives_use_n_prefix(int value, string expected) =>
        Assert.Equal(expected, CSharpIdentifier.NumericToken(value));

    [Theory]
    [InlineData("object", "@object")]     // A17: C# keyword escaped
    [InlineData("params", "@params")]
    [InlineData("AccountId", "AccountId")]
    [InlineData("my param", "my_param")]  // invalid char sanitized
    [InlineData("2fa", "_2fa")]           // leading digit
    [InlineData("", "_")]
    public void Sanitize_produces_valid_identifiers(string input, string expected) =>
        Assert.Equal(expected, CSharpIdentifier.Sanitize(input));

    [Fact]
    public void Member_keeps_keyless_caller_name_for_a_plain_keyword()
    {
        // @object's [CallerMemberName] is "object", so no explicit key is needed.
        (string member, string key) = CSharpIdentifier.Member("object");
        Assert.Equal("@object", member);
        Assert.Equal("", key);
    }

    [Fact]
    public void Member_emits_explicit_key_when_the_name_had_to_be_sanitized()
    {
        (string member, string key) = CSharpIdentifier.Member("my.param");
        Assert.Equal("my_param", member);
        Assert.Equal("\"my.param\"", key);
    }
}
