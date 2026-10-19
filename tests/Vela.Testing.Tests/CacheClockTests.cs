using System;
using System.Linq;
using Vela.Caching;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Each harness has a plug-in cache of its own, expiring on its clock: a test moves time past a TTL instead of
    // sleeping, and never sees another test's entries.
    public sealed class CacheClockTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

        // Caches the name of the record that missed for 5 minutes and writes the cached value onto the record, so a
        // test sees which execution loaded it.
        private sealed class CachingPlugin : EntityPluginBase<FakeAccount>
        {
            public CachingPlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(e => e.Name).Handle(OnCreating);

            private void OnCreating(ICreatePreContext<FakeAccount> ctx)
            {
                string name = ctx.Request.Target.Name;
                ctx.Request.Target["description"] = ctx.GetRequiredService<IPluginCacheService>()
                    .GetOrCreate("settings", TimeSpan.FromMinutes(5), () => name);
            }
        }

        private static string Description(PluginPipeline pipeline, Guid id)
            => pipeline.GetAll("account").Single(e => e.Id == id).GetAttributeValue<string>("description");

        [Fact]
        public void PluginPipeline_cache_expires_on_the_pipeline_clock()
        {
            DateTime now = Start;
            PluginPipeline pipeline = PluginPipeline.Empty().WithClock(() => now).Register<CachingPlugin>();

            Guid first = pipeline.Service.Create(new FakeAccount { Name = "a" });
            now = Start.AddMinutes(4);
            Guid second = pipeline.Service.Create(new FakeAccount { Name = "b" });
            now = Start.AddMinutes(6);
            Guid third = pipeline.Service.Create(new FakeAccount { Name = "c" });

            Assert.Equal("a", Description(pipeline, first));
            Assert.Equal("a", Description(pipeline, second));   // within the TTL: the cached value
            Assert.Equal("c", Description(pipeline, third));    // past it: the factory ran again
        }

        [Fact]
        public void PluginPipeline_cache_is_not_shared_between_pipelines()
        {
            PluginPipeline one = PluginPipeline.Empty().Register<CachingPlugin>();
            PluginPipeline two = PluginPipeline.Empty().Register<CachingPlugin>();

            Guid a = one.Service.Create(new FakeAccount { Name = "a" });
            Guid b = two.Service.Create(new FakeAccount { Name = "b" });

            Assert.Equal("a", Description(one, a));
            Assert.Equal("b", Description(two, b));   // a cache of its own: not the other pipeline's "a"
        }

        [Fact]
        public void PluginContext_cache_expires_on_the_builder_clock_across_executions()
        {
            DateTime now = Start;
            CachingPlugin plugin = new CachingPlugin(string.Empty, string.Empty);
            FakeAccount target = new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" };
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .OnCreate().Stage(PluginStages.PreOperation).Sync()
                .WithTarget(target)
                .WithClock(() => now);

            builder.Execute(plugin);
            Assert.Equal("Zava", target.GetAttributeValue<string>("description"));

            target.Name = "Contoso";
            now = Start.AddMinutes(4);
            builder.Execute(plugin);
            Assert.Equal("Zava", target.GetAttributeValue<string>("description"));     // within the TTL

            now = Start.AddMinutes(6);
            builder.Execute(plugin);
            Assert.Equal("Contoso", target.GetAttributeValue<string>("description"));  // past it
        }

        [Fact]
        public void PluginContext_cache_is_not_shared_between_builders()
        {
            CachingPlugin plugin = new CachingPlugin(string.Empty, string.Empty);
            FakeAccount first = new FakeAccount { Id = Guid.NewGuid(), Name = "a" };
            FakeAccount second = new FakeAccount { Id = Guid.NewGuid(), Name = "b" };

            PluginContext.For<FakeAccount>().OnCreate().Stage(PluginStages.PreOperation).Sync().WithTarget(first).Execute(plugin);
            PluginContext.For<FakeAccount>().OnCreate().Stage(PluginStages.PreOperation).Sync().WithTarget(second).Execute(plugin);

            Assert.Equal("a", first.GetAttributeValue<string>("description"));
            Assert.Equal("b", second.GetAttributeValue<string>("description"));
        }

        [Fact]
        public void PluginContext_WithClock_rejects_null()
        {
            Assert.Throws<ArgumentNullException>(() => PluginContext.For<FakeAccount>().WithClock(null!));
        }
    }
}
