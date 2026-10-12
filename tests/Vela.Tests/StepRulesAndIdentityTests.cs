using System;
using System.Collections.Generic;
using System.Linq;
using ContractPluginFixture;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The step rules shared by the runtime, the analyzers and the CLI (StepImageRules), the step identity of W1
// (derived keys, uniqueness, relationship scope and the executing step's selection), and the reflection reader.
public sealed class StepRulesAndIdentityTests
{
    // ---- StepImageRules ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Create", "Id")]
    [InlineData(" CREATEMULTIPLE ", "Ids")]
    [InlineData("UpdateMultiple", "Targets")]
    [InlineData("SetState", "EntityMoniker")]
    [InlineData("SetStateDynamicEntity", "EntityMoniker")]
    [InlineData("Send", "EmailId")]
    [InlineData("DeliverIncoming", "EmailId")]
    [InlineData("DeliverPromote", "EmailId")]
    [InlineData("RouteTo", null)]
    [InlineData("Update", "Target")]
    [InlineData(null, "Target")]
    public void Each_message_registers_its_images_on_its_request_property(string? message, string? property)
    {
        Assert.Equal(property, StepImageRules.GetMessagePropertyName(message));
        Assert.Equal(property, StepImageRules.ResolveMessagePropertyName(message, "  "));
        Assert.Equal("SubordinateId", StepImageRules.ResolveMessagePropertyName(message, " SubordinateId "));
    }

    [Fact]
    public void An_explicit_image_property_is_checked_against_the_message()
    {
        Assert.Null(StepImageRules.GetMessagePropertyRejectionReason("Update", null));
        Assert.Null(StepImageRules.GetMessagePropertyRejectionReason("Update", " target "));
        Assert.Null(StepImageRules.GetMessagePropertyRejectionReason(" Merge ", "subordinateid"));
        Assert.Equal("only a Merge step can capture an image of its subordinate record (SubordinateId)",
            StepImageRules.GetMessagePropertyRejectionReason("Update", "SubordinateId"));
        Assert.Equal("Vela registers images on this message against 'Target' only, not 'Ids'",
            StepImageRules.GetMessagePropertyRejectionReason("Update", "Ids"));
        Assert.Equal("Vela registers images on this message against 'nothing' only, not 'Target'",
            StepImageRules.GetMessagePropertyRejectionReason("RouteTo", "Target"));
    }

    [Fact]
    public void Images_are_refused_where_the_platform_cannot_provide_them()
    {
        Assert.Equal("no image exists at MainOperation", StepImageRules.GetRejectionReason("Update", 30, isPreImage: true));
        Assert.Equal("a post-image only exists at PostOperation, after the write", StepImageRules.GetRejectionReason("Update", 20, isPreImage: false));
        Assert.Equal("Dataverse supports no image on this message", StepImageRules.GetRejectionReason("RouteTo", 40, isPreImage: true));
        Assert.Equal("there is no prior row state before a create", StepImageRules.GetRejectionReason("Create", 40, isPreImage: true));
        Assert.Equal("there is no prior row state before a create", StepImageRules.GetRejectionReason(" createmultiple ", 10, isPreImage: true));
        Assert.Equal("the row no longer exists after a delete", StepImageRules.GetRejectionReason("Delete", 40, isPreImage: false));
        Assert.Null(StepImageRules.GetRejectionReason("Update", 10, isPreImage: true));
        Assert.True(StepImageRules.IsImageAllowed("Update", 40, isPreImage: false));
        Assert.False(StepImageRules.IsImageAllowed("Create", 40, isPreImage: true));
    }

    // ---- StepIdentity: keys ---------------------------------------------------------------------------------------

    private static StepDefinition Step(string? key = null, string[]? filters = null, ImageDefinition[]? pre = null, string[]? relationships = null, int rank = 1) => new()
    {
        Message = "Update",
        EntityLogicalName = "account",
        Stage = PluginStages.PostOperation,
        Mode = ExecutionMode.Synchronous,
        StepKey = key ?? string.Empty,
        FilteringAttributes = filters ?? [],
        PreImages = pre ?? [],
        Relationships = relationships ?? [],
        ExecutionOrder = rank,
    };

    [Fact]
    public void The_first_step_of_a_trigger_keeps_the_historical_key_and_the_next_add_their_shape()
    {
        StepDefinition first = Step(filters: ["name"], rank: 9);
        StepDefinition second = Step(
            filters: ["B", "a"],
            pre: [new ImageDefinition { Alias = "X", Attributes = ["c", "B"] }, new ImageDefinition { Alias = "Older", Attributes = ["b", "a"] }],
            relationships: ["r2", "R1"]);
        StepDefinition keyed = Step(key: "mine");

        StepIdentity.AssignStepKeys([first, keyed, second], "P");

        Assert.Equal("dv-516b9ec03b30", first.StepKey); // plugin|trigger|1: the rank is not part of it
        Assert.Equal("dv-bbf9e500b991", second.StepKey); // + sorted, lower-cased filters, images and relationships
        Assert.Equal("mine", keyed.StepKey);
    }

    [Fact]
    public void Two_steps_nothing_tells_apart_are_refused()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => StepIdentity.AssignStepKeys([Step(filters: ["a", "b"]), Step(filters: ["B", "A"])], "P"));

        Assert.Equal("Plugin 'P' registers two steps for the same trigger (message 'Update', stage 40, mode 0) that cannot be told apart: they have the same filtering attributes and images. Give one of them a .WithStepKey(\"...\"), or merge them into one step.", ex.Message);
    }

    [Fact]
    public void A_step_key_used_twice_is_refused_with_the_reason()
    {
        InvalidOperationException sameTrigger = Assert.Throws<InvalidOperationException>(
            () => StepIdentity.AssignStepKeys([Step(key: "k", filters: ["a"]), Step(key: "K", filters: ["b"])], "P"));
        StepDefinition delete = Step(key: "k");
        delete.Message = "Delete";
        InvalidOperationException otherTrigger = Assert.Throws<InvalidOperationException>(
            () => StepIdentity.AssignStepKeys([Step(key: "k"), delete], "P"));

        Assert.Equal("Plugin 'P' registers two steps for the same trigger (message 'Update', stage 40, mode 0) that cannot be told apart: they have the same filtering attributes, images and step key. Give one of them a distinct .WithStepKey(\"...\"), or merge them into one step.", sameTrigger.Message);
        Assert.Equal("Plugin 'P' registers two steps with the step key 'k'. Step keys must be unique within a plugin.", otherTrigger.Message);
    }

    // ---- StepIdentity: dispatch ----------------------------------------------------------------------------------

    private static LocalPluginContext Context(string? relationship = null, EntityReference? owning = null, IOrganizationService? system = null)
    {
        PluginContextBuilder builder = new();
        builder.OnUpdate();
        builder.Context.Stage = 40;
        if (relationship != null)
        {
            builder.WithInputParameter("Relationship", new Relationship(relationship));
        }

        builder.Context.OwningExtension = owning;
        if (system != null)
        {
            builder.WithSystemService(system);
        }

        return builder.Build();
    }

    [Fact]
    public void A_relationship_scoped_step_runs_only_for_its_relationships()
    {
        StepDefinition scoped = Step(relationships: ["contact_accounts"]);

        Assert.True(StepIdentity.IsInRelationshipScope(Step(), Context("other")));
        Assert.True(StepIdentity.IsInRelationshipScope(scoped, Context()));
        Assert.True(StepIdentity.IsInRelationshipScope(scoped, Context("CONTACT_ACCOUNTS")));
        Assert.False(StepIdentity.IsInRelationshipScope(scoped, Context("other")));
        Assert.Empty(StepIdentity.Select([scoped], Context("other"), "P"));
    }

    [Fact]
    public void The_executing_step_selects_its_own_definition_by_name()
    {
        StepDefinition a = Step(key: "alpha");
        StepDefinition b = Step(key: "beta");

        Assert.Same(b, Assert.Single(StepIdentity.Select([a, b], Context(owning: new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()) { Name = "P - PostOpSync: BETA" }), "P")));
        Assert.Same(a, Assert.Single(StepIdentity.Select([a, b], Context(owning: new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()) { Name = " alpha " }), "P")));
        Assert.Same(a, Assert.Single(StepIdentity.Select([a], Context(), "P")));
    }

    [Fact]
    public void A_sibling_relationship_row_does_not_run_another_scope_handler()
    {
        StepDefinition a = Step(key: "rel-a", relationships: ["rel_a"]);
        StepDefinition b = Step(key: "rel-b", relationships: ["rel_b"]);
        EntityReference rowA = new("sdkmessageprocessingstep", Guid.NewGuid()) { Name = "P - PostOpSync: rel-a" };
        EntityReference rowB = new("sdkmessageprocessingstep", Guid.NewGuid()) { Name = "P - PostOpSync: rel-b" };

        // Associate registrations are global: a rel_a request runs both rows, and only the rel-a row runs its handler.
        Assert.Same(a, Assert.Single(StepIdentity.Select([a, b], Context("rel_a", rowA), "P")));
        Assert.Empty(StepIdentity.Select([a, b], Context("rel_a", rowB), "P"));
        Assert.Same(b, Assert.Single(StepIdentity.Select([a, b], Context("rel_b", rowB), "P")));
        // A legacy deployment whose step names carry no key still falls back to the relationship scope.
        Assert.Same(a, Assert.Single(StepIdentity.Select([a, b], Context("rel_a"), "P")));
    }

    [Fact]
    public void Images_on_different_request_properties_get_distinct_keys()
    {
        StepDefinition target = Step(pre: [new ImageDefinition { Alias = "img", Attributes = ["name"] }]);
        StepDefinition subordinate = Step(pre: [new ImageDefinition { Alias = "img", Attributes = ["name"], MessagePropertyName = "SubordinateId" }]);
        target.Message = subordinate.Message = "Merge";

        StepIdentity.AssignStepKeys([target, subordinate], "P");

        Assert.NotEqual(target.StepKey, subordinate.StepKey);
        Assert.Throws<InvalidOperationException>(() => StepIdentity.AssignStepKeys(
            [Step(pre: [new ImageDefinition { Alias = "img", MessagePropertyName = "SubordinateId" }]),
             Step(pre: [new ImageDefinition { Alias = "img", MessagePropertyName = "subordinateid" }])], "P"));
    }

    [Fact]
    public void An_asynchronous_step_is_identified_by_reading_its_row()
    {
        Guid stepId = Guid.NewGuid();
        InMemoryOrganizationService org = new InMemoryOrganizationService().Seed(
            new Entity("sdkmessageprocessingstep", stepId) { ["name"] = "P - PostOpAsync: beta" });
        StepDefinition a = Step(key: "alpha");
        StepDefinition b = Step(key: "beta");

        Assert.Same(b, Assert.Single(StepIdentity.Select([a, b], Context(owning: new EntityReference("sdkmessageprocessingstep", stepId), system: org), "P")));
    }

    [Fact]
    public void Several_steps_without_an_identifiable_executing_step_are_refused()
    {
        StepDefinition a = Step(key: "alpha");
        StepDefinition b = Step(key: "beta");

        InvalidPluginExecutionException none = Assert.Throws<InvalidPluginExecutionException>(() => StepIdentity.Select([a, b], Context(), "P"));
        InvalidPluginExecutionException unknown = Assert.Throws<InvalidPluginExecutionException>(() => StepIdentity.Select([a, b],
            Context(owning: new EntityReference("sdkmessageprocessingstep", Guid.Empty) { Name = "P - PostOpSync: gamma" }), "P"));

        Assert.Equal("Plugin 'P' has 2 steps for message 'Update' at stage 40, mode 0, and the executing step '' does not name one of their step keys (alpha, beta). Redeploy the plugin with 'vela apply' so each step carries its key in its name.", none.Message);
        Assert.Contains("the executing step 'P - PostOpSync: gamma'", unknown.Message, StringComparison.Ordinal);
    }

    // ---- StepDefinitionReader and defaults -----------------------------------------------------------------------

    private sealed class ThrowingPlugin(string u, string s) : EntityPluginBase<TestAccount>(u, s)
    {
        protected override void RegisterPluginSteps() => throw new InvalidOperationException("broken registration");
    }

    private sealed class NoStandardConstructorPlugin() : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps() => RegisterStep().OnDelete().PreOperation().Sync().Handle(_ => { });
    }

    [Fact]
    public void The_reader_reads_every_plugin_of_an_assembly()
    {
        StepDefinition[] steps = StepDefinitionReader.GetStepDefinitions(typeof(AccountContractPlugin).Assembly).ToArray();

        Assert.Equal(7, steps.Length);
        Assert.Equal(6, steps.Count(s => s.PluginTypeName == typeof(AccountContractPlugin).FullName));
    }

    [Fact]
    public void The_reader_surfaces_a_failing_plugin_and_skips_what_is_not_readable()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => StepDefinitionReader.GetStepDefinitionFromType(typeof(ThrowingPlugin)));

        Assert.Equal($"Failed to read step definitions from plugin '{typeof(ThrowingPlugin).FullName}': broken registration", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("broken registration", ex.InnerException!.Message);
        Assert.Null(StepDefinitionReader.GetStepDefinitionFromType(typeof(string)));
        Assert.Null(StepDefinitionReader.GetStepDefinitionFromType(typeof(NoStandardConstructorPlugin)));
        Assert.Equal("pluginType", Assert.Throws<ArgumentNullException>(() => StepDefinitionReader.GetStepDefinitionFromType(null!)).ParamName);
        Assert.Equal("assembly", Assert.Throws<ArgumentNullException>(() => StepDefinitionReader.GetStepDefinitions(null!).ToArray()).ParamName);
    }

    [Fact]
    public void A_new_definition_starts_empty_enabled_and_unscoped()
    {
        StepDefinition d = new();
        ImageDefinition image = new();

        Assert.Equal(string.Empty, d.PluginTypeName + d.EntityLogicalName + d.Message + d.StepKey + d.Description + d.HandlerMethodName + d.UnsecureConfig + d.SecureConfigName);
        Assert.Equal(1, d.ExecutionOrder);
        Assert.True(d.Enabled);
        Assert.False(d.AsyncAutoDelete);
        Assert.Null(d.SkipWhenDepthAbove);
        Assert.Empty(d.Relationships);
        Assert.Equal(string.Empty, image.Alias + image.MessagePropertyName);
        Assert.Empty(image.Attributes);
    }
}
