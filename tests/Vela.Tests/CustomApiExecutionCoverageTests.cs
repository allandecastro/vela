using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Drives Custom API execution to cover CustomApiBase<TRequest,TResponse> value conversion / response mapping
// and the lightweight CustomApiBase handler dispatch.
public sealed class CustomApiExecutionCoverageTests
{
    public sealed class Nested
    {
        public string V { get; set; } = string.Empty;
    }

    public sealed class ConvRequest
    {
        public int Number { get; set; }       // int input -> directly assignable
        public long Big { get; set; }          // int input -> Convert.ChangeType
        public DayOfWeek Day { get; set; }     // int input -> Enum.ToObject
        public Nested? Obj { get; set; }       // JSON string input -> deserialize
    }

    public sealed class ConvResponse
    {
        public string Out { get; set; } = string.Empty;
    }

    private sealed class ConvApi : CustomApiBase<ConvRequest, ConvResponse>
    {
        public ConvApi() : base(string.Empty, string.Empty) { }
        protected override ConvResponse Execute(ILocalPluginContext ctx, ConvRequest request)
            => new ConvResponse { Out = $"{request.Number}/{request.Big}/{request.Day}/{request.Obj?.V}" };
    }

    private sealed class NullResponseApi : CustomApiBase<ConvRequest, ConvResponse>
    {
        public NullResponseApi() : base(string.Empty, string.Empty) { }
        protected override ConvResponse Execute(ILocalPluginContext ctx, ConvRequest request) => null!;
    }

    private sealed class GreetLightApi : CustomApiBase
    {
        public GreetLightApi() : base(string.Empty, string.Empty) { }
        protected override void RegisterCustomApi() => RegisterCustomApi("vela_Greet", nameof(Handle));
        private void Handle(ILocalPluginContext ctx) => ctx.SetOutputParameter("Message", "hi");
    }

    [Fact]
    public void Typed_api_converts_every_input_kind_and_maps_the_response()
    {
        ConvApi api = new ConvApi();
        LocalPluginContext context = PluginContext.ForMessage("vela_Conv").Build();
        var inputs = context.PluginExecutionContext.InputParameters;
        inputs["Number"] = 5;                       // assignable
        inputs["Big"] = 3;                          // int -> long via Convert.ChangeType
        inputs["Day"] = 2;                          // int -> DayOfWeek via Enum.ToObject
        inputs["Obj"] = "{\"V\":\"hi\"}";           // JSON string -> Nested

        api.Execute(context.ServiceProvider);

        Assert.Equal("5/3/Tuesday/hi", context.PluginExecutionContext.OutputParameters["Out"]);
    }

    [Fact]
    public void Typed_api_with_a_null_response_writes_no_output()
    {
        NullResponseApi api = new NullResponseApi();
        LocalPluginContext context = PluginContext.ForMessage("vela_Null").Build();
        context.PluginExecutionContext.InputParameters["Number"] = 1;

        api.Execute(context.ServiceProvider);

        Assert.Empty(context.PluginExecutionContext.OutputParameters);
    }

    [Fact]
    public void Lightweight_api_dispatches_to_its_registered_handler()
    {
        GreetLightApi api = new GreetLightApi();
        LocalPluginContext context = PluginContext.ForMessage("vela_Greet").Build();

        api.Execute(context.ServiceProvider);

        Assert.Equal("hi", context.PluginExecutionContext.OutputParameters["Message"]);
    }
}
