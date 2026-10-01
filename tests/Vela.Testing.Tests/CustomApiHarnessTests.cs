using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.DI;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: a Custom API registered on the harness executes through the pipeline like the platform — input
    // parameters flow in, a DI service resolves in the handler, and output parameters come back under their plain
    // (dot-free) names. Covers unbound/bound and Action/Function shapes, typed request/response mapping, and a
    // handler fault surfacing to the caller.
    public sealed class CustomApiHarnessTests
    {
        private static OrganizationRequest Request(string name, params (string Key, object Value)[] parameters)
        {
            OrganizationRequest request = new OrganizationRequest(name);
            foreach ((string key, object value) in parameters)
            {
                request[key] = value;
            }

            return request;
        }

        // --- Unbound Action + a DI service injected into the handler -------------------------------------

        [Fact]
        public void An_unbound_action_uses_an_injected_service_and_returns_dot_free_output()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SumApi>();

            OrganizationResponse response = pipeline.Service.Execute(Request("vela_Sum", ("A", 20), ("B", 22)));

            // The output comes back under the plain name "Sum" (no alias/dotting) and was computed by the DI service.
            Assert.True(response.Results.Contains("Sum"));
            Assert.Equal(42, response["Sum"]);
        }

        // --- Bound Action reads its Target and returns output --------------------------------------------

        [Fact]
        public void A_bound_action_reads_its_target_and_returns_dot_free_output()
        {
            Guid recordId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TouchApi>();

            OrganizationResponse response = pipeline.Service.Execute(
                Request("vela_Touch", ("Target", new EntityReference("account", recordId))));

            Assert.Equal(recordId, response["RecordId"]);
        }

        // --- Function returns output --------------------------------------------------------------------

        [Fact]
        public void A_function_returns_dot_free_output()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<EchoApi>();

            OrganizationResponse response = pipeline.Service.Execute(Request("vela_Echo", ("Text", "ping")));

            Assert.Equal("echo:ping", response["Echo"]);
        }

        // --- Required input parameters are enforced like the platform -----------------------------------

        [Fact]
        public void A_missing_required_input_parameter_is_rejected()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RequiredParamApi>();

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Execute(Request("vela_RequiredParam")));   // "Key" omitted
            Assert.Contains("Key", ex.Message);
        }

        [Fact]
        public void A_supplied_required_input_parameter_is_accepted()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RequiredParamApi>();

            OrganizationResponse response = pipeline.Service.Execute(Request("vela_RequiredParam", ("Key", "value")));

            Assert.NotNull(response);
        }

        // --- Typed request/response mapping -------------------------------------------------------------

        [Fact]
        public void A_typed_api_maps_the_response_object_to_dot_free_output_parameters()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SumTypedApi>();

            OrganizationResponse response = pipeline.Service.Execute(Request("vela_SumTyped", ("A", 2), ("B", 3)));

            Assert.Equal(5, response["Sum"]);
        }

        // --- A handler that throws surfaces the fault ---------------------------------------------------

        [Fact]
        public void A_custom_api_that_throws_surfaces_the_fault()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<BoomApi>();

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Execute(Request("vela_Boom")));
            Assert.Contains("kaboom", ex.Message);
        }
    }

    // A domain service resolved via the per-execution DI container.
    public interface ICalculator
    {
        int Add(int a, int b);
    }

    public sealed class Calculator : ICalculator
    {
        public int Add(int a, int b) => a + b;
    }

    // Unbound Action that resolves ICalculator from DI and writes a plain-named output.
    public sealed class SumApi : CustomApiBase
    {
        public SumApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_Sum", nameof(Handle)).AsAction();

        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
            => services.AddScoped<ICalculator>(_ => new Calculator());

        private void Handle(ILocalPluginContext ctx)
        {
            ICalculator calculator = ctx.GetRequiredService<ICalculator>();
            int a = ctx.GetInputParameter<int>("A");
            int b = ctx.GetInputParameter<int>("B");
            ctx.SetOutputParameter("Sum", calculator.Add(a, b));
        }
    }

    // Declares one required input parameter ("Key" - RequestParameter defaults to optional: false).
    public sealed class RequiredParamApi : CustomApiBase
    {
        public RequiredParamApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_RequiredParam", nameof(Handle)).AsAction().RequestParameter<string>("Key");

        private void Handle(ILocalPluginContext ctx)
        {
        }
    }

    // Bound Action: reads its Target and returns the record id.
    public sealed class TouchApi : CustomApiBase
    {
        public TouchApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_Touch", nameof(Handle)).AsAction().BoundTo("account");

        private void Handle(ILocalPluginContext ctx)
        {
            EntityReference? target = ctx.GetInputParameter<EntityReference>("Target");
            ctx.SetOutputParameter("RecordId", target!.Id);
        }
    }

    // Function that echoes its input.
    public sealed class EchoApi : CustomApiBase
    {
        public EchoApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_Echo", nameof(Handle)).AsFunction();

        private void Handle(ILocalPluginContext ctx)
            => ctx.SetOutputParameter("Echo", $"echo:{ctx.GetInputParameter<string>("Text")}");
    }

    // A Custom API whose handler throws.
    public sealed class BoomApi : CustomApiBase
    {
        public BoomApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_Boom", nameof(Handle)).AsAction();

        private void Handle(ILocalPluginContext ctx) => throw new PluginBusinessException("kaboom");
    }

    public sealed class SumRequest : OrganizationRequest
    {
        public SumRequest() => RequestName = "vela_SumTyped";

        public int A
        {
            get => Parameters.Contains(nameof(A)) ? (int)Parameters[nameof(A)] : 0;
            set => Parameters[nameof(A)] = value;
        }

        public int B
        {
            get => Parameters.Contains(nameof(B)) ? (int)Parameters[nameof(B)] : 0;
            set => Parameters[nameof(B)] = value;
        }
    }

    public sealed class SumResponse : OrganizationResponse
    {
        public int Sum
        {
            get => Results.Contains(nameof(Sum)) ? (int)Results[nameof(Sum)] : 0;
            set => Results[nameof(Sum)] = value;
        }
    }

    // Typed Action that maps a response object back to plain-named output parameters.
    public sealed class SumTypedApi : CustomApiBase<SumRequest, SumResponse>
    {
        public SumTypedApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_SumTyped").AsAction();

        protected override SumResponse Execute(ILocalPluginContext ctx, SumRequest request)
            => new SumResponse { Sum = request.A + request.B };
    }
}
