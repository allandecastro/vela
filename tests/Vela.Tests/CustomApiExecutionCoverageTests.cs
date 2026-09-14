using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Testing;
using Xunit;

namespace Vela.Tests;

// Drives Custom API execution to cover CustomApiBase<TRequest,TResponse> request hydration / response mapping,
// the derived metadata (which must exclude the OrganizationRequest/OrganizationResponse base members), and the
// lightweight CustomApiBase handler dispatch.
public sealed class CustomApiExecutionCoverageTests
{
    public sealed class ConvRequest : OrganizationRequest
    {
        public ConvRequest()
        {
            RequestName = "vela_Conv";
        }

        public int Number
        {
            get => Parameters.Contains(nameof(Number)) ? (int)Parameters[nameof(Number)] : default;
            set => Parameters[nameof(Number)] = value;
        }

        public string Text
        {
            get => Parameters.Contains(nameof(Text)) ? (string)Parameters[nameof(Text)] : string.Empty;
            set => Parameters[nameof(Text)] = value;
        }

        public EntityReference? Target
        {
            get => Parameters.Contains(nameof(Target)) ? (EntityReference)Parameters[nameof(Target)] : null;
            set => Parameters[nameof(Target)] = value;
        }
    }

    public sealed class ConvResponse : OrganizationResponse
    {
        public string Out
        {
            get => Results.Contains(nameof(Out)) ? (string)Results[nameof(Out)] : string.Empty;
            set => Results[nameof(Out)] = value;
        }
    }

    private sealed class ConvApi : CustomApiBase<ConvRequest, ConvResponse>
    {
        public ConvApi() : base(string.Empty, string.Empty) { }
        protected override ConvResponse Execute(ILocalPluginContext ctx, ConvRequest request)
            => new ConvResponse { Out = $"{request.Number}/{request.Text}/{request.Target?.Id}" };
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
    public void Typed_api_reads_live_input_parameters_and_maps_the_response()
    {
        Guid targetId = Guid.NewGuid();
        ConvApi api = new ConvApi();
        LocalPluginContext context = PluginContext.ForMessage("vela_Conv").Build();
        var inputs = context.PluginExecutionContext.InputParameters;
        inputs["Number"] = 5;
        inputs["Text"] = "hi";
        inputs["Target"] = new EntityReference("account", targetId);

        api.Execute(context.ServiceProvider);

        Assert.Equal($"5/hi/{targetId}", context.PluginExecutionContext.OutputParameters["Out"]);
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
    public void Derived_metadata_reflects_declared_properties_and_excludes_base_members()
    {
        CustomApiDefinition definition = Vela.Register.RegisterCustomApi<ConvRequest, ConvResponse>()
            .UniqueName("vela_Conv")
            .AsAction()
            .Build();

        string[] inputNames = definition.InputParameters.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] outputNames = definition.OutputParameters.Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "Number", "Target", "Text" }, inputNames);
        Assert.Equal(new[] { "Out" }, outputNames);

        // The OrganizationRequest/OrganizationResponse infrastructure members must never leak in as parameters.
        foreach (string leaked in new[] { "RequestName", "RequestId", "Parameters", "ResponseName", "Results", "ExtensionData", "Item" })
        {
            Assert.DoesNotContain(leaked, inputNames);
            Assert.DoesNotContain(leaked, outputNames);
        }
    }

    [Fact]
    public void Lightweight_api_dispatches_to_its_registered_handler()
    {
        GreetLightApi api = new GreetLightApi();
        LocalPluginContext context = PluginContext.ForMessage("vela_Greet").Build();

        api.Execute(context.ServiceProvider);

        Assert.Equal("hi", context.PluginExecutionContext.OutputParameters["Message"]);
    }

    [Fact]
    public void GetCustomApiDefinition_is_thread_safe_and_returns_one_consistent_definition()
    {
        GreetLightApi api = new GreetLightApi();

        CustomApiDefinition[] results = new CustomApiDefinition[32];
        // The Dataverse sandbox reuses one plugin instance across concurrent first-executions. Without the init
        // lock, racing builds null the shared _activeBuilder mid-build and throw or cross-wire the definition.
        System.Threading.Tasks.Parallel.For(0, results.Length, i => results[i] = api.GetCustomApiDefinition());

        Assert.All(results, d => Assert.Same(results[0], d)); // one shared, fully-built definition
        Assert.Equal("vela_Greet", results[0].UniqueName);
    }
}
