using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Vela.Constants;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.LabelLengthAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class LabelLengthAnalyzerTests
{
    // Minimal stand-ins for the Custom API builders (typed and lightweight), the request/response models and the
    // step builder, with the real parameter names the analyzer matches on.
    private const string Harness = @"#nullable enable
namespace Microsoft.Xrm.Sdk
{
    public class OrganizationRequest { }
    public class OrganizationResponse { }
}

namespace Vela.CustomApi
{
    using System;
    using System.Linq.Expressions;

    public interface ICustomApiMetadata<out TSelf>
    {
        TSelf UniqueName(string uniqueName);
        TSelf DisplayName(string displayName);
        TSelf Description(string description);
    }

    public interface ICustomApiBuilder : ICustomApiMetadata<ICustomApiBuilder>
    {
        ICustomApiBuilder RequestParameter<TParameter>(string name, bool optional = false, string? description = null, string? logicalEntityName = null);
        ICustomApiBuilder RequestParameter(string name, Type parameterType, bool optional = false, string? description = null, string? logicalEntityName = null);
        ICustomApiBuilder ResponseParameter<TParameter>(string name, string? description = null, string? logicalEntityName = null);
        ICustomApiBuilder ResponseParameter(string name, Type parameterType, string? description = null, string? logicalEntityName = null);
    }

    public interface ICustomApiParameterBuilder<TModel> where TModel : class
    {
        ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property);
        ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property, bool optional, string? description = null, string? logicalEntityName = null);
    }

    public interface ICustomApiBuilder<TRequest, TResponse> : ICustomApiMetadata<ICustomApiBuilder<TRequest, TResponse>>
        where TRequest : class
        where TResponse : class
    {
        ICustomApiBuilder<TRequest, TResponse> Request(Action<ICustomApiParameterBuilder<TRequest>> configure);
        ICustomApiBuilder<TRequest, TResponse> Response(Action<ICustomApiParameterBuilder<TResponse>> configure);
    }

    public sealed class CustomApiLightBuilder : ICustomApiBuilder
    {
        public ICustomApiBuilder UniqueName(string uniqueName) => this;
        public ICustomApiBuilder DisplayName(string displayName) => this;
        public ICustomApiBuilder Description(string description) => this;
        public ICustomApiBuilder RequestParameter<TParameter>(string name, bool optional = false, string? description = null, string? logicalEntityName = null) => this;
        public ICustomApiBuilder RequestParameter(string name, Type parameterType, bool optional = false, string? description = null, string? logicalEntityName = null) => this;
        public ICustomApiBuilder ResponseParameter<TParameter>(string name, string? description = null, string? logicalEntityName = null) => this;
        public ICustomApiBuilder ResponseParameter(string name, Type parameterType, string? description = null, string? logicalEntityName = null) => this;
    }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IFakeStep : IFluentStep
    {
        IFakeStep WithDescription(string description);
        IFakeStep WithStepKey(string key);
        void Handle();
    }
}

namespace Demo
{
    using Microsoft.Xrm.Sdk;

    public sealed class Req : OrganizationRequest { public string? Name { get; set; } }
    public sealed class Resp : OrganizationResponse { public string? Result { get; set; } }

    public class Base
    {
        protected Vela.CustomApi.ICustomApiBuilder Light() => null!;
        protected Vela.CustomApi.ICustomApiBuilder<Req, Resp> Typed() => null!;
        protected Vela.Steps.IFakeStep RegisterStep() => null!;
    }
}
";

    private static string InMethod(string body, string members = "") => Harness + @"
namespace Demo
{
    using System;
    using Vela.CustomApi;

    public class Use : Base
    {
        " + members + @"
        public void M(string runtime)
        {
            " + body + @"
        }
    }
}
";

    private static string Text(int length) => "\"" + new string('a', length) + "\"";

    private static DiagnosticResult Expected(string label, int length, int max) =>
        new DiagnosticResult(LabelLengthAnalyzer.Rule).WithLocation(0).WithArguments(label, length, max);

    private static DiagnosticResult ExpectedStep(int length, int max, int marker) =>
        new DiagnosticResult(LabelLengthAnalyzer.StepRule).WithLocation(0).WithArguments(length, max, marker);

    // --- Custom API display name and description -----------------------------------------------------------------

    [Fact]
    public async Task Display_name_at_the_limit_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Light().DisplayName({Text(100)});"));

    [Fact]
    public async Task Display_name_over_the_limit_is_reported()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Light().DisplayName({{|#0:{Text(101)}|}});"),
            Expected("The Custom API display name", 101, 100));

    [Fact]
    public async Task Description_at_the_limit_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Typed().Description({Text(300)});"));

    [Fact]
    public async Task Description_over_the_limit_is_reported_on_the_typed_builder()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Typed().UniqueName(\"x\").Description({{|#0:{Text(301)}|}});"),
            Expected("The Custom API description", 301, 300));

    [Fact]
    public async Task Description_over_the_limit_is_reported_on_the_concrete_light_builder()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"new CustomApiLightBuilder().Description({{|#0:{Text(301)}|}});"),
            Expected("The Custom API description", 301, 300));

    [Fact]
    public async Task A_const_field_is_evaluated()
        => await Verify.VerifyAnalyzerAsync(
            InMethod("Light().DisplayName({|#0:Long|});", $"private const string Long = {Text(102)};"),
            Expected("The Custom API display name", 102, 100));

    [Fact]
    public async Task A_concatenation_of_constants_is_evaluated()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Light().DisplayName({{|#0:Part + {Text(51)}|}});", $"private const string Part = {Text(50)};"),
            Expected("The Custom API display name", 101, 100));

    [Fact]
    public async Task A_constant_interpolated_string_is_evaluated()
        => await Verify.VerifyAnalyzerAsync(
            InMethod("Light().DisplayName({|#0:$\"{Part}-{Part}\"|});", $"private const string Part = {Text(50)};"),
            Expected("The Custom API display name", 101, 100));

    [Fact]
    public async Task A_non_constant_value_is_left_to_plan()
        => await Verify.VerifyAnalyzerAsync(InMethod("Light().DisplayName(runtime + new string('a', 200)).Description(runtime);"));

    [Fact]
    public async Task UniqueName_is_not_a_label()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Light().UniqueName({Text(150)});"));

    // --- Lightweight builder parameters ---------------------------------------------------------------------------

    [Fact]
    public async Task Request_parameter_description_at_the_limit_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Light().RequestParameter<string>(\"Name\", description: {Text(300)});"));

    [Fact]
    public async Task Request_parameter_description_over_the_limit_is_reported()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Light().RequestParameter<string>(\"Name\", false, {{|#0:{Text(301)}|}});"),
            Expected("The description of request parameter 'Name'", 301, 300));

    [Fact]
    public async Task Request_parameter_description_is_reported_on_the_Type_overload()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Light().RequestParameter(runtime, typeof(string), description: {{|#0:{Text(301)}|}});"),
            Expected("The description of a request parameter", 301, 300));

    [Fact]
    public async Task Response_property_description_at_the_limit_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Light().ResponseParameter<string>(\"Result\", {Text(100)});"));

    [Fact]
    public async Task Response_property_description_over_the_limit_is_reported()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Light().ResponseParameter(\"Result\", typeof(string), {{|#0:{Text(101)}|}});"),
            Expected("The description of response property 'Result'", 101, 100));

    [Fact]
    public async Task A_parameter_without_a_description_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod("Light().RequestParameter<string>(\"Name\").ResponseParameter<string>(\"Result\");"));

    // --- Typed builder parameters (Add) ---------------------------------------------------------------------------

    [Fact]
    public async Task Typed_request_parameter_description_at_the_limit_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Typed().Request(p => p.Add(x => x.Name, false, {Text(300)}));"));

    [Fact]
    public async Task Typed_request_parameter_description_over_the_limit_is_reported()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Typed().Request(p => p.Add(x => x.Name, false, {{|#0:{Text(301)}|}}));"),
            Expected("The description of request parameter 'Name'", 301, 300));

    [Fact]
    public async Task Typed_response_property_description_at_the_limit_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"Typed().Response(p => p.Add(x => x.Result, false, description: {Text(100)}));"));

    [Fact]
    public async Task Typed_response_property_description_over_the_limit_is_reported()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"Typed().Response(p => p.Add(x => x.Result, false, description: {{|#0:{Text(101)}|}}));"),
            Expected("The description of response property 'Result'", 101, 100));

    // --- [Description] on a request / response model --------------------------------------------------------------

    private static string Models(string requestAttribute, string responseAttribute) => Harness + @"
namespace Demo
{
    using System.ComponentModel;
    using Microsoft.Xrm.Sdk;

    public sealed class OrderRequest : OrganizationRequest
    {
        [Description(" + requestAttribute + @")]
        public string? Order { get; set; }
    }

    public sealed class OrderResponse : OrganizationResponse
    {
        [Description(" + responseAttribute + @")]
        public string? Total { get; set; }
    }

    public sealed class NotAModel
    {
        [Description(" + Text(400) + @")]
        public string? Other { get; set; }
    }
}
";

    [Fact]
    public async Task Description_attributes_at_the_limit_are_fine()
        => await Verify.VerifyAnalyzerAsync(Models(Text(300), Text(100)));

    [Fact]
    public async Task Description_attributes_over_the_limit_are_reported_with_the_model_kind()
        => await Verify.VerifyAnalyzerAsync(
            Models($"{{|#0:{Text(301)}|}}", $"{{|#1:{Text(101)}|}}"),
            Expected("The description of request parameter 'Order'", 301, 300),
            new DiagnosticResult(LabelLengthAnalyzer.Rule).WithLocation(1).WithArguments("The description of response property 'Total'", 101, 100));

    // --- Step description (the CLI checks it with the step-key marker) -------------------------------------------

    private const int AutoKeyMarker = 31; // "\n[vela:stepkey=" + "dv-" + 12 hex + "]"

    [Fact]
    public async Task Step_description_at_the_limit_with_the_automatic_key_is_fine()
        => await Verify.VerifyAnalyzerAsync(InMethod($"RegisterStep().WithDescription({Text(256 - AutoKeyMarker)}).Handle();"));

    [Fact]
    public async Task Step_description_over_the_limit_with_the_automatic_key_is_reported()
        => await Verify.VerifyAnalyzerAsync(
            InMethod($"RegisterStep().WithDescription({{|#0:{Text(256 - AutoKeyMarker + 1)}|}}).Handle();"),
            ExpectedStep(226, 225, AutoKeyMarker));

    [Fact]
    public async Task Step_description_uses_an_explicit_key_set_later_in_the_chain()
    {
        // "\n[vela:stepkey=" + 40 + "]" = 56, so 200 characters fit and 201 do not.
        string key = Text(40);
        await Verify.VerifyAnalyzerAsync(InMethod($"RegisterStep().WithDescription({Text(200)}).WithStepKey({key}).Handle();"));
        await Verify.VerifyAnalyzerAsync(
            InMethod($"RegisterStep().WithStepKey({key}).WithDescription({{|#0:{Text(201)}|}}).Handle();"),
            ExpectedStep(201, 200, 56));
    }

    [Fact]
    public async Task Step_description_is_trimmed_like_the_CLI()
        => await Verify.VerifyAnalyzerAsync(InMethod($"RegisterStep().WithDescription(\"  \" + {Text(256 - AutoKeyMarker)} + \"  \").Handle();"));

    [Fact]
    public async Task Step_description_with_a_non_constant_key_is_left_to_plan()
        => await Verify.VerifyAnalyzerAsync(InMethod($"RegisterStep().WithDescription({Text(250)}).WithStepKey(runtime).Handle();"));

    [Fact]
    public async Task Step_description_of_a_split_chain_is_left_to_plan()
        => await Verify.VerifyAnalyzerAsync(InMethod($"var step = RegisterStep().WithDescription({Text(250)}); step.WithStepKey(\"k\").Handle();"));

    [Fact]
    public async Task Non_constant_step_description_is_left_to_plan()
        => await Verify.VerifyAnalyzerAsync(InMethod("RegisterStep().WithDescription(runtime).Handle();"));

    // --- Scope ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Same_named_methods_on_unrelated_types_are_ignored()
        => await Verify.VerifyAnalyzerAsync(@"
public sealed class Other
{
    public Other DisplayName(string displayName) => this;
    public Other Description(string description) => this;
    public Other WithDescription(string description) => this;
    public void M() => new Other().DisplayName(" + Text(500) + ").Description(" + Text(500) + ").WithDescription(" + Text(500) + @");
}
");

    // The limits the rule enforces are the ones vela plan checks (one linked source, DataverseLabelLimits): pin the
    // values so a change to a Dataverse column length is a deliberate edit of that file and of the docs.
    [Fact]
    public void Limits_match_the_documented_Dataverse_column_lengths()
    {
        Assert.Equal(100, DataverseLabelLimits.CustomApiDisplayName);
        Assert.Equal(300, DataverseLabelLimits.CustomApiDescription);
        Assert.Equal(300, DataverseLabelLimits.CustomApiRequestParameterDescription);
        Assert.Equal(100, DataverseLabelLimits.CustomApiResponsePropertyDescription);
        Assert.Equal(256, DataverseLabelLimits.StepDescription);
        Assert.Equal(AutoKeyMarker, DataverseLabelLimits.StepKeyMarkerLength(DataverseLabelLimits.AutoStepKeyPrefix.Length + DataverseLabelLimits.AutoStepKeyHashLength));
    }
}
