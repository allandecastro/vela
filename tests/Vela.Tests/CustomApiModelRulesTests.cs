using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// A-38 / A-39: the Custom API model - required-by-default request parameters, the parameter's table
// (logicalentityname), the Dataverse rules Build() enforces, and the core both builders share.
public sealed class CustomApiModelRulesTests
{
    private sealed class Req : OrganizationRequest
    {
        public string Name { get => Get<string>(nameof(Name)); set => Parameters[nameof(Name)] = value; }

        [Required]
        public string Code { get => Get<string>(nameof(Code)); set => Parameters[nameof(Code)] = value; }

        public EntityReference Owner { get => Get<EntityReference>(nameof(Owner)); set => Parameters[nameof(Owner)] = value; }

        [LogicalEntityName("contact")]
        public EntityReference Contact { get => Get<EntityReference>(nameof(Contact)); set => Parameters[nameof(Contact)] = value; }

        public TestAccount Account { get => Get<TestAccount>(nameof(Account)); set => Parameters[nameof(Account)] = value; }

        private T Get<T>(string key) => Parameters.Contains(key) ? (T)Parameters[key] : default!;
    }

    private sealed class Resp : OrganizationResponse
    {
        public string Message { get => Results.Contains(nameof(Message)) ? (string)Results[nameof(Message)] : string.Empty; set => Results[nameof(Message)] = value; }

        public EntityCollection Rows { get => (EntityCollection)Results[nameof(Rows)]; set => Results[nameof(Rows)] = value; }
    }

    private sealed class EmptyResp : OrganizationResponse
    {
    }

    private static ICustomApiBuilder<Req, Resp> Typed() => new CustomApiBuilder<Req, Resp>().UniqueName("vela_Api");

    private static ICustomApiBuilder Light() => new CustomApiLightBuilder().UniqueName("vela_Api");

    private static CustomApiParameterDefinition Input(CustomApiDefinition definition, string name) => definition.InputParameters.Single(p => p.Name == name);

    // ---- required by default -------------------------------------------------------------------------

    [Fact]
    public void An_added_request_parameter_is_required_unless_declared_optional()
    {
        CustomApiDefinition definition = Typed()
            .Request(r => r
                .Add(x => x.Name)                                   // no [Required]: still required
                .Add(x => x.Code, optional: true)                   // [Required] does not override optional: true
                .Add(x => x.Owner, optional: false, "the owner"))
            .Response(r => r.Add(x => x.Message, optional: true))  // a response property has no optionality
            .Build();

        Assert.False(Input(definition, "Name").IsOptional);
        Assert.True(Input(definition, "Code").IsOptional);
        Assert.False(Input(definition, "Owner").IsOptional);
        Assert.False(definition.OutputParameters.Single().IsOptional);
    }

    [Fact]
    public void An_inferred_request_parameter_stays_optional_unless_it_carries_Required()
    {
        CustomApiDefinition definition = Typed().Build();

        Assert.True(Input(definition, "Name").IsOptional);
        Assert.False(Input(definition, "Code").IsOptional);
        Assert.All(definition.OutputParameters, p => Assert.False(p.IsOptional));
    }

    [Fact]
    public void A_light_request_parameter_is_required_by_default()
    {
        CustomApiDefinition definition = Light().RequestParameter<string>("A").RequestParameter<string>("B", optional: true).Build();

        Assert.False(Input(definition, "A").IsOptional);
        Assert.True(Input(definition, "B").IsOptional);
    }

    // ---- logicalentityname ---------------------------------------------------------------------------

    [Fact]
    public void A_typed_parameter_takes_its_table_from_the_argument_the_attribute_or_the_early_bound_type()
    {
        CustomApiDefinition definition = Typed()
            .Request(r => r
                .Add(x => x.Owner, optional: false, logicalEntityName: "systemuser")
                .Add(x => x.Contact)
                .Add(x => x.Account)
                .Add(x => x.Name))
            .Response(r => r.Add(x => x.Message))
            .Build();

        Assert.Equal("systemuser", Input(definition, "Owner").LogicalEntityName);
        Assert.Equal("contact", Input(definition, "Contact").LogicalEntityName);
        Assert.Equal("account", Input(definition, "Account").LogicalEntityName);
        Assert.Null(Input(definition, "Name").LogicalEntityName);
    }

    [Fact]
    public void An_argument_overrides_the_attribute_and_inferred_parameters_read_it_too()
    {
        CustomApiDefinition explicitDefinition = Typed().Request(r => r.Add(x => x.Contact, optional: false, logicalEntityName: "lead")).Build();
        CustomApiDefinition inferred = Typed().Build();

        Assert.Equal("lead", Input(explicitDefinition, "Contact").LogicalEntityName);
        Assert.Equal("contact", Input(inferred, "Contact").LogicalEntityName);
        Assert.Equal("account", Input(inferred, "Account").LogicalEntityName);
        Assert.Null(Input(inferred, "Owner").LogicalEntityName);
    }

    [Fact]
    public void A_light_parameter_takes_its_table_from_the_argument_or_the_early_bound_type()
    {
        CustomApiDefinition definition = Light()
            .RequestParameter<EntityReference>("Target2", logicalEntityName: "account")
            .RequestParameter<TestAccount>("Record")
            .ResponseParameter<EntityReference>("Created", logicalEntityName: "contact")
            .Build();

        Assert.Equal("account", Input(definition, "Target2").LogicalEntityName);
        Assert.Equal("account", Input(definition, "Record").LogicalEntityName);
        Assert.Equal("contact", definition.OutputParameters.Single().LogicalEntityName);
    }

    [Fact]
    public void A_table_on_a_parameter_that_cannot_have_one_fails()
    {
        ArgumentException onString = Assert.Throws<ArgumentException>(() => Light().RequestParameter<string>("Name", logicalEntityName: "account"));
        Assert.StartsWith(
            "Custom API request parameter 'Name' declares the table 'account', but it is a String. " +
            "A table applies only to a Entity or EntityReference or EntityCollection request parameter.",
            onString.Message);

        // Dataverse: a response property's table cannot be set for EntityCollection.
        ArgumentException onCollection = Assert.Throws<ArgumentException>(() => Light().ResponseParameter<EntityCollection>("Rows", logicalEntityName: "account"));
        Assert.StartsWith(
            "Custom API response property 'Rows' declares the table 'account', but it is a EntityCollection. " +
            "A table applies only to a Entity or EntityReference response property.",
            onCollection.Message);

        // A request EntityCollection may name its table.
        Assert.Equal("account", Input(Light().RequestParameter<EntityCollection>("Rows", logicalEntityName: "account").Build(), "Rows").LogicalEntityName);
    }

    // ---- Build() rules -------------------------------------------------------------------------------

    [Fact]
    public void A_function_without_a_response_property_fails()
    {
        InvalidOperationException light = Assert.Throws<InvalidOperationException>(() => Light().AsFunction().RequestParameter<string>("A").Build());
        Assert.Equal(
            "Custom API 'vela_Api' is a function but declares no response property. Dataverse requires a function to return at least one " +
            "response property (otherwise it is not callable): add one, or make it an action (AsAction()).",
            light.Message);

        Assert.Throws<InvalidOperationException>(() => new CustomApiBuilder<Req, EmptyResp>().UniqueName("vela_Api").AsFunction().Build());
        Assert.Single(Light().AsFunction().ResponseParameter<string>("R").Build().OutputParameters); // one is enough
        Assert.Empty(Light().AsAction().Build().OutputParameters);                                  // an action may return nothing
    }

    [Fact]
    public void A_workflow_enabled_function_fails()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            Light().AsFunction().WorkflowSdkStep(true).ResponseParameter<string>("R").Build());
        Assert.Equal(
            "Custom API 'vela_Api': WorkflowSdkStep(true) requires an action, but it is a function. Make it an action (AsAction()) or disable workflow support.",
            ex.Message);
    }

    [Theory]
    [InlineData(typeof(Entity), "Entity")]
    [InlineData(typeof(EntityCollection), "EntityCollection")]
    [InlineData(typeof(string[]), "StringArray")]
    public void A_workflow_enabled_api_rejects_entity_collection_and_string_array_parameters(Type type, string label)
    {
        InvalidOperationException request = Assert.Throws<InvalidOperationException>(() => Light().WorkflowSdkStep(true).RequestParameter("P", type).Build());
        Assert.Equal(
            $"Custom API 'vela_Api': parameter 'P' is a {label}, which a workflow-enabled Custom API cannot use " +
            "(Entity, EntityCollection and StringArray are not available in workflows). Change its type or disable workflow support.",
            request.Message);
        Assert.Throws<InvalidOperationException>(() => Light().WorkflowSdkStep(true).ResponseParameter("P", type).Build());
        Assert.Single(Light().RequestParameter("P", type).Build().InputParameters); // fine without workflow
    }

    [Fact]
    public void A_workflow_enabled_api_uses_an_EntityReference_only_when_bound_to_a_table()
    {
        InvalidOperationException global = Assert.Throws<InvalidOperationException>(() =>
            Light().WorkflowSdkStep(true).RequestParameter<EntityReference>("Ref").Build());
        Assert.Equal(
            "Custom API 'vela_Api': parameter 'Ref' is an EntityReference, which a workflow-enabled Custom API can use only " +
            "when it is bound to a table (BoundTo<TEntity>()). Bind the API, change the type or disable workflow support.",
            global.Message);
        Assert.Throws<InvalidOperationException>(() => Light().WorkflowSdkStep(true).BoundToCollection("account").RequestParameter<EntityReference>("Ref").Build());

        CustomApiDefinition bound = Light().WorkflowSdkStep(true).BoundTo("account").RequestParameter<EntityReference>("Ref").RequestParameter<int>("N").Build();
        Assert.Equal(2, bound.InputParameters.Length);
    }

    [Fact]
    public void The_light_builder_refuses_a_duplicate_parameter_name()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            Light().RequestParameter<string>("Amount").RequestParameter<int>("amount"));
        Assert.Equal(
            "Custom API parameter 'amount' was added more than once. Each request/response parameter must be added exactly once (across all .Request()/.Response() calls).",
            ex.Message);
        Assert.Throws<InvalidOperationException>(() => Light().ResponseParameter<string>("R").ResponseParameter<string>("R"));

        // Request parameters and response properties are separate tables: one name may appear in both.
        CustomApiDefinition both = Light().RequestParameter<string>("Value").ResponseParameter<string>("Value").Build();
        Assert.Equal("Value", both.InputParameters.Single().Name);
        Assert.Equal("Value", both.OutputParameters.Single().Name);
    }

    private sealed class CaseClashRequest : OrganizationRequest
    {
        public string Foo { get => (string)Parameters[nameof(Foo)]; set => Parameters[nameof(Foo)] = value; }

#pragma warning disable IDE1006 // the lower-case name is the point of the test
        public string foo { get => (string)Parameters[nameof(foo)]; set => Parameters[nameof(foo)] = value; }
#pragma warning restore IDE1006
    }

    [Fact]
    public void Inferred_parameters_differing_only_by_case_fail_like_added_ones()   // PR #428 review
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            new CustomApiBuilder<CaseClashRequest, Resp>().UniqueName("vela_Api").Build());
        Assert.StartsWith("Custom API parameter 'foo' was added more than once.", ex.Message);
    }

    [Theory]
    [InlineData("My Param")]
    [InlineData("vela_Api.Name")]
    [InlineData("a-b")]
    public void A_parameter_name_with_special_characters_fails(string name)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Light().RequestParameter<string>(name));
        Assert.StartsWith($"Custom API parameter name '{name}' is not valid: a parameter's unique name can contain only letters, digits and underscores.", ex.Message);
        Assert.Throws<ArgumentException>(() => Light().ResponseParameter<string>(name));
        Assert.Single(Light().RequestParameter<string>("Under_score1").Build().InputParameters);
    }

    // ---- shared core ---------------------------------------------------------------------------------

    private static TSelf Describe<TSelf>(TSelf builder) where TSelf : ICustomApiMetadata<TSelf>
        => builder.UniqueName("vela_Same").DisplayName("Same").Description("d").AsFunction().AsAction()
            .BoundTo<TestAccount>().ExecutePrivilege("prvReadAccount").Public().Private()
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.AsyncOnly).WorkflowSdkStep(true);

    [Fact]
    public void Both_builders_share_the_metadata_core()
    {
        CustomApiDefinition typed = Describe<ICustomApiBuilder<Req, EmptyResp>>(new CustomApiBuilder<Req, EmptyResp>()).Request(r => r.Add(x => x.Name)).Build();
        CustomApiDefinition light = Describe<ICustomApiBuilder>(new CustomApiLightBuilder()).RequestParameter<string>("Name").Build();

        foreach (CustomApiDefinition d in new[] { typed, light })
        {
            Assert.Equal("vela_Same", d.UniqueName);
            Assert.Equal("Same", d.DisplayName);
            Assert.Equal("d", d.Description);
            Assert.False(d.IsFunction);
            Assert.True(d.IsBound);
            Assert.Equal(CustomApiBindingType.Entity, d.BindingType);
            Assert.Equal("account", d.BoundEntityLogicalName);
            Assert.Equal("prvReadAccount", d.ExecutePrivilegeName);
            Assert.True(d.IsPrivate);
            Assert.Equal(CustomApiAllowedCustomProcessingStepType.AsyncOnly, d.AllowedCustomProcessingStepType);
            Assert.True(d.WorkflowSdkStepEnabled);
            Assert.Equal("vela_Same", d.PluginStep.Message);
            Assert.Equal(Vela.Constants.PluginStages.MainOperation, d.PluginStep.Stage);
            Assert.False(Input(d, "Name").IsOptional);
        }
    }
}
