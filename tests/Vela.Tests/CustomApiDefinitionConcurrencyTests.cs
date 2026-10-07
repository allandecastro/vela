using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;
using Vela.Tests.TestSupport;
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
        // Dedicated threads released together (maximizes the race on the first build); fails rather than hangs.
        IReadOnlyCollection<Exception> failures = ConcurrentStart.Run(threadCount, i => results[i] = api.GetCustomApiDefinition(), TimeSpan.FromSeconds(30));

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
