using System;
using System.Collections.Generic;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Vela;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Querying;
using Vela.Retry;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Final mop-up of the small Vela.Plugins classes: RetryPolicy transient handling, the Register
// entry points, the TypedFilterBuilder null guards, and a couple of EntityAttributeExtensions edges.
public sealed class SmallClassesCoverageTests
{
    // A context outside any transaction: the policy retries normally.
    private static readonly ILocalPluginContext Ctx = new Moq.Mock<ILocalPluginContext>().Object;

    // ---- RetryPolicy ---------------------------------------------------------------------------------

    [Fact]
    public void Retry_returns_on_success_and_rejects_a_null_operation()
    {
        Assert.Throws<ArgumentNullException>(() => RetryPolicy.Execute<int>(Ctx, null!));
        Assert.Equal(42, RetryPolicy.Execute(Ctx, () => 42));
    }

    [Fact]
    public void Retry_recovers_from_a_transient_fault_then_succeeds()
    {
        int calls = 0;
        int result = RetryPolicy.Execute(Ctx, () =>
        {
            calls++;
            if (calls == 1)
            {
                throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = "request was throttled", ErrorCode = -2147015902 });
            }

            return 7;
        }, maxAttempts: 3, initialDelayMs: 1);

        Assert.Equal(7, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Retry_rethrows_a_non_transient_fault_immediately()
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => RetryPolicy.Execute<int>(Ctx, () =>
        {
            calls++;
            throw new InvalidOperationException("boom"); // not transient
        }, maxAttempts: 3, initialDelayMs: 1));
        Assert.Equal(1, calls); // not retried
    }

    [Fact]
    public void Retry_exhausts_attempts_on_a_persistent_transient_fault()
    {
        int calls = 0;
        Assert.Throws<FaultException<OrganizationServiceFault>>(() => RetryPolicy.Execute<int>(Ctx, () =>
        {
            calls++;
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { Message = "request was throttled", ErrorCode = -2147015902 }); // transient, never recovers
        }, maxAttempts: 2, initialDelayMs: 1));
        Assert.Equal(2, calls);
    }

    // ---- Register entry points -----------------------------------------------------------------------

    [Fact]
    public void Register_entry_points()
    {
        // The lightweight entry point is a fresh, independent builder each call.
        ICustomApiBuilder first = Register.RegisterCustomApi();
        Assert.IsType<CustomApiLightBuilder>(first);
        Assert.NotSame(first, Register.RegisterCustomApi());
        Assert.Equal("zava_Ping", first.UniqueName("zava_Ping").Handler(_ => { }).Build().UniqueName);
    }

    // ---- TypedFilterBuilder null guards --------------------------------------------------------------

    [Fact]
    public void Typed_filter_builder_nested_configurators_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => Query.For<TestAccount>().WhereAll(f => f.All(null!)));
        Assert.Throws<ArgumentNullException>(() => Query.For<TestAccount>().WhereAll(f => f.Any(null!)));
    }

    // ---- EntityAttributeExtensions edges -------------------------------------------------------------

    [Fact]
    public void Attribute_helpers_cover_absent_and_present_mixes()
    {
        TestAccount account = new TestAccount { Name = "Acme" };

        // Contains(params): first present, second absent -> false (the early-out branch).
        Assert.False(account.Contains(a => a.Name, a => a.AccountId));
        // ContainsAny: first absent, second present -> true after scanning past the first.
        Assert.True(account.ContainsAny(a => a.AccountId, a => a.Name));
        // ContainsAny: none present -> false.
        Assert.False(account.ContainsAny(a => a.AccountId, a => a.Customers));
    }
}
