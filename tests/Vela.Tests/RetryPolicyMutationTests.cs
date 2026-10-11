using System;
using System.Diagnostics;
using System.ServiceModel;
using Moq;
using Vela.Context;
using Vela.Retry;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

// Pins the edges of RetryPolicy that the behavioural suite leaves open: argument messages, the boundaries of the
// budget, the transaction guard and timeout classification, and the exponential backoff ceiling.
public sealed class RetryPolicyMutationTests
{
    private const int NumberOfRequestsCode = -2147015902;

    // A context outside any transaction: the policy retries normally.
    private static readonly ILocalPluginContext Ctx = new Mock<ILocalPluginContext>().Object;

    private static FaultException<OrganizationServiceFault> Fault(string message, int errorCode = 0) =>
        new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { Message = message, ErrorCode = errorCode }, message);

    private static FaultException<OrganizationServiceFault> Throttled(TimeSpan retryAfter)
    {
        OrganizationServiceFault detail = new OrganizationServiceFault { Message = "throttled", ErrorCode = NumberOfRequestsCode };
        detail.ErrorDetails["Retry-After"] = retryAfter;
        return new FaultException<OrganizationServiceFault>(detail, "throttled");
    }

    [Fact]
    public void Execute_ExplainsAnOutOfRangeMaxAttempts()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(Ctx, () => 1, maxAttempts: 0, initialDelayMs: 1));

        Assert.Equal("maxAttempts", exception.ParamName);
        Assert.StartsWith("maxAttempts must be at least 1.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ExplainsANegativeInitialDelay()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(Ctx, () => 1, maxAttempts: 3, initialDelayMs: -1));

        Assert.Equal("initialDelayMs", exception.ParamName);
        Assert.StartsWith("initialDelayMs must be non-negative.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ExplainsANegativeBudget()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(Ctx, () => 1, maxElapsed: TimeSpan.FromMilliseconds(-1)));

        Assert.Equal("maxElapsed", exception.ParamName);
        Assert.StartsWith("maxElapsed must be non-negative.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AcceptsAZeroBudget_RunningTheOperationOnceWithoutRetry()
    {
        // A zero budget is valid (non-negative): the first attempt runs, and no retry fits in it.
        Assert.Equal(4, RetryPolicy.Execute(Ctx, () => 4, maxElapsed: TimeSpan.Zero));

        int calls = 0;
        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx,
                () => { calls++; throw Fault("throttled", NumberOfRequestsCode); },
                maxAttempts: 3,
                initialDelayMs: 1,
                maxElapsed: TimeSpan.Zero));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_DoesNotWaitAfterTheLastAttempt()
    {
        // The final failure propagates at once: waiting the Retry-After when no attempt follows only burns the budget.
        Stopwatch watch = Stopwatch.StartNew();

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx, () => throw Throttled(TimeSpan.FromMilliseconds(1500)), maxAttempts: 1, initialDelayMs: 1));

        Assert.True(watch.ElapsedMilliseconds < 1000, $"waited {watch.ElapsedMilliseconds} ms after the last attempt");
    }

    [Fact]
    public void Execute_InsideATransaction_WithASingleAttempt_DoesNotTrace()
    {
        // The transaction note is for a retry the policy suppresses; a caller that asked for one attempt loses nothing.
        Mock<ILocalPluginContext> mock = new Mock<ILocalPluginContext>();
        mock.SetupGet(c => c.IsInTransaction).Returns(true);

        Assert.Equal(3, RetryPolicy.Execute(mock.Object, () => 3, maxAttempts: 1, initialDelayMs: 1));

        mock.Verify(c => c.Trace(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void Execute_RetriesAFaultWhoseMessageStartsWithTimeout_WhenOptedIn()
    {
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx,
                () => { calls++; throw Fault("Timeout expired before the operation completed"); },
                retryOnTimeout: true,
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_DoesNotRetryAFaultWithoutTimeoutInItsMessage_EvenWhenOptedIn()
    {
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx,
                () => { calls++; throw Fault("Record does not exist"); },
                retryOnTimeout: true,
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_DoesNotRetryANonTimeoutException_EvenWhenOptedIn()
    {
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() =>
            RetryPolicy.Execute<int>(Ctx,
                () => { calls++; throw new InvalidOperationException("permanent"); },
                retryOnTimeout: true,
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_TreatsANegativeRetryAfter_AsAnImmediateRetry()
    {
        // A Retry-After already in the past means "now": the retry is made without waiting.
        int calls = 0;

        int result = RetryPolicy.Execute(Ctx,
            () => { calls++; if (calls == 1) { throw Throttled(TimeSpan.FromSeconds(-5)); } return 8; },
            maxAttempts: 2,
            initialDelayMs: 0);

        Assert.Equal(8, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Execute_FirstBackoff_NeverExceedsTheInitialDelay()
    {
        // The first wait is a random point in [0, initialDelayMs]. With a budget 50 ms above that ceiling the first
        // retry always fits; a larger exponent (a wait up to 4x the initial delay) would overrun it most of the time.
        for (int run = 0; run < 10; run++)
        {
            int calls = 0;

            int result = RetryPolicy.Execute(Ctx,
                () => { calls++; if (calls == 1) { throw Fault("throttled", NumberOfRequestsCode); } return run; },
                maxAttempts: 2,
                initialDelayMs: 100,
                maxElapsed: TimeSpan.FromMilliseconds(150));

            Assert.Equal(run, result);
            Assert.Equal(2, calls);
        }
    }
}
