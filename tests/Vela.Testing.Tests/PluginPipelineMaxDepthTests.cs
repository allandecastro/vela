using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // PluginPipeline.WithMaxDepth (A-82): the cascade-depth cap that stops runaway recursion between plugins.
    public sealed class PluginPipelineMaxDepthTests
    {
        // A task whose subject is the number of tasks left in the chain: each create of "n" (n > 1) creates "n - 1",
        // one pipeline depth further down.
        public sealed class ChainTaskCreates : EntityPluginBase<FakeTask>
        {
            public ChainTaskCreates(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PostOperation().Sync()
                    .FilterOn(t => t.Subject)
                    .Handle(OnCreated);

            private void OnCreated(ICreatePostContext<FakeTask> ctx)
            {
                int left = int.Parse(ctx.Request.Target.Subject!, CultureInfo.InvariantCulture);
                if (left > 1)
                {
                    ctx.SystemService.Create(new FakeTask { Subject = (left - 1).ToString(CultureInfo.InvariantCulture) });
                }
            }
        }

        private static PluginPipeline Chain(int? maxDepth)
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ChainTaskCreates>();
            return maxDepth is int cap ? pipeline.WithMaxDepth(cap) : pipeline;
        }

        [Fact]
        public void A_cascade_that_reaches_exactly_the_cap_completes()
        {
            PluginPipeline pipeline = Chain(maxDepth: 3);

            pipeline.Service.Create(new FakeTask { Subject = "3" });

            Assert.Equal(new[] { "1", "2", "3" }, pipeline.GetAll("task").Select(t => t.GetAttributeValue<string>("subject")).OrderBy(s => s).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, pipeline.ExecutionLog.Select(e => e.Depth).ToArray());
        }

        [Fact]
        public void A_cascade_one_level_past_the_cap_fails_and_rolls_back()
        {
            PluginPipeline pipeline = Chain(maxDepth: 3);

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Create(new FakeTask { Subject = "4" }));

            Assert.Contains("stopped at depth 4 (cap 3)", ex.Message);
            // The failed operation rolls back the whole cascade, like the platform transaction.
            Assert.Empty(pipeline.GetAll("task"));
        }

        [Fact]
        public void The_default_cap_allows_a_deeper_cascade()
        {
            PluginPipeline pipeline = Chain(maxDepth: null);

            pipeline.Service.Create(new FakeTask { Subject = "4" });

            Assert.Equal(4, pipeline.GetAll("task").Count);
        }

        [Fact]
        public void A_cap_below_one_is_clamped_to_one()
        {
            PluginPipeline pipeline = Chain(maxDepth: 0);

            // Depth 1 is allowed (a single create whose plugin does not cascade)...
            pipeline.Service.Create(new FakeTask { Subject = "1" });
            Assert.Single(pipeline.GetAll("task"));

            // ...but the first cascaded write (depth 2) trips the cap.
            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Create(new FakeTask { Subject = "2" }));
            Assert.Contains("stopped at depth 2 (cap 1)", ex.Message);
        }
    }
}
