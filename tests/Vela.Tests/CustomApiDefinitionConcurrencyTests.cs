using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;
using Xunit;

namespace Vela.Tests;

public sealed class CustomApiDefinitionConcurrencyTests
{
    // The Dataverse sandbox caches and concurrently reuses one plugin instance, so the typed Custom API's one-time
    // definition build - which mutates a shared builder field - must be serialized and memoized. Before the fix the
    // typed GetCustomApiDefinition override bypassed the base lock/memoization, so concurrent first executions could
    // null the shared builder mid-build (spurious "can only call inside RegisterCustomApi") or cross-wire builders.
    [Fact]
    public void GetCustomApiDefinition_is_thread_safe_under_concurrent_first_access()
    {
        ConcurrentTypedApi api = new ConcurrentTypedApi();

        const int threadCount = 32;
        CustomApiDefinition?[] results = new CustomApiDefinition?[threadCount];
        ConcurrentBag<Exception> failures = new ConcurrentBag<Exception>();
        using Barrier gate = new Barrier(threadCount);

        Parallel.For(0, threadCount, i =>
        {
            try
            {
                gate.SignalAndWait(); // release all threads together to maximize the race on the first build
                results[i] = api.GetCustomApiDefinition();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);

        // Every caller observes the same cached definition (built exactly once) and it is correct.
        CustomApiDefinition canonical = api.GetCustomApiDefinition();
        Assert.All(results, r => Assert.Same(canonical, r));
        Assert.Equal("zava_Concurrent", canonical.UniqueName);
    }

    public sealed class Req : TypedRequest
    {
        public Req()
        {
            RequestName = "zava_Concurrent";
        }
    }

    public sealed class Resp : TypedResponse
    {
    }

    private sealed class ConcurrentTypedApi : CustomApiBase<Req, Resp>
    {
        public ConcurrentTypedApi()
            : base(string.Empty, string.Empty)
        {
        }

        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_Concurrent");
        }

        protected override Resp Execute(ILocalPluginContext ctx, Req request) => new Resp();
    }
}
