using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// A custom API's request parameters and response properties carry bare-identifier uniquenames equal to their
// plain names, so Dataverse keys inputs and outputs by that plain name. These tests lock in that the typed
// CustomApiRequest/CustomApiResponse bases key their bags by the plain property name, and that the lightweight
// CustomApiBase reads inputs and publishes outputs under the plain name with no name bridging.
public sealed class CustomApiParameterNamingTests
{
    private sealed class EchoRequest : CustomApiRequest
    {
        public EchoRequest() : base("test_Echo") { }

        public string Message { get => Get<string>(); set => Set(value); }
    }

    private sealed class EchoResponse : CustomApiResponse
    {
        public EchoResponse() : base("test_Echo") { }

        public string Echo { get => Get<string>(); set => Set(value); }
    }

    [Fact]
    public void CustomApiRequest_keys_parameters_by_plain_name()
    {
        EchoRequest request = new EchoRequest { Message = "hi" };

        Assert.True(request.Parameters.Contains("Message"));
        Assert.Equal("hi", request.Parameters["Message"]);
        Assert.Equal("hi", request.Message); // property round-trips through the plain key
    }

    [Fact]
    public void CustomApiResponse_keys_results_by_plain_name()
    {
        EchoResponse response = new EchoResponse { Echo = "echo:hi" };

        Assert.True(response.Results.Contains("Echo"));
        Assert.Equal("echo:hi", response.Results["Echo"]);
        Assert.Equal("echo:hi", response.Echo);
    }

    [Fact]
    public void CustomApiResponse_and_request_carry_the_api_unique_name()
    {
        // The constructor argument used to be ignored on the response side.
        Assert.Equal("test_Echo", new EchoResponse().ResponseName);
        Assert.Equal("test_Echo", new EchoRequest().RequestName);
    }

    private sealed class EchoLightApi : CustomApiBase
    {
        public EchoLightApi() : base(string.Empty, string.Empty) { }

        protected override void RegisterCustomApi() =>
            RegisterCustomApi("test_EchoLight", nameof(Handle))
                .RequestParameter<string>("Message")
                .ResponseParameter<string>("Echo");

        private void Handle(ILocalPluginContext ctx)
        {
            string message = ctx.GetInputParameter<string>("Message");
            ctx.SetOutputParameter("Echo", "echo:" + message);
        }
    }

    [Fact]
    public void Lightweight_custom_api_reads_plain_keyed_input_and_publishes_plain_keyed_output()
    {
        EchoLightApi api = new EchoLightApi();
        LocalPluginContext context = PluginContext.ForMessage("test_EchoLight").Build();
        // Simulate a real environment: Dataverse delivers the input under the plain (bare) name.
        context.PluginExecutionContext.InputParameters["Message"] = "hi";

        api.Execute(context.ServiceProvider);

        ParameterCollection output = context.PluginExecutionContext.OutputParameters;
        // The handler read the plain-keyed input (echo:hi, not echo:) and its output is published under the plain
        // response key Dataverse expects.
        Assert.Equal("echo:hi", output["Echo"]);
    }
}
