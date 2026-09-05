using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

public sealed class StepDefinitionConcurrencyTests
{
    // The Dataverse sandbox caches and concurrently reuses a single plugin instance, so the one-time lazy
    // build in GetStepDefinitions() (which mutates shared fields under a lock) must be thread-safe: many
    // threads hitting it on the very first execution must not race or corrupt the step list.
    [Fact]
    public void GetStepDefinitions_is_thread_safe_under_concurrent_first_access()
    {
        ConcurrentMultiStepPlugin plugin = new ConcurrentMultiStepPlugin();

        const int threadCount = 32;
        StepDefinition[]?[] results = new StepDefinition[threadCount][];
        ConcurrentBag<Exception> failures = new ConcurrentBag<Exception>();
        using Barrier gate = new Barrier(threadCount);

        Parallel.For(0, threadCount, i =>
        {
            try
            {
                gate.SignalAndWait(); // release all threads together to maximize the race on the first build
                results[i] = plugin.GetStepDefinitions();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);

        // Every caller observes the same cached array (built exactly once)...
        StepDefinition[] canonical = plugin.GetStepDefinitions();
        Assert.All(results, r => Assert.Same(canonical, r));

        // ...and the build is correct and stable: both steps, with distinct auto step keys.
        Assert.Equal(2, canonical.Length);
        Assert.Equal(2, canonical.Select(s => s.StepKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(canonical, s => Assert.False(string.IsNullOrWhiteSpace(s.StepKey)));
    }

    private sealed class ConcurrentMultiStepPlugin : EntityPluginBase<TestAccount>
    {
        public ConcurrentMultiStepPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name).Handler(OnCreate);
            RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name).Handler(OnUpdate);
        }

        private void OnCreate(ILocalPluginContext ctx)
        {
        }

        private void OnUpdate(ILocalPluginContext ctx)
        {
        }
    }
}
