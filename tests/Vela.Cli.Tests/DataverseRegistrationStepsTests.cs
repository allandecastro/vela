using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;
using TestSupport = Vela.Cli.Tests.TestSupport;

namespace Vela.Cli.Tests;

/// <summary>
/// Tests for the step upsert/reconcile helpers in <see cref="DataverseRegistration"/> (the Steps partial):
/// the step entity build, step lookup by key/signature, message &amp; filter resolution, secure-config and
/// state reconciliation, image reconciliation (create/update/delete), and the pure stage/rank/name normalizers.
/// Driven through the <see cref="IOrganizationService"/> seam so every written attribute and branch is asserted.
/// </summary>
public sealed class DataverseRegistrationStepsTests
{
    private const string Plugin = "Zava.Plugins.AccountPlugin";

    private static StepApplyDefinition Def(
        string message = "Update", string entity = "account", string stepKey = "", int stage = 40, int mode = 0,
        int order = 1, string[]? filtering = null, ImageApplyDefinition[]? pre = null, ImageApplyDefinition[]? post = null)
        => new(Plugin, message, entity, stepKey, "Execute", stage, mode, order,
            filtering ?? Array.Empty<string>(), pre ?? Array.Empty<ImageApplyDefinition>(), post ?? Array.Empty<ImageApplyDefinition>(),
            Enabled: true, UnsecureConfig: "", SecureConfig: "");

    // ---- FindStepId --------------------------------------------------------------------------------

    private static Entity StepRow(Guid id, string message, string entity, int stage, int mode, string? name = null)
    {
        var row = new Entity("sdkmessageprocessingstep", id)
        {
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["name"] = name ?? $"{Plugin} - PostOpSync",
        };
        row["msg.name"] = new AliasedValue("sdkmessage", "name", message);
        row["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", entity);
        return row;
    }

    [Fact]
    public void FindStepId_matches_by_signature_when_step_key_blank()
    {
        var match = StepRow(Guid.NewGuid(), "Update", "account", 40, 0);
        var other = StepRow(Guid.NewGuid(), "Create", "contact", 20, 0);
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(other, match) : new EntityCollection(),
        };

        Guid? found = DataverseRegistration.FindStepId(svc, Guid.NewGuid(), Def(message: "Update", entity: "account", stage: 40, mode: 0));
        Assert.Equal(match.Id, found);
    }

    [Fact]
    public void FindStepId_signature_match_requires_every_field()
    {
        // Same message/entity/stage but different mode → not a match.
        var row = StepRow(Guid.NewGuid(), "Update", "account", 40, 1);
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(row) : new EntityCollection(),
        };

        Assert.Null(DataverseRegistration.FindStepId(svc, Guid.NewGuid(), Def(message: "Update", entity: "account", stage: 40, mode: 0)));
    }

    [Fact]
    public void FindStepId_matches_by_step_key_ignoring_signature()
    {
        var row = StepRow(Guid.NewGuid(), "Different", "other", 10, 1, name: $"{Plugin} - PreValSync: my-key");
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { if (q.EntityName == "sdkmessageprocessingstep") { captured = q; return TestSupport.FakeOrgService.Collection(row); } return new EntityCollection(); },
        };

        Guid? found = DataverseRegistration.FindStepId(svc, Guid.NewGuid(), Def(stepKey: "my-key"));
        Assert.Equal(row.Id, found);

        // Query shape: filtered by eventhandler, with message + filter links.
        Assert.Equal(new[] { "sdkmessageprocessingstepid", "name", "stage", "mode" }, captured!.ColumnSet.Columns);
        Assert.Equal("eventhandler", captured.Criteria.Conditions.Single().AttributeName);
        LinkEntity msg = captured.LinkEntities.Single(l => l.EntityAlias == "msg");
        Assert.Equal("sdkmessage", msg.LinkToEntityName);
        Assert.Equal("sdkmessageid", msg.LinkFromAttributeName);
        Assert.Equal("sdkmessageid", msg.LinkToAttributeName);
        Assert.Contains("name", msg.Columns.Columns);
        LinkEntity filter = captured.LinkEntities.Single(l => l.EntityAlias == "filter");
        Assert.Equal("sdkmessagefilter", filter.LinkToEntityName);
        Assert.Equal("sdkmessagefilterid", filter.LinkFromAttributeName);
        Assert.Equal("sdkmessagefilterid", filter.LinkToAttributeName);
        Assert.Contains("primaryobjecttypecode", filter.Columns.Columns);
    }

    [Theory]
    [InlineData("Create", "account", 40, 0)] // message differs
    [InlineData("Update", "contact", 40, 0)] // entity differs
    [InlineData("Update", "account", 20, 0)] // stage differs
    [InlineData("Update", "account", 40, 1)] // mode differs
    public void FindStepId_signature_no_match_when_any_field_differs(string message, string entity, int stage, int mode)
    {
        // The live row is the canonical Update/account/40/0 signature; the sought definition differs in one field.
        var row = StepRow(Guid.NewGuid(), "Update", "account", 40, 0);
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(row) : new EntityCollection(),
        };

        Assert.Null(DataverseRegistration.FindStepId(svc, Guid.NewGuid(), Def(message: message, entity: entity, stage: stage, mode: mode)));
    }

    // ---- ResolveMessageId / ResolveMessageFilterId ------------------------------------------------

    [Fact]
    public void ResolveMessageId_returns_id_and_queries_by_name()
    {
        var id = Guid.NewGuid();
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", id)); },
        };

        Assert.Equal(id, DataverseRegistration.ResolveMessageId(svc, "Update"));
        Assert.Equal("sdkmessage", captured!.EntityName);
        Assert.Contains("sdkmessageid", captured.ColumnSet.Columns);
        Assert.Equal(1, captured.TopCount);
        ConditionExpression c = Assert.Single(captured.Criteria.Conditions);
        Assert.Equal("name", c.AttributeName);
        Assert.Equal("Update", c.Values.Single());
    }

    [Fact]
    public void ResolveMessageId_throws_when_message_absent()
    {
        var svc = new TestSupport.FakeOrgService();
        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.ResolveMessageId(svc, "Nope"));
        Assert.Contains("Dataverse message not found: Nope", ex.Message);
    }

    [Fact]
    public void ResolveMessageFilterId_returns_null_for_blank_entity_without_querying()
    {
        var svc = new TestSupport.FakeOrgService();
        Assert.Null(DataverseRegistration.ResolveMessageFilterId(svc, Guid.NewGuid(), "   "));
        Assert.Empty(svc.Queries);
    }

    [Fact]
    public void ResolveMessageFilterId_filters_by_message_and_entity()
    {
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return TestSupport.FakeOrgService.Collection(new Entity("sdkmessagefilter", filterId)); },
        };

        Assert.Equal(filterId, DataverseRegistration.ResolveMessageFilterId(svc, messageId, "account"));
        Assert.Equal("sdkmessagefilter", captured!.EntityName);
        Assert.Contains("sdkmessagefilterid", captured.ColumnSet.Columns);
        Assert.Equal(1, captured.TopCount);
        Assert.Equal(2, captured.Criteria.Conditions.Count);
        Assert.Contains(captured.Criteria.Conditions, c => c.AttributeName == "sdkmessageid" && (Guid)c.Values.Single() == messageId);
        Assert.Contains(captured.Criteria.Conditions, c => c.AttributeName == "primaryobjecttypecode" && (string)c.Values.Single() == "account");
    }

    [Fact]
    public void ResolveMessageFilterId_returns_null_when_no_filter_row()
        => Assert.Null(DataverseRegistration.ResolveMessageFilterId(new TestSupport.FakeOrgService(), Guid.NewGuid(), "account"));

    // ---- FindStepByName / GetAllPluginTypeIdsByName -----------------------------------------------

    [Fact]
    public void FindStepByName_queries_by_name_top_one()
    {
        var id = Guid.NewGuid();
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", id)); },
        };

        Assert.Equal(id, DataverseRegistration.FindStepByName(svc, "MyStep"));
        Assert.Equal("sdkmessageprocessingstep", captured!.EntityName);
        Assert.Contains("sdkmessageprocessingstepid", captured.ColumnSet.Columns);
        Assert.Equal(1, captured.TopCount);
        Assert.Equal("name", captured.Criteria.Conditions.Single().AttributeName);
        Assert.Equal("MyStep", captured.Criteria.Conditions.Single().Values.Single());
    }

    [Fact]
    public void GetAllPluginTypeIdsByName_keeps_first_id_and_skips_blank()
    {
        var first = Guid.NewGuid();
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                captured = q;
                return TestSupport.FakeOrgService.Collection(
                    new Entity("plugintype", first) { ["typename"] = "Zava.Plugins.A" },
                    new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.A" }, // duplicate → ignored
                    new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "  " });             // blank → skipped
            },
        };

        var map = DataverseRegistration.GetAllPluginTypeIdsByName(svc);
        Assert.Equal(first, map["Zava.Plugins.A"]);
        Assert.Single(map);
        Assert.Equal("plugintype", captured!.EntityName);
        Assert.Equal(new[] { "plugintypeid", "typename" }, captured.ColumnSet.Columns);
    }

    // ---- SyncStepState -----------------------------------------------------------------------------

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(false, 1, 2)]
    public void SyncStepState_sets_expected_state_and_status(bool enabled, int state, int status)
    {
        var stepId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService();

        DataverseRegistration.SyncStepState(svc, stepId, enabled);

        var req = Assert.IsType<SetStateRequest>(Assert.Single(svc.Executed));
        Assert.Equal(stepId, req.EntityMoniker.Id);
        Assert.Equal("sdkmessageprocessingstep", req.EntityMoniker.LogicalName);
        Assert.Equal(state, req.State.Value);
        Assert.Equal(status, req.Status.Value);
    }

    // ---- SyncStepSecureConfig ----------------------------------------------------------------------

    [Fact]
    public void SyncStepSecureConfig_creates_and_links_new_config()
    {
        var stepId = Guid.NewGuid();
        var configId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieve = (e, id, _) => new Entity(e, id), // step has no existing secure-config link
            OnCreate = e => e.LogicalName == "sdkmessageprocessingstepsecureconfig" ? configId : Guid.NewGuid(),
        };

        DataverseRegistration.SyncStepSecureConfig(svc, stepId, "s3cr3t");

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("sdkmessageprocessingstepsecureconfig", created.LogicalName);
        Assert.Equal("s3cr3t", created.GetAttributeValue<string>("secureconfig"));
        Entity link = Assert.Single(svc.Updated);
        Assert.Equal("sdkmessageprocessingstep", link.LogicalName);
        Assert.Equal(stepId, link.Id);
        EntityReference configRef = link.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid");
        Assert.Equal("sdkmessageprocessingstepsecureconfig", configRef.LogicalName);
        Assert.Equal(configId, configRef.Id);
    }

    [Fact]
    public void SyncStepSecureConfig_updates_existing_config_row()
    {
        var stepId = Guid.NewGuid();
        var existingConfigId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", existingConfigId) },
        };

        DataverseRegistration.SyncStepSecureConfig(svc, stepId, "updated");

        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal("sdkmessageprocessingstepsecureconfig", updated.LogicalName);
        Assert.Equal(existingConfigId, updated.Id);
        Assert.Equal("updated", updated.GetAttributeValue<string>("secureconfig"));
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void SyncStepSecureConfig_clears_and_deletes_when_value_empty()
    {
        var stepId = Guid.NewGuid();
        var existingConfigId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", existingConfigId) },
        };

        DataverseRegistration.SyncStepSecureConfig(svc, stepId, "");

        Entity link = Assert.Single(svc.Updated);
        Assert.Equal("sdkmessageprocessingstep", link.LogicalName);
        Assert.Equal(stepId, link.Id);
        Assert.Null(link.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")); // link cleared
        (string entity, Guid id) = Assert.Single(svc.Deleted);
        Assert.Equal("sdkmessageprocessingstepsecureconfig", entity);
        Assert.Equal(existingConfigId, id);
    }

    [Fact]
    public void SyncStepSecureConfig_noop_when_empty_and_none_existing()
    {
        var svc = new TestSupport.FakeOrgService();
        DataverseRegistration.SyncStepSecureConfig(svc, Guid.NewGuid(), "");
        Assert.Empty(svc.Updated);
        Assert.Empty(svc.Created);
        Assert.Empty(svc.Deleted);
    }

    // ---- SyncStepImages ----------------------------------------------------------------------------

    [Fact]
    public void SyncStepImages_creates_pre_and_post_images_with_target_property()
    {
        var stepId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService(); // no existing images
        var pre = new[] { new ImageApplyDefinition("PreImage", new[] { "name", "accountnumber" }) };
        var post = new[] { new ImageApplyDefinition("PostImage", new[] { "name" }) };

        DataverseRegistration.SyncStepImages(svc, stepId, "Update", pre, post);

        Assert.Equal(2, svc.Created.Count);
        Entity preRow = svc.Created.Single(e => e.GetAttributeValue<string>("entityalias") == "PreImage");
        Assert.Equal("sdkmessageprocessingstepimage", preRow.LogicalName);
        EntityReference stepRef = preRow.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid");
        Assert.Equal("sdkmessageprocessingstep", stepRef.LogicalName);
        Assert.Equal(stepId, stepRef.Id);
        Assert.Equal("PreImage", preRow.GetAttributeValue<string>("name"));
        Assert.Equal("accountnumber,name", preRow.GetAttributeValue<string>("attributes")); // sorted
        Assert.Equal(0, preRow.GetAttributeValue<OptionSetValue>("imagetype").Value);
        Assert.Equal("Target", preRow.GetAttributeValue<string>("messagepropertyname"));
        Entity postRow = svc.Created.Single(e => e.GetAttributeValue<string>("entityalias") == "PostImage");
        Assert.Equal(1, postRow.GetAttributeValue<OptionSetValue>("imagetype").Value);
        Assert.Equal("Target", postRow.GetAttributeValue<string>("messagepropertyname")); // non-Create message
    }

    [Fact]
    public void SyncStepImages_uses_id_property_for_create_message_post_image()
    {
        var svc = new TestSupport.FakeOrgService();
        var post = new[] { new ImageApplyDefinition("PostImage", Array.Empty<string>()) };

        DataverseRegistration.SyncStepImages(svc, Guid.NewGuid(), "Create", Array.Empty<ImageApplyDefinition>(), post);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("id", created.GetAttributeValue<string>("messagepropertyname")); // Create + post image → "id"
        Assert.Equal("", created.GetAttributeValue<string>("attributes"));
    }

    [Fact]
    public void SyncStepImages_updates_existing_image_matched_by_alias_and_type()
    {
        var stepId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var existing = new Entity("sdkmessageprocessingstepimage", existingId)
        {
            ["entityalias"] = "PreImage",
            ["imagetype"] = new OptionSetValue(0),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        var pre = new[] { new ImageApplyDefinition("PreImage", new[] { "name" }) };

        DataverseRegistration.SyncStepImages(svc, stepId, "Update", pre, Array.Empty<ImageApplyDefinition>());

        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal("sdkmessageprocessingstepimage", updated.LogicalName);
        Assert.Equal(existingId, updated.Id);
        Assert.Equal("PreImage", updated.GetAttributeValue<string>("entityalias"));
        Assert.Equal("PreImage", updated.GetAttributeValue<string>("name"));
        Assert.Equal("name", updated.GetAttributeValue<string>("attributes"));
        Assert.Equal(0, updated.GetAttributeValue<OptionSetValue>("imagetype").Value);
        Assert.Equal("Target", updated.GetAttributeValue<string>("messagepropertyname"));
        Assert.Empty(svc.Created);
        Assert.Empty(svc.Deleted);
    }

    [Fact]
    public void SyncStepImages_deletes_orphaned_image()
    {
        var stepId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();
        var orphan = new Entity("sdkmessageprocessingstepimage", orphanId)
        {
            ["entityalias"] = "Gone",
            ["imagetype"] = new OptionSetValue(0),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(orphan),
        };

        // No desired images → the existing one is orphaned and deleted.
        DataverseRegistration.SyncStepImages(svc, stepId, "Update", Array.Empty<ImageApplyDefinition>(), Array.Empty<ImageApplyDefinition>());

        (string entity, Guid id) = Assert.Single(svc.Deleted);
        Assert.Equal("sdkmessageprocessingstepimage", entity);
        Assert.Equal(orphanId, id);
    }

    [Fact]
    public void SyncStepImages_query_shape_targets_images_for_the_step()
    {
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = q => { captured = q; return new EntityCollection(); } };
        var stepId = Guid.NewGuid();

        DataverseRegistration.SyncStepImages(svc, stepId, "Update", Array.Empty<ImageApplyDefinition>(), Array.Empty<ImageApplyDefinition>());

        Assert.Equal("sdkmessageprocessingstepimage", captured!.EntityName);
        Assert.Equal(new[] { "sdkmessageprocessingstepimageid", "imagetype", "entityalias", "attributes", "name" }, captured.ColumnSet.Columns);
        Assert.Equal("sdkmessageprocessingstepid", captured.Criteria.Conditions.Single().AttributeName);
        Assert.Equal(stepId, captured.Criteria.Conditions.Single().Values.Single());
    }

    // ---- UpsertStep --------------------------------------------------------------------------------

    [Fact]
    public void UpsertStep_creates_step_with_every_column()
    {
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessageprocessingstep" => new EntityCollection(),                                        // FindStepId → no match → create
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", messageId)),
                "sdkmessagefilter" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessagefilter", filterId)),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id), // secure-config reconcile: no existing link
            OnCreate = e => e.LogicalName == "sdkmessageprocessingstep" ? stepId : Guid.NewGuid(),
        };

        JsonElement def = Json(new
        {
            pluginTypeName = Plugin,
            message = "Update",
            entityLogicalName = "account",
            stepKey = "dv-key",
            stage = 40,
            mode = 0,
            executionOrder = 7,
            filteringAttributes = new[] { "name", "accountnumber" },
            enabled = true,
            unsecureConfig = "unsec",
            secureConfig = "",
        });

        DataverseRegistration.UpsertStep(svc, new Dictionary<string, Guid> { [Plugin] = pluginTypeId }, def, verbose: false);

        Entity step = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        EntityReference handler = step.GetAttributeValue<EntityReference>("eventhandler");
        Assert.Equal("plugintype", handler.LogicalName);
        Assert.Equal(pluginTypeId, handler.Id);
        EntityReference msgRef = step.GetAttributeValue<EntityReference>("sdkmessageid");
        Assert.Equal("sdkmessage", msgRef.LogicalName);
        Assert.Equal(messageId, msgRef.Id);
        EntityReference filterRef = step.GetAttributeValue<EntityReference>("sdkmessagefilterid");
        Assert.Equal("sdkmessagefilter", filterRef.LogicalName);
        Assert.Equal(filterId, filterRef.Id);
        Assert.Equal(40, step.GetAttributeValue<OptionSetValue>("stage").Value);
        Assert.Equal(0, step.GetAttributeValue<OptionSetValue>("mode").Value);
        Assert.Equal(7, step.GetAttributeValue<int>("rank"));
        Assert.Equal("accountnumber,name", step.GetAttributeValue<string>("filteringattributes")); // sorted
        Assert.Equal(0, step.GetAttributeValue<OptionSetValue>("supporteddeployment").Value);
        Assert.Equal("unsec", step.GetAttributeValue<string>("configuration"));
        Assert.Contains("dv-key", step.GetAttributeValue<string>("name"));
        Assert.Equal(step.GetAttributeValue<string>("name"), step.GetAttributeValue<string>("description")); // description mirrors name
    }

    [Fact]
    public void UpsertStep_updates_existing_step_and_throws_for_unknown_plugin_type()
    {
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                // FindStepId matches by step key → update path.
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(StepRow(existingStepId, "Update", "account", 40, 0, name: $"{Plugin} - PostOpSync: dv-key")),
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", messageId)),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };
        JsonElement def = Json(new { pluginTypeName = Plugin, message = "Update", entityLogicalName = "", stepKey = "dv-key", stage = 40, mode = 0, enabled = true });

        DataverseRegistration.UpsertStep(svc, new Dictionary<string, Guid> { [Plugin] = pluginTypeId }, def, verbose: false);

        Entity updated = svc.Updated.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.Equal(existingStepId, updated.Id);
        Assert.DoesNotContain(svc.Created, e => e.LogicalName == "sdkmessageprocessingstep");

        // Unknown plugin type → throw.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.UpsertStep(svc, new Dictionary<string, Guid>(), def, verbose: false));
        Assert.Contains("Plugin type not found in Dataverse", ex.Message);
    }

    // ---- pure normalizers --------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    [InlineData(30, 30)]
    [InlineData(40, 40)]
    [InlineData(99, 40)] // unknown → PostOperation
    public void NormalizeRegistrationStage_keeps_known_stages_and_defaults_unknown(int input, int expected)
        => Assert.Equal(expected, DataverseRegistration.NormalizeRegistrationStage(input));

    [Theory]
    [InlineData(20, 0, 20)]  // sync keeps its stage
    [InlineData(20, 1, 40)]  // async forced to PostOperation
    [InlineData(40, 1, 40)]  // async already post
    public void NormalizeRegistrationStage_forces_async_to_post_operation(int stage, int mode, int expected)
        => Assert.Equal(expected, DataverseRegistration.NormalizeRegistrationStage(stage, mode));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public void NormalizeRegistrationRank_clamps_to_positive(int order, int expected)
        => Assert.Equal(expected, DataverseRegistration.NormalizeRegistrationRank(order));

    [Fact]
    public void BuildImageIdentityKey_combines_type_and_alias()
        => Assert.Equal("1:MyAlias", DataverseRegistration.BuildImageIdentityKey("MyAlias", 1));

    [Fact]
    public void BuildStepName_uses_step_key_when_present_else_message_and_entity()
    {
        Assert.Equal($"{Plugin} - PostOpSync: my-key", DataverseRegistration.BuildStepName(Def(stepKey: "my-key")));
        Assert.Equal($"{Plugin} - PostOpSync: Update of account", DataverseRegistration.BuildStepName(Def(message: "Update", entity: "account")));
    }
}
