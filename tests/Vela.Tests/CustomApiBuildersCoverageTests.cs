using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Covers the two public Custom API metadata builders: the typed CustomApiBuilder<TRequest, TResponse>
// (reflection-based default parameters + the typed parameter builder) and the CustomApiLightBuilder.
public sealed class CustomApiBuildersCoverageTests
{
    private sealed class SampleRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        public int? Count { get; set; }

        public string[] Tags { get; set; } = Array.Empty<string>();

        [Description("a note")]
        public string Note { get; set; } = string.Empty;
    }

    private sealed class SampleResponse
    {
        public string Message { get; set; } = string.Empty;
    }

    private static CustomApiBuilder<SampleRequest, SampleResponse> Typed() => new CustomApiBuilder<SampleRequest, SampleResponse>();

    private void InstanceHandler(ILocalPluginContext ctx) { }

    private static class Static
    {
        public static void Handler(ILocalPluginContext ctx) { }
    }

    // ---- CustomApiBuilder<TRequest, TResponse> -------------------------------------------------------

    [Fact]
    public void Typed_builder_requires_a_unique_name()
    {
        Assert.Throws<InvalidOperationException>(() => Typed().Build());
        Assert.Throws<ArgumentNullException>(() => Typed().UniqueName("  "));
        Assert.Throws<ArgumentNullException>(() => Typed().DisplayName(null!));
        Assert.Throws<ArgumentNullException>(() => Typed().ExecutePrivilege(" "));
        Assert.Throws<ArgumentNullException>(() => Typed().BoundTo(string.Empty));
    }

    [Fact]
    public void Typed_builder_defaults_parameters_from_the_models_by_reflection()
    {
        CustomApiDefinition definition = Typed()
            .UniqueName("vela_Sample")
            .Description(null!)        // null -> empty
            .AsFunction()
            .BoundTo<TestAccount>()    // [EntityLogicalName] -> "account"
            .Private()
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.AsyncOnly)
            .WorkflowSdkStep(true)
            .Build();

        Assert.Equal("vela_Sample", definition.DisplayName); // defaults to unique name
        Assert.True(definition.IsFunction);
        Assert.Equal("account", definition.BoundEntityLogicalName);

        CustomApiParameterDefinition name = definition.InputParameters.Single(p => p.Name == "Name");
        Assert.False(name.IsOptional);                 // [Required]
        CustomApiParameterDefinition tags = definition.InputParameters.Single(p => p.Name == "Tags");
        Assert.True(tags.IsCollection);                // string[] is a collection
        Assert.Equal("a note", definition.InputParameters.Single(p => p.Name == "Note").Description);
        Assert.Equal("Message", definition.OutputParameters.Single().Name);
    }

    [Fact]
    public void Typed_builder_request_response_configurators_validate_and_build_parameters()
    {
        Assert.Throws<ArgumentNullException>(() => Typed().Request(null!));
        Assert.Throws<ArgumentNullException>(() => Typed().Response(null!));

        CustomApiDefinition definition = Typed()
            .UniqueName("vela_Custom")
            .DisplayName("Custom")
            .AsAction()
            .BoundToCollection("account")
            .Request(b => b
                .Add(r => r.Name)
                .Add(r => r.Count, optional: true, description: "the count"))
            .Response(b => b.Add(r => r.Message))
            .Build();

        Assert.Equal(CustomApiBindingType.EntityCollection, definition.BindingType);
        Assert.Equal(new[] { "Name", "Count" }, definition.InputParameters.Select(p => p.Name).ToArray());
        Assert.Equal("the count", definition.InputParameters.Single(p => p.Name == "Count").Description);
        Assert.Equal("Message", definition.OutputParameters.Single().Name);
    }

    [Fact]
    public void Typed_parameter_builder_rejects_null_and_non_property_expressions()
    {
        Assert.Throws<ArgumentNullException>(() => Typed().UniqueName("x").Request(b => b.Add<string>(null!)));
        Assert.Throws<ArgumentException>(() => Typed().UniqueName("x").Request(b => b.Add(r => "literal")));
    }

    // ---- CustomApiLightBuilder -----------------------------------------------------------------------

    [Fact]
    public void Light_builder_validates_required_values()
    {
        Assert.Throws<InvalidOperationException>(() => new CustomApiLightBuilder().Build());
        Assert.Throws<ArgumentNullException>(() => new CustomApiLightBuilder().UniqueName(" "));
        Assert.Throws<ArgumentNullException>(() => new CustomApiLightBuilder().DisplayName(null!));
        Assert.Throws<ArgumentNullException>(() => new CustomApiLightBuilder().Handler((string)null!));
        Assert.Throws<ArgumentNullException>(() => new CustomApiLightBuilder().Handler((Action<ILocalPluginContext>)null!));
        Assert.Throws<ArgumentException>(() => new CustomApiLightBuilder().Handler(Static.Handler)); // static rejected
        Assert.Throws<ArgumentNullException>(() => new CustomApiLightBuilder().RequestParameter(null!, typeof(string)));
        Assert.Throws<ArgumentNullException>(() => new CustomApiLightBuilder().ResponseParameter("ok", (Type)null!));
    }

    [Fact]
    public void Light_builder_builds_parameters_and_defaults_the_display_name()
    {
        CustomApiDefinition definition = new CustomApiLightBuilder()
            .UniqueName("vela_Light")
            .Description(null!)
            .AsFunction()
            .BoundToCollection<TestAccount>()
            .Public()
            .Handler(InstanceHandler)
            .RequestParameter<int>("Amount", optional: true, description: "amount")
            .RequestParameter("Names", typeof(string[]))
            .ResponseParameter<string>("Result")
            .Build();

        Assert.Equal("vela_Light", definition.DisplayName);          // defaulted
        Assert.Equal(nameof(InstanceHandler), definition.PluginStep.HandlerMethodName);
        Assert.Equal(string.Empty, definition.RequestTypeName);
        CustomApiParameterDefinition amount = definition.InputParameters.Single(p => p.Name == "Amount");
        Assert.True(amount.IsOptional);
        Assert.True(definition.InputParameters.Single(p => p.Name == "Names").IsCollection); // string[] collection
        Assert.False(definition.InputParameters.Single(p => p.Name == "Amount").IsCollection);
        Assert.Equal("Result", definition.OutputParameters.Single().Name);
    }
}
