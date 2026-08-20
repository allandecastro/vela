using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// Boundary coverage for LooksSolutionPrefixed, which mutation testing flagged as under-asserted: the
// "underscore position" guard and every clause of the "2-8 letters/digits, letter-first" prefix rule.
// Each case pins one boundary so a flipped comparison or a swapped &&/|| / All-to-Any changes the result.
public sealed class DataverseRegistrationApplyMutationTests
{
    [Theory]
    // Valid: a real customization prefix followed by "_".
    [InlineData("ab_cd", true)]           // underscore at index 2, minimum 2-char prefix
    [InlineData("abcdefgh_x", true)]      // prefix at the 8-char upper bound
    [InlineData("zava_account", true)]    // typical case
    [InlineData("a1b2_name", true)]       // digits allowed after a leading letter
    // Invalid: underscore too early / absent.
    [InlineData("a_bc", false)]           // underscore at index 1 (< 2)
    [InlineData("_abc", false)]           // underscore at index 0
    [InlineData("noprefix", false)]       // no underscore at all
    // Invalid: prefix fails the shape rule.
    [InlineData("abcdefghi_x", false)]    // 9-char prefix, past the upper bound
    [InlineData("1ab_cd", false)]         // prefix does not start with a letter
    [InlineData("a-b_cd", false)]         // prefix contains a non-alphanumeric character
    public void LooksSolutionPrefixed_AppliesTheTwoToEightLetterFirstRule(string packageName, bool expected)
    {
        Assert.Equal(expected, DataverseRegistration.LooksSolutionPrefixed(packageName));
    }
}
