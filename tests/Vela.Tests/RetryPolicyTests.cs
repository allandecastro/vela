using System;
using System.Net;
using System.ServiceModel;
using Vela.Retry;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public void Execute_ReturnsResult_WithoutRetry_OnFirstSuccess()
    {
        int calls = 0;

        int result = RetryPolicy.Execute(() => { calls++; return 42; }, maxAttempts: 3, initialDelayMs: 1);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_RetriesThenSucceeds_OnTransientException()
    {
        int calls = 0;

        int result = RetryPolicy.Execute(
            () =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new TimeoutException("transient");
                }

                return 7;
            },
            maxAttempts: 3,
            initialDelayMs: 1);

        Assert.Equal(7, result);
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
    public void Execute_Exhausts_AndThrowsLastException_OnPersistentTransient()
    {
        int calls = 0;

        Assert.Throws<TimeoutException>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw new TimeoutException("always"); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_Throws_OnNullOperation()
    {
        Assert.Throws<ArgumentNullException>(() => RetryPolicy.Execute<int>(null!));
    }

    [Theory]
    [InlineData("Request timeout exceeded", true)]
    [InlineData("The service is throttled", true)]
    [InlineData("A temporary error occurred", true)]
    [InlineData("Record does not exist", false)]
    public void Execute_TreatsOrganizationServiceFault_AsTransient_ByMessage(string faultMessage, bool expectedTransient)
    {
        int calls = 0;
        FaultException<OrganizationServiceFault> Fault() =>
            new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = faultMessage }, faultMessage);

        Action act = () => RetryPolicy.Execute<int>(
            () => { calls++; throw Fault(); },
            maxAttempts: 3,
            initialDelayMs: 1);

        Assert.Throws<FaultException<OrganizationServiceFault>>(act);
        Assert.Equal(expectedTransient ? 3 : 1, calls);
    }

    [Fact]
    public void Execute_TreatsWebException_AsTransient()
    {
        int calls = 0;

        Assert.Throws<WebException>(() =>
            RetryPolicy.Execute<int>(
                () => { calls++; throw new WebException("network blip"); },
                maxAttempts: 2,
                initialDelayMs: 1));

        Assert.Equal(2, calls);
    }
}
