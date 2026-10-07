using System;
using System.Net;
using System.ServiceModel;
using System.Diagnostics;
using System.Linq;
using Moq;
using Vela.Context;
using Vela.Retry;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class RetryPolicyTests
{
    // A context outside any transaction: the policy retries normally.
    private static readonly ILocalPluginContext Ctx = new Moq.Mock<ILocalPluginContext>().Object;

    private static FaultException<OrganizationServiceFault> Fault(string message, int errorCode = 0) =>
        new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { Message = message, ErrorCode = errorCode }, message);

    [Fact]
    public void Execute_ReturnsResult_WithoutRetry_OnFirstSuccess()
    {
        int calls = 0;

        int result = RetryPolicy.Execute(Ctx, () => { calls++; return 42; }, maxAttempts: 3, initialDelayMs: 1);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Execute_RetriesThenSucceeds_OnThrottlingFault()
    {
        int calls = 0;

        int result = RetryPolicy.Execute(Ctx, 
            () =>
            {
                calls++;
                if (calls < 3)
                {
                    throw Fault("The service is throttled, please try again later.", errorCode: -2147015898);
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
            RetryPolicy.Execute<int>(Ctx, 
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
            RetryPolicy.Execute<int>(Ctx, 
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
            RetryPolicy.Execute<int>(Ctx, 
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
            RetryPolicy.Execute<int>(Ctx, 
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
            RetryPolicy.Execute<int>(Ctx, 
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
            RetryPolicy.Execute<int>(Ctx, 
                () => { calls++; throw Fault("throttled", errorCode: -2147015903); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_Throws_OnNullOperation()
    {
        Assert.Throws<ArgumentNullException>(() => RetryPolicy.Execute<int>(Ctx, null!));
    }

    [Fact]
    public void Execute_RetriesThrottling_WithZeroInitialDelay()
    {
        // initialDelayMs 0 exercises the zero-backoff path; the throttling fault is still retried to exhaustion.
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx, 
                () => { calls++; throw Fault("throttled", errorCode: -2147015903); },
                maxAttempts: 3,
                initialDelayMs: 0));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_Throws_OnNonPositiveMaxAttempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(Ctx, () => 1, maxAttempts: 0, initialDelayMs: 1));
    }

    [Fact]
    public void Execute_Throws_OnNegativeInitialDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Execute(Ctx, () => 1, maxAttempts: 3, initialDelayMs: -1));
    }

    [Theory]
    // Classification is by service-protection error code only: a message that merely sounds transient is not
    // replayed (a business error saying "temporarily unavailable" must surface at once).
    [InlineData("The service is throttled", false)]
    [InlineData("A temporary error occurred", false)]
    [InlineData("Too many requests", false)]
    [InlineData("Request timeout exceeded", false)] // a timeout is not retried by default
    [InlineData("Record does not exist", false)]
    public void Execute_DoesNotClassifyOrganizationServiceFault_ByMessage(string faultMessage, bool expectedRetried)
    {
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx, 
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
            RetryPolicy.Execute<int>(Ctx, 
                () => { calls++; throw Fault("Request timeout exceeded"); },
                retryOnTimeout: true,
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    // ---- Retry-After ---------------------------------------------------------------------------------

    private static FaultException<OrganizationServiceFault> Throttled(object retryAfter)
    {
        OrganizationServiceFault detail = new OrganizationServiceFault { Message = "throttled", ErrorCode = -2147015902 };
        detail.ErrorDetails["Retry-After"] = retryAfter;
        return new FaultException<OrganizationServiceFault>(detail, "throttled");
    }

    [Fact]
    public void Execute_WaitsTheFaultsRetryAfter_BeforeRetrying()
    {
        int calls = 0;
        Stopwatch watch = Stopwatch.StartNew();

        int result = RetryPolicy.Execute(Ctx, 
            () =>
            {
                calls++;
                if (calls == 1)
                {
                    throw Throttled(TimeSpan.FromMilliseconds(300));
                }

                return 5;
            },
            maxAttempts: 2,
            initialDelayMs: 0); // jitter alone would not wait at all

        Assert.Equal(5, result);
        Assert.Equal(2, calls);
        Assert.True(watch.ElapsedMilliseconds >= 280, $"waited only {watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("seconds")]
    [InlineData("text")]
    public void Execute_ReadsARetryAfterGivenAsSecondsOrText(string shape)
    {
        object retryAfter = shape == "seconds" ? (object)0 : "00:00:00.010";
        int calls = 0;

        int result = RetryPolicy.Execute(Ctx, 
            () => { calls++; if (calls == 1) { throw Throttled(retryAfter); } return 1; },
            maxAttempts: 2,
            initialDelayMs: 0);

        Assert.Equal(1, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Execute_DoesNotRetry_WhenRetryAfterExceedsTheCap()
    {
        int calls = 0;
        Stopwatch watch = Stopwatch.StartNew();

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(Ctx, 
                () => { calls++; throw Throttled(RetryPolicy.MaxDelay + TimeSpan.FromMinutes(1)); },
                maxAttempts: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls); // retrying earlier than asked would only be rejected again
        Assert.True(watch.ElapsedMilliseconds < 5000);
    }

    // ---- Transactions --------------------------------------------------------------------------------

    private static ILocalPluginContext ContextInTransaction(bool inTransaction, Mock<ILocalPluginContext>? mock = null)
    {
        mock ??= new Mock<ILocalPluginContext>();
        mock.SetupGet(c => c.IsInTransaction).Returns(inTransaction);
        return mock.Object;
    }

    [Fact]
    public void Execute_WithContext_RunsOnce_InsideATransaction()
    {
        Mock<ILocalPluginContext> mock = new Mock<ILocalPluginContext>();
        ILocalPluginContext ctx = ContextInTransaction(true, mock);
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(ctx, () => { calls++; throw Fault("throttled", errorCode: -2147015902); }, initialDelayMs: 1));

        Assert.Equal(1, calls); // the failed call aborted the transaction: a replay cannot succeed
        mock.Verify(c => c.Trace(It.Is<string>(m => m.Contains("transaction")), It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public void Execute_WithContext_Retries_OutsideATransaction()
    {
        ILocalPluginContext ctx = ContextInTransaction(false);
        int calls = 0;

        Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            RetryPolicy.Execute<int>(ctx, () => { calls++; throw Fault("throttled", errorCode: -2147015902); }, initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Execute_WithContext_RejectsANullContext()
    {
        Assert.Throws<ArgumentNullException>(() => RetryPolicy.Execute(null!, () => 1));
    }

    [Fact]
    public void Every_public_retry_entry_point_takes_the_plugin_context()
    {
        // The context-free overloads could not see a transaction and replayed calls that could never succeed.
        System.Reflection.MethodInfo[] executes = typeof(RetryPolicy).GetMethods()
            .Where(m => m.Name == nameof(RetryPolicy.Execute)).ToArray();
        Assert.NotEmpty(executes);
        Assert.All(executes, m => Assert.Equal(typeof(ILocalPluginContext), m.GetParameters()[0].ParameterType));

        System.Reflection.MethodInfo[] helpers = typeof(Vela.Plugin.PluginBase)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == "ExecuteWithRetry" || m.Name == "ExecuteIdempotentWithRetry").ToArray();
        Assert.Equal(2, helpers.Length);
        Assert.All(helpers, m => Assert.Equal(typeof(ILocalPluginContext), m.GetParameters()[0].ParameterType));
    }
}
