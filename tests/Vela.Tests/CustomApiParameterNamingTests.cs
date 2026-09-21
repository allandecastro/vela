using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Dataverse names a custom API's parameters by their full unique name ("{api}.{param}" for inputs,
// "{api}.{prop}.response" for outputs). These tests lock in that the typed CustomApiRequest/CustomApiResponse
// bases key their bags accordingly, and that the lightweight CustomApiBase translates short handler names to and
// from those full keys.
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
    public void CustomApiRequest_keys_parameters_by_full_unique_name()
    {
        EchoRequest request = new EchoRequest { Message = "hi" };

        Assert.True(request.Parameters.Contains("test_Echo.Message"));
        Assert.False(request.Parameters.Contains("Message"));
        Assert.Equal("hi", request.Parameters["test_Echo.Message"]);
        Assert.Equal("hi", request.Message); // short property round-trips through the full key
    }

    [Fact]
    public void CustomApiResponse_keys_results_by_full_response_name()
    {
        EchoResponse response = new EchoResponse { Echo = "echo:hi" };

        Assert.True(response.Results.Contains("test_Echo.Echo.response"));
        Assert.False(response.Results.Contains("Echo"));
        Assert.Equal("echo:hi", response.Results["test_Echo.Echo.response"]);
        Assert.Equal("echo:hi", response.Echo);
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
    public void Lightweight_custom_api_reads_full_keyed_input_and_publishes_full_keyed_output()
    {
        EchoLightApi api = new EchoLightApi();
        LocalPluginContext context = PluginContext.ForMessage("test_EchoLight").Build();
        // Simulate a real environment: Dataverse delivers the input under the full unique name.
        context.PluginExecutionContext.InputParameters["test_EchoLight.Message"] = "hi";

        api.Execute(context.ServiceProvider);

        ParameterCollection output = context.PluginExecutionContext.OutputParameters;
        // The handler read the full-keyed input (echo:hi, not echo:) and its output is published under the
        // full response key Dataverse expects.
        Assert.Equal("echo:hi", output["test_EchoLight.Echo.response"]);
    }
}
