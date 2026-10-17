using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.DI;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // WithService replaces a service the plugin registers itself, on both harnesses.
    public sealed class ServiceOverrideTests
    {
        public interface IGreeter
        {
            string Greet(string name);
        }

        private sealed class PrefixGreeter : IGreeter
        {
            private readonly string _prefix;

            public PrefixGreeter(string prefix) => _prefix = prefix;

            public List<string> Greeted { get; } = new List<string>();

            public string Greet(string name)
            {
                Greeted.Add(name);
                return _prefix + ":" + name;
            }
        }

        // Writes the greeting onto the account in PreOperation, then creates a task whose own plugin greets too.
        private sealed class GreetingAccountPlugin : EntityPluginBase<FakeAccount>
        {
            public GreetingAccountPlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
                => services.AddScoped<IGreeter>(_ => new PrefixGreeter("plugin"));

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnCreate().PreOperation().Sync().FilterOn(e => e.Name).Handle(OnCreating);
                RegisterStep().OnCreate().PostOperation().Sync().FilterOn(e => e.Name).Handle(OnCreated);
            }

            private void OnCreating(ICreatePreContext<FakeAccount> ctx)
                => ctx.Request.Target["description"] = ctx.GetRequiredService<IGreeter>().Greet(ctx.Request.Target.Name);

            private void OnCreated(ICreatePostContext<FakeAccount> ctx)
                => ctx.Service.Create(new FakeTask { Subject = "follow-up" });
        }

        private sealed class GreetingTaskPlugin : EntityPluginBase<FakeTask>
        {
            public GreetingTaskPlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
                => services.AddScoped<IGreeter>(_ => new PrefixGreeter("task-plugin"));

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(e => e.Subject).Handle(OnCreating);

            private void OnCreating(ICreatePreContext<FakeTask> ctx)
                => ctx.Request.Target["description"] = ctx.GetRequiredService<IGreeter>().Greet(ctx.Request.Target.Subject);
        }

        // Calls an HTTP endpoint through the HttpClient it registers and stores the response body.
        private sealed class ScoringPlugin : EntityPluginBase<FakeAccount>
        {
            public ScoringPlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
                => services.AddScoped(_ => new HttpClient());

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(e => e.Name).Handle(OnCreating);

            private void OnCreating(ICreatePreContext<FakeAccount> ctx)
            {
                HttpClient http = ctx.GetRequiredService<HttpClient>();
                string score = http.GetStringAsync("https://scoring.example/score?name=" + ctx.Request.Target.Name)
                    .GetAwaiter().GetResult();
                ctx.Request.Target["description"] = score;
            }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42") });
            }
        }

        private static string Description(PluginPipeline pipeline, string table)
            => pipeline.GetAll(table).Single().GetAttributeValue<string>("description");

        [Fact]
        public void PluginContext_without_WithService_uses_the_plugins_own_registration()
        {
            FakeAccount target = new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" };

            PluginContext.For<FakeAccount>()
                .OnCreate().Stage(PluginStages.PreOperation).Sync()
                .WithTarget(target)
                .Execute(new GreetingAccountPlugin(string.Empty, string.Empty));

            Assert.Equal("plugin:Zava", target.GetAttributeValue<string>("description"));
        }

        [Fact]
        public void PluginContext_WithService_replaces_the_plugins_registration()
        {
            FakeAccount target = new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" };
            PrefixGreeter fake = new PrefixGreeter("test");

            PluginContext.For<FakeAccount>()
                .OnCreate().Stage(PluginStages.PreOperation).Sync()
                .WithTarget(target)
                .WithService<IGreeter>(fake)
                .Execute(new GreetingAccountPlugin(string.Empty, string.Empty));

            Assert.Equal("test:Zava", target.GetAttributeValue<string>("description"));
            Assert.Equal(new[] { "Zava" }, fake.Greeted);
        }

        [Fact]
        public void PluginContext_WithService_called_twice_for_a_type_keeps_the_last_instance()
        {
            FakeAccount target = new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" };

            PluginContext.For<FakeAccount>()
                .OnCreate().Stage(PluginStages.PreOperation).Sync()
                .WithTarget(target)
                .WithService<IGreeter>(new PrefixGreeter("first"))
                .WithService<IGreeter>(new PrefixGreeter("second"))
                .Execute(new GreetingAccountPlugin(string.Empty, string.Empty));

            Assert.Equal("second:Zava", target.GetAttributeValue<string>("description"));
        }

        [Fact]
        public void WithService_rejects_a_null_instance()
        {
            Assert.Throws<ArgumentNullException>(() => PluginContext.For<FakeAccount>().WithService<IGreeter>(null!));
            Assert.Throws<ArgumentNullException>(() => PluginPipeline.Empty().WithService<IGreeter>(null!));
        }

        [Fact]
        public void Pipeline_without_WithService_uses_each_plugins_own_registration()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<GreetingAccountPlugin>().Register<GreetingTaskPlugin>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal("plugin:Zava", Description(pipeline, "account"));
            Assert.Equal("task-plugin:follow-up", Description(pipeline, "task"));
        }

        [Fact]
        public void Pipeline_WithService_reaches_every_plugin_including_nested_executions()
        {
            PrefixGreeter fake = new PrefixGreeter("test");
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<GreetingAccountPlugin>()
                .Register<GreetingTaskPlugin>()
                .WithService<IGreeter>(fake);

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal("test:Zava", Description(pipeline, "account"));
            Assert.Equal("test:follow-up", Description(pipeline, "task")); // the task step runs nested, at depth 2
            Assert.Equal(new[] { "Zava", "follow-up" }, fake.Greeted); // the same instance served both
        }

        private sealed class MultiplyingCalculator : ICalculator
        {
            public int Add(int a, int b) => a * b;
        }

        [Fact]
        public void Pipeline_WithService_reaches_a_custom_api()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SumApi>()
                .WithService<ICalculator>(new MultiplyingCalculator());
            OrganizationRequest request = new OrganizationRequest("vela_Sum") { ["A"] = 6, ["B"] = 7 };

            OrganizationResponse response = pipeline.Service.Execute(request);

            Assert.Equal(42, response["Sum"]); // 6 * 7 from the test's calculator, not 6 + 7 from the API's own
        }

        [Fact]
        public void PluginContext_WithService_hands_an_HttpClient_over_a_fake_handler()
        {
            FakeHandler handler = new FakeHandler();
            FakeAccount target = new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" };

            PluginContext.For<FakeAccount>()
                .OnCreate().Stage(PluginStages.PreOperation).Sync()
                .WithTarget(target)
                .WithService(new HttpClient(handler))
                .Execute(new ScoringPlugin(string.Empty, string.Empty));

            Assert.Equal("42", target.GetAttributeValue<string>("description"));
            Assert.Equal(new[] { new Uri("https://scoring.example/score?name=Zava") }, handler.Requests);
        }

        [Fact]
        public void Pipeline_WithService_hands_an_HttpClient_over_a_fake_handler()
        {
            FakeHandler handler = new FakeHandler();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ScoringPlugin>()
                .WithService(new HttpClient(handler));

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal("42", Description(pipeline, "account"));
            Assert.Single(handler.Requests);
        }
    }
}
