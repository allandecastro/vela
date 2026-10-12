using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;
using TestSupport = Vela.Cli.Tests.TestSupport;

namespace Vela.Cli.Tests;

/// <summary>
/// #442: the step description and the Custom API display name / descriptions declared in code reach Dataverse, show
/// in the plan diff, and converge (what apply writes reads back as no change).
/// </summary>
public sealed class RegistrationMetadataTests
{
    private const string Plugin = "Zava.Plugins.AccountPlugin";
    private const string ApiPlugin = "Zava.Plugins.MyApi";
    private static readonly JsonSerializerOptions Options = new();

    // ---- Step description ------------------------------------------------------------------------------

    private static JsonElement LocalStep(string? description, string stepKey = "score")
        => DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = Plugin, message = "Update", entityLogicalName = "account", stepKey, stage = 40, mode = 0, filteringAttributes = new[] { "name" }, description }),
            Options);

    // Applies the local step to a fake org and returns the sdkmessageprocessingstep row it wrote.
    private static Entity WriteStep(JsonElement local)
    {
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                "sdkmessagefilter" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessagefilter", Guid.NewGuid())),
                _ => new EntityCollection(),
            },
        };
        DataverseRegistration.UpsertStep(svc, new Dictionary<string, Guid> { [Plugin] = Guid.NewGuid() }, local, verbose: false);
        return svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
    }

    // Reads a step back the way plan does, from the name and description stored on the row.
    private static RegistrationItem ReadStep(string name, string? description)
    {
        Guid pluginTypeId = Guid.NewGuid();
        var row = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", pluginTypeId),
            ["msg.name"] = new AliasedValue("sdkmessage", "name", "Update"),
            ["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", "account"),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["rank"] = 1,
            ["filteringattributes"] = "name",
            ["statecode"] = new OptionSetValue(0),
            ["supporteddeployment"] = new OptionSetValue(0),
            ["name"] = name,
            ["description"] = description,
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(row) : new EntityCollection(),
        };
        return Assert.Single(DataverseRegistration.GetStepItems(svc, new Dictionary<Guid, string> { [pluginTypeId] = Plugin }, Options));
    }

    private static RegistrationDiff Diff(RegistrationItem local, RegistrationItem live)
        => DiffEngine.BuildDiff(State(local), State(live));

    [Fact]
    public void Apply_writes_the_declared_description_with_the_step_key_marker_as_a_suffix()
    {
        Entity step = WriteStep(LocalStep("Recomputes the account score."));

        Assert.Equal("Recomputes the account score.\n[vela:stepkey=score]", step.GetAttributeValue<string>("description"));
    }

    [Fact]
    public void Apply_writes_the_step_name_when_no_description_is_declared()
    {
        Entity step = WriteStep(LocalStep(null));

        Assert.Equal($"{step.GetAttributeValue<string>("name")}\n[vela:stepkey=score]", step.GetAttributeValue<string>("description"));
    }

    [Fact]
    public void A_keyless_step_gets_its_description_without_a_marker()
    {
        Entity step = WriteStep(LocalStep("Keyless step.", stepKey: ""));

        Assert.Equal("Keyless step.", step.GetAttributeValue<string>("description"));
    }

    [Theory]
    [InlineData("Recomputes the account score.")]
    [InlineData(null)]
    public void What_apply_writes_reads_back_as_no_change(string? description)
    {
        JsonElement local = LocalStep(description);
        Entity written = WriteStep(local);

        RegistrationItem live = ReadStep(written.GetAttributeValue<string>("name"), written.GetAttributeValue<string>("description"));

        Assert.Equal("score", live.Definition.GetProperty("stepKey").GetString());
        RegistrationDiff diff = Diff(new RegistrationItem("step", Plugin, local), live);
        Assert.Empty(diff.Changed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void A_step_deployed_before_descriptions_were_written_does_not_churn()
    {
        // Older CLIs wrote "{name}\n{marker}" for every step: a step that still declares no description matches it.
        JsonElement local = LocalStep(null);
        string name = local.GetProperty("name").GetString()!;

        RegistrationDiff diff = Diff(new RegistrationItem("step", Plugin, local), ReadStep(name, $"{name}\n[vela:stepkey=score]"));

        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Plan_shows_a_description_change_on_the_step()
    {
        JsonElement local = LocalStep("New text.");
        string name = local.GetProperty("name").GetString()!;

        RegistrationDiff diff = Diff(new RegistrationItem("step", Plugin, local), ReadStep(name, "Old text.\n[vela:stepkey=score]"));

        RegistrationChange change = Assert.Single(diff.Changed);
        Assert.Equal("Old text.", change.Before.GetProperty("description").GetString());
        Assert.Equal("New text.", change.After.GetProperty("description").GetString());
    }

    [Fact]
    public void A_case_only_change_to_a_declared_description_is_a_change()
    {
        JsonElement local = LocalStep("Recompute the score");
        string name = local.GetProperty("name").GetString()!;

        RegistrationDiff diff = Diff(new RegistrationItem("step", Plugin, local), ReadStep(name, "recompute the score\n[vela:stepkey=score]"));

        Assert.Single(diff.Changed);
    }

    [Fact]
    public void The_step_key_is_read_from_the_marker_after_the_developer_text()
    {
        // The developer's text comes first and may even quote a marker: the trailing one is the step's key.
        string description = "See [vela:stepkey=other] for history\n[vela:stepkey=score]";

        Assert.Equal("score", DefinitionParser.ExtractStepKeyFromMarker(description));
        Assert.Equal("See [vela:stepkey=other] for history", DefinitionParser.StripStepKeyMarker(description));
        Assert.Equal("score", DefinitionParser.ResolveStepKey(description, "renamed by an admin", Plugin));
    }

    // PR #468 review: only the marker BuildStepDescription writes (the final non-whitespace line) is a key. A marker the
    // developer's text quotes anywhere else is neither read as a key nor stripped from the description.
    [Theory]
    [InlineData("See [vela:stepkey=other] for history")]
    [InlineData("[vela:stepkey=other]\nis how Vela tags a step")]
    [InlineData("Tagged [vela:stepkey=other]")]
    [InlineData("Tagged\n[vela:stepkey=other] later")]
    [InlineData("Empty\n[vela:stepkey=]")]
    public void A_marker_that_is_not_the_trailing_line_is_not_a_step_key(string description)
    {
        Assert.Null(DefinitionParser.ExtractStepKeyFromMarker(description));
        Assert.Equal(description, DefinitionParser.StripStepKeyMarker(description));
    }

    [Theory]
    [InlineData("phase-two [v2")]
    [InlineData("credit.review_2")]
    public void A_valid_step_key_round_trips_through_the_description_marker(string key)
    {
        string description = $"Recompute\n{DefinitionParser.StepKeyMarker(key)}";
        Assert.Equal(key, DefinitionParser.ExtractStepKeyFromMarker(description));
        Assert.Equal("Recompute", DefinitionParser.StripStepKeyMarker(description));
    }

    [Theory]
    [InlineData("[vela:stepkey=score]", "")]
    [InlineData("recompute\r\n[vela:stepkey=score]\r\n", "recompute")]
    [InlineData("recompute\n  [vela:stepkey=score]  \n\n", "recompute")]
    public void The_trailing_marker_line_is_read_and_stripped(string description, string text)
    {
        Assert.Equal("score", DefinitionParser.ExtractStepKeyFromMarker(description));
        Assert.Equal(text, DefinitionParser.StripStepKeyMarker(description));
    }

    [Fact]
    public void StripStepKeyMarker_returns_the_trimmed_text_when_there_is_no_marker()
    {
        Assert.Equal("Plain text", DefinitionParser.StripStepKeyMarker("  Plain text \n"));
        Assert.Equal(string.Empty, DefinitionParser.StripStepKeyMarker(null));
    }

    [Fact]
    public void A_description_too_long_for_dataverse_is_rejected_with_the_room_left_by_the_marker()
    {
        StepApplyDefinition fits = DefinitionParser.ParseStepDefinition(LocalStep(new string('a', 256 - "\n[vela:stepkey=score]".Length)));
        StepApplyDefinition tooLong = DefinitionParser.ParseStepDefinition(LocalStep(new string('a', 256 - "\n[vela:stepkey=score]".Length + 1)));

        Assert.Null(DataverseRegistration.GetStepDescriptionLengthError(fits));
        string? error = DataverseRegistration.GetStepDescriptionLengthError(tooLong);
        Assert.NotNull(error);
        Assert.Contains("at most 235", error);
    }

    // ---- Custom API display name and descriptions ------------------------------------------------------

    private static JsonElement LocalApi(string? displayName, string? description, string? inputDescription = null, string? outputDescription = null)
        => DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new
            {
                pluginTypeName = ApiPlugin,
                uniqueName = "zava_DoThing",
                displayName,
                description,
                inputParameters = new[] { new { name = "Amount", parameterTypeName = "System.Decimal", isOptional = true, description = inputDescription } },
                outputParameters = new[] { new { name = "Result", parameterTypeName = "System.String", description = outputDescription } },
            }),
            Options);

    private static TestSupport.FakeOrgService WriteApi(JsonElement local)
    {
        var svc = new TestSupport.FakeOrgService();
        DataverseRegistration.UpsertCustomApi(svc, new Dictionary<string, Guid> { [ApiPlugin] = Guid.NewGuid() }, local, verbose: false);
        return svc;
    }

    // Reads the rows a fake apply created back the way plan does.
    private static RegistrationItem ReadApi(TestSupport.FakeOrgService written)
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid apiId = Guid.NewGuid();
        Entity Row(Entity source)
        {
            var row = new Entity(source.LogicalName, Guid.NewGuid());
            foreach (KeyValuePair<string, object> attribute in source.Attributes)
            {
                row[attribute.Key] = attribute.Value;
            }

            row[source.LogicalName == "customapi" ? "plugintypeid" : "customapiid"] = source.LogicalName == "customapi"
                ? new EntityReference("plugintype", pluginTypeId)
                : new EntityReference("customapi", apiId);
            return row;
        }

        Entity api = Row(written.Created.Single(e => e.LogicalName == "customapi"));
        api.Id = apiId;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => TestSupport.FakeOrgService.Collection(
                written.Created.Where(e => e.LogicalName == q.EntityName).Select(e => e.LogicalName == "customapi" ? api : Row(e)).ToArray()),
        };
        return Assert.Single(DataverseRegistration.GetCustomApiItems(svc, new Dictionary<Guid, string> { [pluginTypeId] = ApiPlugin }, Options));
    }

    [Fact]
    public void Apply_writes_the_declared_display_name_and_descriptions()
    {
        TestSupport.FakeOrgService svc = WriteApi(LocalApi("Do the thing", "Does the thing.", "The amount.", "The result."));

        Entity api = svc.Created.Single(e => e.LogicalName == "customapi");
        Assert.Equal("Do the thing", api["displayname"]);
        Assert.Equal("Does the thing.", api["description"]);
        Assert.Equal("zava_DoThing", api["name"]);
        Assert.Equal("The amount.", svc.Created.Single(e => e.LogicalName == "customapirequestparameter")["description"]);
        Assert.Equal("The result.", svc.Created.Single(e => e.LogicalName == "customapiresponseproperty")["description"]);
    }

    [Fact]
    public void Apply_falls_back_to_the_unique_name_and_the_parameter_name()
    {
        TestSupport.FakeOrgService svc = WriteApi(LocalApi(null, null));

        Entity api = svc.Created.Single(e => e.LogicalName == "customapi");
        Assert.Equal("zava_DoThing", api["displayname"]);
        Assert.Equal("zava_DoThing", api["description"]);
        Assert.Equal("Amount", svc.Created.Single(e => e.LogicalName == "customapirequestparameter")["description"]);
        Assert.Equal("Result", svc.Created.Single(e => e.LogicalName == "customapiresponseproperty")["description"]);
    }

    [Theory]
    [InlineData("Do the thing", "Does the thing.", "The amount.", "The result.")]
    [InlineData(null, null, null, null)]
    public void What_apply_writes_for_a_custom_api_reads_back_as_no_change(string? displayName, string? description, string? inputDescription, string? outputDescription)
    {
        JsonElement local = LocalApi(displayName, description, inputDescription, outputDescription);

        RegistrationItem live = ReadApi(WriteApi(local));

        Assert.Empty(Diff(new RegistrationItem("custom-api", ApiPlugin, local), live).Changed);
    }

    [Fact]
    public void Plan_shows_a_display_name_or_parameter_description_change()
    {
        RegistrationItem live = ReadApi(WriteApi(LocalApi("Old name", null, "Old amount.")));

        RegistrationChange change = Assert.Single(Diff(new RegistrationItem("custom-api", ApiPlugin, LocalApi("New name", null, "New amount.")), live).Changed);
        Assert.Equal("Old name", change.Before.GetProperty("displayName").GetString());
        Assert.Equal("New name", change.After.GetProperty("displayName").GetString());
        Assert.Equal("New amount.", change.After.GetProperty("inputParameters")[0].GetProperty("description").GetString());
    }

    [Fact]
    public void Apply_updates_the_display_name_and_description_in_place()
    {
        Guid apiId = Guid.NewGuid();
        Guid pluginTypeId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "customapi"
                ? TestSupport.FakeOrgService.Collection(new Entity("customapi", apiId))
                : new EntityCollection(),
            OnRetrieve = (entity, id, _) => new Entity(entity, id)
            {
                ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
                ["bindingtype"] = new OptionSetValue(0),
                ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            },
        };

        DataverseRegistration.UpsertCustomApi(svc, new Dictionary<string, Guid> { [ApiPlugin] = pluginTypeId }, LocalApi("Do the thing", "Does the thing."), verbose: false);

        Entity update = svc.Updated.Single(e => e.LogicalName == "customapi");
        Assert.Equal(apiId, update.Id);
        Assert.Equal("Do the thing", update["displayname"]);
        Assert.Equal("Does the thing.", update["description"]);
    }

    [Fact]
    public void A_custom_api_deployed_before_labels_were_written_does_not_churn_on_unique_name_casing()
    {
        // Older CLIs wrote the unique name into displayname and description; a casing drift of that default is
        // folded like the unique name itself (#305).
        JsonElement live = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { pluginTypeName = ApiPlugin, uniqueName = "zava_dothing", displayName = "zava_dothing", description = "zava_dothing" }), Options);
        JsonElement local = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { pluginTypeName = ApiPlugin, uniqueName = "zava_DoThing" }), Options);

        Assert.Empty(Diff(new RegistrationItem("custom-api", ApiPlugin, local), new RegistrationItem("custom-api", ApiPlugin, live)).Changed);
    }

    [Theory]   // PR #468 review
    [InlineData("displayName")]
    [InlineData("description")]
    public void A_declared_label_differing_from_the_unique_name_only_in_case_is_a_change(string label)
    {
        // The live label is the legacy fallback (the unique name); the code now declares a label that differs from it
        // only in case. It was declared, so it compares exactly and apply writes it.
        JsonElement live = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { pluginTypeName = ApiPlugin, uniqueName = "zava_DoThing", displayName = "zava_DoThing", description = "zava_DoThing" }), Options);
        JsonElement local = DefinitionNormalizer.NormalizeCustomApiDefinition(
            JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["pluginTypeName"] = ApiPlugin, ["uniqueName"] = "zava_DoThing", [label] = "zava_dothing" }), Options);

        RegistrationChange change = Assert.Single(Diff(new RegistrationItem("custom-api", ApiPlugin, local), new RegistrationItem("custom-api", ApiPlugin, live)).Changed);
        Assert.Equal("zava_dothing", change.After.GetProperty(label).GetString());
    }

    [Fact]
    public void A_declared_step_description_differing_from_the_step_name_only_in_case_is_a_change()   // PR #468 review
    {
        JsonElement local = LocalStep(description: null);
        string name = local.GetProperty("name").GetString()!;
        JsonElement declared = LocalStep(name.ToUpperInvariant());

        // The live description is the fallback (the step name); the declared one differs only in case.
        Assert.Single(Diff(new RegistrationItem("step", Plugin, declared), ReadStep(name, $"{name}\n[vela:stepkey=score]")).Changed);
        Assert.Empty(Diff(new RegistrationItem("step", Plugin, local), ReadStep(name, $"{name.ToLowerInvariant()}\n[vela:stepkey=score]")).Changed);
    }

    [Fact]
    public void Labels_too_long_for_dataverse_are_rejected()
    {
        CustomApiApplyDefinition ok = DefinitionParser.ParseCustomApiDefinition(LocalApi(new string('a', 100), new string('b', 300), new string('c', 300), new string('d', 100)));
        CustomApiApplyDefinition tooLong = DefinitionParser.ParseCustomApiDefinition(LocalApi(new string('a', 101), new string('b', 301), new string('c', 301), new string('d', 101)));

        Assert.Null(DataverseRegistration.GetCustomApiLabelLengthError(ok));
        string? error = DataverseRegistration.GetCustomApiLabelLengthError(tooLong);
        Assert.NotNull(error);
        Assert.Contains("the display name has 101 characters", error);
        Assert.Contains("the description has 301 characters", error);
        Assert.Contains("request parameter 'Amount' has 301 characters", error);
        Assert.Contains("response property 'Result' has 101 characters", error);
    }
}
