using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Testing;
using Xunit;

namespace Vela.Tests
{
    // RegisterCustomApi(uniqueName, Action<ILocalPluginContext>) keeps and invokes the delegate. Resolving the
    // delegate's method name on the plug-in type only found a lambda capturing `this`: a non-capturing lambda (compiled
    // onto the compiler's <>c class), a lambda capturing locals (a display class) and a method of another object all
    // failed at runtime with "handler '<RegisterCustomApi>b__...' was not found".
    public sealed class CustomApiLightDelegateHandlerTests
    {
        private sealed class NonCapturingLambdaApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            protected override void RegisterCustomApi()
                => RegisterCustomApi("vela_NonCapturing", ctx => ctx.SetOutputParameter("Out", "non-capturing"))
                    .ResponseParameter<string>("Out");
        }

        private sealed class CapturingLambdaApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            protected override void RegisterCustomApi()
            {
                string prefix = "captured:";
                RegisterCustomApi("vela_Capturing", ctx => ctx.SetOutputParameter("Out", prefix + ctx.GetInputParameter<string>("In")))
                    .RequestParameter<string>("In")
                    .ResponseParameter<string>("Out");
            }
        }

        private sealed class MethodGroupApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            protected override void RegisterCustomApi()
                => RegisterCustomApi("vela_MethodGroup", Handle).ResponseParameter<string>("Out");

            private void Handle(ILocalPluginContext ctx) => ctx.SetOutputParameter("Out", "method-group");
        }

        private sealed class ThisCapturingLambdaApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            private readonly string _value = "this-capturing";

            protected override void RegisterCustomApi()
                => RegisterCustomApi("vela_ThisCapturing", ctx => ctx.SetOutputParameter("Out", _value))
                    .ResponseParameter<string>("Out");
        }

        private sealed class Greeter
        {
            private readonly string _greeting;

            public Greeter(string greeting) => _greeting = greeting;

            public void Greet(ILocalPluginContext ctx) => ctx.SetOutputParameter("Out", _greeting);
        }

        private sealed class OtherObjectMethodApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            protected override void RegisterCustomApi()
                => RegisterCustomApi("vela_OtherObject", new Greeter("other-object").Greet).ResponseParameter<string>("Out");
        }

        private sealed class CountingApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            protected override void RegisterCustomApi()
            {
                int calls = 0;
                RegisterCustomApi("vela_Counting", ctx => ctx.SetOutputParameter("Calls", ++calls))
                    .ResponseParameter<int>("Calls");
            }
        }

        private static object Call<TPlugin>(string uniqueName, string output, Action<OrganizationRequest>? configure = null)
            where TPlugin : CustomApiBase
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TPlugin>();
            OrganizationRequest request = new OrganizationRequest(uniqueName);
            configure?.Invoke(request);

            OrganizationResponse response = pipeline.Service.Execute(request);

            PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog);
            Assert.True(entry.HandlerInvoked);
            return response[output];
        }

        [Fact]
        public void Non_capturing_lambda_runs()
            => Assert.Equal("non-capturing", Call<NonCapturingLambdaApi>("vela_NonCapturing", "Out"));

        [Fact]
        public void Lambda_capturing_locals_runs()
            => Assert.Equal("captured:x", Call<CapturingLambdaApi>("vela_Capturing", "Out", r => r["In"] = "x"));

        [Fact]
        public void Method_group_runs()
            => Assert.Equal("method-group", Call<MethodGroupApi>("vela_MethodGroup", "Out"));

        [Fact]
        public void Lambda_capturing_this_runs()
            => Assert.Equal("this-capturing", Call<ThisCapturingLambdaApi>("vela_ThisCapturing", "Out"));

        [Fact]
        public void Delegate_to_another_objects_method_runs()
            => Assert.Equal("other-object", Call<OtherObjectMethodApi>("vela_OtherObject", "Out"));

        [Fact]
        public void The_delegate_built_at_registration_is_reused_across_executions()
        {
            // The definition is built once per plug-in instance; its handler (and what it captured) is shared by
            // every later execution, as Dataverse reuses one cached instance.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CountingApi>();

            Assert.Equal(1, pipeline.Service.Execute(new OrganizationRequest("vela_Counting"))["Calls"]);
            Assert.Equal(2, pipeline.Service.Execute(new OrganizationRequest("vela_Counting"))["Calls"]);
        }

        [Fact]
        public void Handler_name_replaces_an_earlier_delegate()
        {
            CustomApiDefinition definition = new CustomApiLightBuilder()
                .UniqueName("vela_Replaced")
                .Handler(new Greeter("unused").Greet)
                .Handler("Named")
                .Build();

            Assert.Equal("Named", definition.HandlerMethodName);
            Assert.Null(definition.RuntimeHandler);
        }

        [Fact]
        public void Handler_delegate_is_kept_with_its_method_name()
        {
            Action<ILocalPluginContext> handler = new Greeter("kept").Greet;

            CustomApiDefinition definition = new CustomApiLightBuilder().UniqueName("vela_Kept").Handler(handler).Build();

            Assert.Same(handler, definition.RuntimeHandler);
            Assert.Equal(nameof(Greeter.Greet), definition.HandlerMethodName);
            Assert.Equal(nameof(Greeter.Greet), definition.PluginStep.HandlerMethodName);
        }

        [Fact]
        public void Handler_exception_propagates_unwrapped()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ThrowingApi>();

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Execute(new OrganizationRequest("vela_Throwing")));

            Assert.Equal("boom", ex.Message);
        }

        private sealed class ThrowingApi(string unsecureConfig, string secureConfig)
            : CustomApiBase(unsecureConfig, secureConfig)
        {
            protected override void RegisterCustomApi()
                => RegisterCustomApi("vela_Throwing", _ => throw new InvalidPluginExecutionException("boom"));
        }
    }
}
