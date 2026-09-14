using System;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class ApplyConfirmationTests
{
    [Fact]
    public void AutoApprove_applies_without_prompting()
    {
        ApplyDecision d = ApplyConfirmation.Decide(
            autoApprove: true, inputRedirected: true,
            readConfirmation: () => throw new InvalidOperationException("must not prompt when auto-approved"));

        Assert.Equal(ApplyDecision.Apply, d);
    }

    [Fact]
    public void NonInteractive_without_autoApprove_refuses_without_prompting()
    {
        ApplyDecision d = ApplyConfirmation.Decide(
            autoApprove: false, inputRedirected: true,
            readConfirmation: () => throw new InvalidOperationException("must not prompt with no TTY"));

        Assert.Equal(ApplyDecision.RefuseNonInteractive, d);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("  YES  ", true)]
    [InlineData("no", false)]
    [InlineData("y", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Interactive_requires_a_typed_yes(string? answer, bool expectApply)
    {
        // The interactive branch never refuses; a non-"yes" answer cancels.
        ApplyDecision expected = expectApply ? ApplyDecision.Apply : ApplyDecision.Cancel;

        ApplyDecision d = ApplyConfirmation.Decide(
            autoApprove: false, inputRedirected: false, readConfirmation: () => answer);

        Assert.Equal(expected, d);
    }
}
