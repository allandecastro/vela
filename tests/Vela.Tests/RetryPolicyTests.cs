using System;
using System.Net;
using System.ServiceModel;
using Vela.Retry;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class RetryPolicyTests
{
    private static FaultException<OrganizationServiceFault> Fault(string message, int errorCode = 0) =>
        new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { Message = message, ErrorCode = errorCode }, message);

    [Fact]
    public void Execute_ReturnsResult_WithoutRetry_OnFirstSuccess()
    {
        int calls = 0;

        int result = RetryPolicy.Execute(() => { calls++; return 42; }, maxAttempts: 3, initialDelayMs: 1);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_RetriesThenSucceeds_OnThrottlingFault()
    {
        int calls = 0;

        int result = RetryPolicy.Execute(
            () =>
            {
                calls++;
                if (calls < 3)
                {
                    throw Fault("The service is throttled, please try again later.");
                }

                return 7;
            },
            maxAttempts: 3,
            initialDelayMs: 1);

        Assert.Equal(7, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_RetriesThrottling_ByErrorCode()
    {
        int calls = 0;

        // -2147015902 is the "number of requests over the limit" service-protection code.
        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw Fault("rejected", errorCode: -2147015902); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_DoesNotRetry_OnNonTransientException()
    {
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw new InvalidOperationException("permanent"); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_DoesNotRetry_OnTimeout_ByDefault()
    {
        // A timed-out write may already have committed server-side, so a blind retry would double-write: not retried
        // unless the caller opts in for an idempotent operation.
        int calls = 0;

        Assert.Throws<TimeoutException>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw new TimeoutException("slow"); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_DoesNotRetry_OnWebException_ByDefault()
    {
        int calls = 0;

        Assert.Throws<WebException>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw new WebException("network blip"); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_RetriesTimeout_WhenOptedIn()
    {
        int calls = 0;

        Assert.Throws<TimeoutException>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw new TimeoutException("slow"); },
                retryOnTimeout: true,
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_Exhausts_AndThrowsLastException_OnPersistentThrottling()
    {
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw Fault("throttled"); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_Throws_OnNullOperation()
    {
        Assert.Throws<ArgumentNullException>(() => RetryPolicy.Execute<int>(null!));
    }

    [Fact]
    public void Execute_Throws_OnNonPositiveMaxAttempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(() => 1, maxAttempts: 0, initialDelayMs: 1));
    }

    [Fact]
    public void Execute_Throws_OnNegativeInitialDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(() => 1, maxAttempts: 3, initialDelayMs: -1));
    }

    [Theory]
    [InlineData("The service is throttled", true)]
    [InlineData("A temporary error occurred", true)]
    [InlineData("Too many requests", true)]
    [InlineData("Request timeout exceeded", false)] // a timeout is not retried by default
    [InlineData("Record does not exist", false)]
    public void Execute_ClassifiesOrganizationServiceFault_ByMessage(string faultMessage, bool expectedRetried)
    {
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw Fault(faultMessage); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(expectedRetried ? 3 : 1, calls);
    }

    [Fact]
    public void Execute_RetriesTimeoutMessagedFault_WhenOptedIn()
    {
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw Fault("Request timeout exceeded"); },
                retryOnTimeout: true,
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }
}
