using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // PluginPipeline.WithManagedIdentityService reaches every plugin the pipeline runs; WithService refuses the
    // platform services the context reads from the platform rather than from the resolver, on both harnesses.
    public sealed class PipelineManagedIdentityTests
    {
        private sealed class ScopeEchoIdentity : IManagedIdentityService
        {
            public List<string> Scopes { get; } = new List<string>();

            public string AcquireToken(IEnumerable<string> scopes)
            {
                string scope = scopes.Single();
                Scopes.Add(scope);
                return "token-for:" + scope;
            }

            public string AcquireToken(Guid applicationUserId, IEnumerable<string> scopes) => AcquireToken(scopes);
        }

        // Stores a token on the account in PreOperation, then creates a task whose own plugin acquires one too.
        private sealed class TokenAccountPlugin : EntityPluginBase<FakeAccount>
        {
            public TokenAccountPlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnCreate().PreOperation().Sync().FilterOn(e => e.Name).Handle(OnCreating);
                RegisterStep().OnCreate().PostOperation().Sync().FilterOn(e => e.Name).Handle(OnCreated);
            }

            private void OnCreating(ICreatePreContext<FakeAccount> ctx)
                => ctx.Request.Target["description"] = ctx.AcquireManagedIdentityToken(new[] { "https://account.example/.default" });

            private void OnCreated(ICreatePostContext<FakeAccount> ctx)
                => ctx.Service.Create(new FakeTask { Subject = "follow-up" });
        }

        private sealed class TokenTaskPlugin : EntityPluginBase<FakeTask>
        {
            public TokenTaskPlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(e => e.Subject).Handle(OnCreating);

            private void OnCreating(ICreatePreContext<FakeTask> ctx)
                => ctx.Request.Target["description"] = ctx.AcquireManagedIdentityToken(new[] { "https://task.example/.default" });
        }

        private static string Description(PluginPipeline pipeline, string table)
            => pipeline.GetAll(table).Single().GetAttributeValue<string>("description");

        [Fact]
        public void WithManagedIdentityService_reaches_every_plugin_including_nested_executions()
        {
            ScopeEchoIdentity identity = new ScopeEchoIdentity();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TokenAccountPlugin>()
                .Register<TokenTaskPlugin>()
                .WithManagedIdentityService(identity);

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Equal("token-for:https://account.example/.default", Description(pipeline, "account"));
            Assert.Equal("token-for:https://task.example/.default", Description(pipeline, "task")); // nested, depth 2
            Assert.Equal(new[] { "https://account.example/.default", "https://task.example/.default" }, identity.Scopes);
        }

        [Fact]
        public void WithManagedIdentityService_called_twice_keeps_the_last_service()
        {
            ScopeEchoIdentity first = new ScopeEchoIdentity();
            ScopeEchoIdentity second = new ScopeEchoIdentity();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<TokenTaskPlugin>()
                .WithManagedIdentityService(first)
                .WithManagedIdentityService(second);

            pipeline.Service.Create(new FakeTask { Subject = "follow-up" });

            Assert.Empty(first.Scopes);
            Assert.Single(second.Scopes);
        }

        [Fact]
        public void Without_WithManagedIdentityService_the_token_is_unavailable()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TokenTaskPlugin>();

            Exception error = Record.Exception(() => pipeline.Service.Create(new FakeTask { Subject = "follow-up" }));

            Assert.NotNull(error);
            Assert.Contains("No IManagedIdentityService was provided to this plug-in. In a test, give the harness one with WithManagedIdentityService(...).", error!.Message);
        }

        [Fact]
        public void WithManagedIdentityService_rejects_null()
        {
            Assert.Throws<ArgumentNullException>(() => PluginPipeline.Empty().WithManagedIdentityService(null!));
        }

        [Fact]
        public void Pipeline_WithService_rejects_the_managed_identity_service_and_names_the_dedicated_method()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => PluginPipeline.Empty().WithService<IManagedIdentityService>(new ScopeEchoIdentity()));

            Assert.Contains("WithManagedIdentityService", error.Message);
        }

        [Fact]
        public void PluginContext_WithService_rejects_the_managed_identity_service_and_names_the_dedicated_method()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => PluginContext.For<FakeAccount>().WithService<IManagedIdentityService>(new ScopeEchoIdentity()));

            Assert.Contains("WithManagedIdentityService", error.Message);
        }

        [Fact]
        public void PluginContext_WithService_rejects_every_platform_service_with_its_builder_method()
        {
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>();
            IOrganizationService service = new InMemoryOrganizationService();

            Assert.Contains("WithTracingService", Assert.Throws<ArgumentException>(() => builder.WithService<ITracingService>(new RecordingTracingService())).Message);
            Assert.Contains("WithLogger", Assert.Throws<ArgumentException>(() => builder.WithService<ILogger>(new RecordingLogger())).Message);
            Assert.Contains("WithOrganizationService", Assert.Throws<ArgumentException>(() => builder.WithService(service)).Message);
            Assert.Contains("WithOrganizationService", Assert.Throws<ArgumentException>(() => builder.WithService<IOrganizationServiceFactory>(new FakeFactory())).Message);
            Assert.Contains("WithTarget", Assert.Throws<ArgumentException>(() => builder.WithService<IPluginExecutionContext>(new FakePluginExecutionContext())).Message);
            Assert.Contains("WithTarget", Assert.Throws<ArgumentException>(() => builder.WithService<IPluginExecutionContext7>(new FakePluginExecutionContext())).Message);
        }

        [Fact]
        public void Pipeline_WithService_rejects_every_platform_service_with_how_the_pipeline_supplies_it()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            Assert.Contains("Trace", Assert.Throws<ArgumentException>(() => pipeline.WithService<ITracingService>(new RecordingTracingService())).Message);
            Assert.Contains("LogCapture", Assert.Throws<ArgumentException>(() => pipeline.WithService<ILogger>(new RecordingLogger())).Message);
            Assert.Contains("in-memory store", Assert.Throws<ArgumentException>(() => pipeline.WithService<IOrganizationService>(new InMemoryOrganizationService())).Message);
            Assert.Contains("in-memory store", Assert.Throws<ArgumentException>(() => pipeline.WithService<IOrganizationServiceFactory>(new FakeFactory())).Message);
            Assert.Contains("execution context", Assert.Throws<ArgumentException>(() => pipeline.WithService<IPluginExecutionContext>(new FakePluginExecutionContext())).Message);
        }

        [Fact]
        public void WithService_still_accepts_a_concrete_type_that_implements_a_platform_interface()
        {
            // Only the platform interfaces are reserved: a service registered under its own type is the plugin's.
            PluginPipeline pipeline = PluginPipeline.Empty().WithService(new RecordingTracingService());

            Assert.NotNull(pipeline);
        }

        private sealed class FakeFactory : IOrganizationServiceFactory
        {
            public IOrganizationService CreateOrganizationService(Guid? userId) => throw new NotSupportedException();
        }
    }
}
