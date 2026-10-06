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
/// Unit tests for the Custom API upsert/delete helpers in <see cref="DataverseRegistration"/> (the CustomApi
/// partial): the customapi entity build, parameter/property reconciliation (create/update/delete), the
/// uniquename lookup, and the parameter type-code resolver — driven through the <see cref="IOrganizationService"/>
/// seam so every written attribute, query condition and branch is asserted.
/// </summary>
public sealed class DataverseRegistrationCustomApiTests
{
    private const string Plugin = "Zava.Plugins.MyApi";

    [Fact]
    public void ComputeCustomApiPrefixDiagnostics_warns_for_each_unprefixed_unique_name()
    {
        IReadOnlyList<string> diagnostics = DataverseRegistration.ComputeCustomApiPrefixDiagnostics(
            "acme",
            new[] { "acme_DoThing", "DoOtherThing", "wrong_Api" });

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Contains("'DoOtherThing'") && d.Contains("'acme_'") && d.Contains("'acme_DoOtherThing'"));
        Assert.Contains(diagnostics, d => d.Contains("'wrong_Api'"));
    }

    [Fact]
    public void ComputeCustomApiPrefixDiagnostics_matches_the_prefix_case_insensitively()
    {
        Assert.Empty(DataverseRegistration.ComputeCustomApiPrefixDiagnostics("acme", new[] { "ACME_DoThing" }));
    }

    [Fact]
    public void ComputeCustomApiPrefixDiagnostics_is_empty_when_all_names_carry_the_prefix()
    {
        Assert.Empty(DataverseRegistration.ComputeCustomApiPrefixDiagnostics("acme", new[] { "acme_One", "acme_Two" }));
    }

    private static (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) NewService(Entity? existingApi = null)
    {
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "customapi" && existingApi != null
                ? TestSupport.FakeOrgService.Collection(existingApi)
                : new EntityCollection(),
        };
        return (svc, new Dictionary<string, Guid> { [Plugin] = Guid.NewGuid() });
    }

    // ---- UpsertCustomApi --------------------------------------------------------------------------

    [Fact]
    public void UpsertCustomApi_creates_api_with_every_column()
    {
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        Guid pluginTypeId = types[Plugin];
        JsonElement def = Json(new
        {
            pluginTypeName = Plugin,
            uniqueName = "zava_DoThing",
            // A workflow-enabled Custom API must be an action (IsFunction=false); the two are mutually exclusive
            // and now validated, so this fixture uses that valid combination while still exercising every column.
            isFunction = false,
            isBound = true,
            boundEntityLogicalName = "account",
            executePrivilegeName = "prvDoThing",
            isPrivate = true,
            allowedCustomProcessingStepType = 1,
            bindingType = 2,
            workflowSdkStepEnabled = true,
        });

        DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false);

        Entity api = Assert.Single(svc.Created);
        Assert.Equal("customapi", api.LogicalName);
        Assert.Equal("zava_DoThing", api.GetAttributeValue<string>("uniquename"));
        Assert.Equal("zava_DoThing", api.GetAttributeValue<string>("name"));
        Assert.Equal("zava_DoThing", api.GetAttributeValue<string>("displayname"));
        Assert.Equal("zava_DoThing", api.GetAttributeValue<string>("description"));
        Assert.False(api.GetAttributeValue<bool>("isfunction"));
        Assert.Equal("account", api.GetAttributeValue<string>("boundentitylogicalname"));
        Assert.Equal("prvDoThing", api.GetAttributeValue<string>("executeprivilegename"));
        Assert.True(api.GetAttributeValue<bool>("isprivate"));
        Assert.Equal(1, api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype").Value);
        Assert.Equal(2, api.GetAttributeValue<OptionSetValue>("bindingtype").Value);
        Assert.True(api.GetAttributeValue<bool>("workflowsdkstepenabled"));
        EntityReference typeRef = api.GetAttributeValue<EntityReference>("plugintypeid");
        Assert.Equal("plugintype", typeRef.LogicalName);
        Assert.Equal(pluginTypeId, typeRef.Id);
    }

    [Fact]
    public void UpsertCustomApi_leaves_bound_and_privilege_null_when_blank()
    {
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        JsonElement def = Json(new
        {
            pluginTypeName = Plugin,
            uniqueName = "zava_Unbound",
            isFunction = false,
            isBound = false,
            boundEntityLogicalName = "   ",
            executePrivilegeName = "",
            isPrivate = false,
        });

        DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false);

        Entity api = Assert.Single(svc.Created);
        Assert.Null(api.GetAttributeValue<string>("boundentitylogicalname"));
        Assert.Null(api.GetAttributeValue<string>("executeprivilegename"));
        Assert.False(api.GetAttributeValue<bool>("isfunction"));
        Assert.False(api.GetAttributeValue<bool>("isprivate"));
    }

    [Fact]
    public void UpsertCustomApi_updates_existing_api_by_id()
    {
        var existingId = Guid.NewGuid();
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService(new Entity("customapi", existingId));
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing", isFunction = false, isBound = false, isPrivate = false });

        DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false);

        Entity api = Assert.Single(svc.Updated);
        Assert.Equal("customapi", api.LogicalName);
        Assert.Equal(existingId, api.Id);
        Assert.Empty(svc.Created);
    }

    [Theory]
    // Dataverse fixes isFunction, bindingType and the bound entity at creation and ignores updates to them,
    // so a local change to any must fail fast rather than report a phantom, never-converging update.
    [InlineData("isfunction", true, 0, "", new object[] { "isFunction" })]
    [InlineData("bindingtype", false, 1, "", new object[] { "bindingType" })]
    [InlineData("boundentitylogicalname", false, 0, "account", new object[] { "boundEntity" })]
    public void UpsertCustomApi_throws_when_an_immutable_field_differs_from_the_live_api(
        string liveColumn, bool liveIsFunction, int liveBindingType, string liveBoundEntity, object[] expectedInMessage)
    {
        var existingId = Guid.NewGuid();
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService(new Entity("customapi", existingId));
        var live = new Entity("customapi", existingId)
        {
            ["isfunction"] = liveIsFunction,
            ["bindingtype"] = new OptionSetValue(liveBindingType),
            ["boundentitylogicalname"] = string.IsNullOrEmpty(liveBoundEntity) ? null : liveBoundEntity,
        };
        svc.OnRetrieve = (_, _, _) => live;
        // The desired shape leaves every immutable field at its default (action, global, unbound); exactly one
        // therefore differs from the seeded live value named by liveColumn.
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing", isFunction = false, isBound = false, isPrivate = false });

        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Contains("zava_DoThing", ex.Message);
        Assert.Contains("cannot be changed", ex.Message);
        Assert.Contains((string)expectedInMessage[0], ex.Message);
        // The message explains why (no convergence) and both ways out, so the fix is actionable on its own.
        Assert.EndsWith(
            "cannot be changed on an existing Custom API. These are set at creation and Dataverse ignores such updates, so applying " +
            "would report success but never converge. Delete the Custom API and re-apply to recreate it with " +
            "the new shape, or revert the change.", ex.Message);
        Assert.Empty(svc.Updated);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_allowedCustomProcessingStepType_changes()
    {
        var existingId = Guid.NewGuid();
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService(new Entity("customapi", existingId));
        svc.OnRetrieve = (_, _, _) => new Entity("customapi", existingId) { ["allowedcustomprocessingsteptype"] = new OptionSetValue(1) };
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing", allowedCustomProcessingStepType = 0 });

        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Contains("allowedCustomProcessingStepType", ex.Message);
        Assert.Contains("cannot be changed", ex.Message);
        Assert.Empty(svc.Updated);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_workflowSdkStepEnabled_changes()
    {
        var existingId = Guid.NewGuid();
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService(new Entity("customapi", existingId));
        svc.OnRetrieve = (_, _, _) => new Entity("customapi", existingId) { ["workflowsdkstepenabled"] = true };
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing", workflowSdkStepEnabled = false });

        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Contains("workflowSdkStepEnabled", ex.Message);
        Assert.Contains("cannot be changed", ex.Message);
        Assert.Empty(svc.Updated);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_workflow_enabled_with_a_string_array_output()
    {
        // Output properties are checked too, and the message shows the collection marker of the type.
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        JsonElement def = Json(new
        {
            pluginTypeName = Plugin,
            uniqueName = "zava_DoThing",
            isFunction = false,
            workflowSdkStepEnabled = true,
            outputParameters = new[] { new { name = "Tags", parameterTypeName = "System.String", isCollection = true } },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Contains("parameter 'Tags' uses a type (System.String[])", ex.Message);
    }

    [Fact]
    public void UpsertCustomApi_refuses_an_existing_api_implemented_by_another_assembly()
    {
        // UpsertCustomApi must consult the ownership guard before re-pointing an existing API to our plugin.
        var existingId = Guid.NewGuid();
        var foreignType = Guid.NewGuid();
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService(new Entity("customapi", existingId));
        svc.OnRetrieve = (entity, id, _) => entity == "plugintype"
            ? new Entity("plugintype", id) { ["typename"] = "Other.Plugin", ["assemblyname"] = "Other" }
            : new Entity("customapi", existingId) { ["plugintypeid"] = new EntityReference("plugintype", foreignType) };
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing" });

        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Contains("implemented by 'Other.Plugin'", ex.Message);
        Assert.Empty(svc.Updated);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_workflow_enabled_on_a_function()
    {
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing", isFunction = true, workflowSdkStepEnabled = true });

        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Equal(
            "Custom API 'zava_DoThing': WorkflowSdkStepEnabled requires an action, but IsFunction is true. " +
            "A function cannot be enabled for workflow - make it an action or disable workflow support.", ex.Message);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_workflow_enabled_with_entity_parameter()
    {
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        JsonElement def = Json(new
        {
            pluginTypeName = Plugin,
            uniqueName = "zava_DoThing",
            isFunction = false,
            workflowSdkStepEnabled = true,
            inputParameters = new[] { new { name = "Record", parameterTypeName = "Microsoft.Xrm.Sdk.Entity", isCollection = false } },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false));
        Assert.Equal(
            "Custom API 'zava_DoThing': parameter 'Record' uses a type (Microsoft.Xrm.Sdk.Entity) that is not allowed when WorkflowSdkStepEnabled is set. " +
            "Workflow-enabled Custom APIs cannot use Entity, EntityCollection or StringArray parameters. " +
            "Change the parameter type or disable workflow support.", ex.Message);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_uniquename_missing_solution_prefix()
    {
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "DoThing" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false, solutionPrefix: "zava"));
        Assert.Equal(
            "Custom API 'DoThing' must start with the solution publisher prefix 'zava_'. " +
            "Dataverse requires the customization prefix on a Custom API's unique name. Rename it to " +
            "'zava_DoThing' (or match the solution's publisher prefix).", ex.Message);
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void UpsertCustomApi_accepts_uniquename_with_solution_prefix()
    {
        (TestSupport.FakeOrgService svc, Dictionary<string, Guid> types) = NewService();
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing" });

        DataverseRegistration.UpsertCustomApi(svc, types, def, verbose: false, solutionPrefix: "zava");
        Assert.Single(svc.Created);
    }

    [Fact]
    public void UpsertCustomApi_throws_when_plugin_type_unresolved()
    {
        var svc = new TestSupport.FakeOrgService();
        JsonElement def = Json(new { pluginTypeName = Plugin, uniqueName = "zava_DoThing" });
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.UpsertCustomApi(svc, new Dictionary<string, Guid>(), def, verbose: false));
        Assert.Contains("Plugin type not found in Dataverse", ex.Message);
        Assert.Contains(Plugin, ex.Message);
    }

    // ---- FindCustomApiId --------------------------------------------------------------------------

    [Fact]
    public void FindCustomApiId_queries_by_uniquename_and_returns_id()
    {
        var apiId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("customapi", apiId)),
        };

        Assert.Equal(apiId, DataverseRegistration.FindCustomApiId(svc, "zava_DoThing"));

        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("customapi", q.EntityName);
        Assert.Equal(1, q.TopCount);
        Assert.Contains("customapiid", q.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(q.Criteria.Conditions);
        Assert.Equal("uniquename", c.AttributeName);
        Assert.Equal("zava_DoThing", c.Values.Single());
    }

    [Fact]
    public void FindCustomApiId_returns_null_when_absent()
        => Assert.Null(DataverseRegistration.FindCustomApiId(new TestSupport.FakeOrgService(), "missing"));

    // ---- SyncCustomApiRequestParameters (create/update/delete) -----------------------------------

    [Fact]
    public void SyncRequestParameters_creates_new_parameter_with_every_column()
    {
        var svc = new TestSupport.FakeOrgService(); // no existing params → create
        var apiId = Guid.NewGuid();
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: true, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: "account");

        DataverseRegistration.SyncCustomApiRequestParameters(svc, apiId, "zava_DoThing", [param], verbose: false);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("customapirequestparameter", created.LogicalName);
        EntityReference apiRef = created.GetAttributeValue<EntityReference>("customapiid");
        Assert.Equal("customapi", apiRef.LogicalName);
        Assert.Equal(apiId, apiRef.Id);
        // The reconcile first reads existing rows for this api, filtered by customapiid.
        Assert.Contains(svc.Queries, q => q.EntityName == "customapirequestparameter"
            && q.Criteria.Conditions.Any(c => c.AttributeName == "customapiid"));
        Assert.Equal("Target", created.GetAttributeValue<string>("name"));
        Assert.Equal("Target", created.GetAttributeValue<string>("displayname"));
        Assert.Equal("Target", created.GetAttributeValue<string>("description"));
        Assert.Equal("Target", created.GetAttributeValue<string>("uniquename")); // bare identifier, no "{api}." prefix
        Assert.True(created.GetAttributeValue<bool>("isoptional"));
        Assert.Equal(SdkConstants.CustomApiParameterType.String, created.GetAttributeValue<OptionSetValue>("type").Value);
        Assert.Equal("account", created.GetAttributeValue<string>("logicalentityname"));
    }

    [Fact]
    public void SyncRequestParameters_omits_logicalentityname_when_blank()
    {
        var svc = new TestSupport.FakeOrgService();
        var param = new CustomApiParameterApplyDefinition("Flag", IsOptional: false, ParameterTypeName: "System.Boolean", IsCollection: false, LogicalEntityName: "  ");

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Entity created = Assert.Single(svc.Created);
        Assert.False(created.Contains("logicalentityname")); // blank → not written
        Assert.False(created.GetAttributeValue<bool>("isoptional"));
        Assert.Equal(SdkConstants.CustomApiParameterType.Boolean, created.GetAttributeValue<OptionSetValue>("type").Value);
    }

    [Fact]
    public void SyncRequestParameters_recreates_a_parameter_whose_optional_flag_or_table_changed()
    {
        // isoptional and logicalentityname are create-only: an in-place update would not converge, so the parameter
        // is deleted and recreated (like a type change).
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Target",
            ["uniquename"] = "Target",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.Integer),
            ["isoptional"] = false,
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: true, ParameterTypeName: "System.Int32", IsCollection: false, LogicalEntityName: "contact");

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Assert.Contains(("customapirequestparameter", existingId), svc.Deleted);
        Entity created = Assert.Single(svc.Created);
        Assert.True(created.GetAttributeValue<bool>("isoptional"));
        Assert.Equal("contact", created.GetAttributeValue<string>("logicalentityname"));
        Assert.Empty(svc.Updated);
    }

    [Fact]
    public void SyncRequestParameters_recreates_a_parameter_whose_optional_flag_alone_changed()
    {
        // required -> optional with the type, unique name and binding unchanged: the optionality check alone must
        // force the recreate (isoptional is create-only).
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Target",
            ["uniquename"] = "Target",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.Integer),
            ["isoptional"] = false,
        };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: true, ParameterTypeName: "System.Int32", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Assert.Contains(("customapirequestparameter", existingId), svc.Deleted);
        Assert.True(Assert.Single(svc.Created).GetAttributeValue<bool>("isoptional"));
    }

    [Fact]
    public void SyncRequestParameters_recreates_a_parameter_whose_table_alone_changed()
    {
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Target",
            ["uniquename"] = "Target",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.EntityReference),
            ["isoptional"] = true,
            ["logicalentityname"] = "account",
        };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: true, ParameterTypeName: "Microsoft.Xrm.Sdk.EntityReference", IsCollection: false, LogicalEntityName: "contact");

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Assert.Contains(("customapirequestparameter", existingId), svc.Deleted);
        Assert.Equal("contact", Assert.Single(svc.Created).GetAttributeValue<string>("logicalentityname"));
    }

    [Fact]
    public void SyncRequestParameters_leaves_an_unchanged_parameter_alone()
    {
        var existing = new Entity("customapirequestparameter", Guid.NewGuid())
        {
            ["name"] = "Target",
            ["uniquename"] = "Target",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.EntityReference),
            ["isoptional"] = true,
            ["logicalentityname"] = "contact",
        };
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing) };
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: true, ParameterTypeName: "Microsoft.Xrm.Sdk.EntityReference", IsCollection: false, LogicalEntityName: "Contact");

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Assert.Empty(svc.Updated);
        Assert.Empty(svc.Created);
        Assert.Empty(svc.Deleted);
    }

    [Fact]
    public void SyncRequestParameters_deletes_and_recreates_a_parameter_whose_type_changed()
    {
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Amount",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.Integer),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        // Desired type differs (String vs Integer). A parameter's type is create-only, so the one parameter is
        // deleted and recreated with the new type (in place; the API and its other parameters are untouched),
        // rather than emitting an update Dataverse ignores (which would never converge).
        var param = new CustomApiParameterApplyDefinition("Amount", IsOptional: false, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        (string entity, Guid id) = Assert.Single(svc.Deleted);
        Assert.Equal("customapirequestparameter", entity);
        Assert.Equal(existingId, id); // the stale-typed row is removed

        Entity created = Assert.Single(svc.Created); // and recreated with the new type
        Assert.Equal("Amount", created.GetAttributeValue<string>("name"));
        Assert.Equal(SdkConstants.CustomApiParameterType.String, created.GetAttributeValue<OptionSetValue>("type").Value);
        Assert.Empty(svc.Updated); // never an in-place update for a type change
    }

    [Fact]
    public void SyncRequestParameters_self_heals_a_legacy_dotted_unique_name()
    {
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Amount",
            ["uniquename"] = "zava_DoThing.Amount", // a legacy dotted uniquename from the old scheme (uncallable)
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.String), // type matches desired
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        // Same name and type, but the live uniquename is dotted. uniquename is create-only, so the parameter is
        // deleted and recreated with the correct bare uniquename so a re-apply converges on a callable API.
        var param = new CustomApiParameterApplyDefinition("Amount", IsOptional: false, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        (string entity, Guid id) = Assert.Single(svc.Deleted);
        Assert.Equal("customapirequestparameter", entity);
        Assert.Equal(existingId, id); // the dotted row is removed

        Entity created = Assert.Single(svc.Created); // and recreated with the bare uniquename
        Assert.Equal("Amount", created.GetAttributeValue<string>("name"));
        Assert.Equal("Amount", created.GetAttributeValue<string>("uniquename"));
        Assert.Empty(svc.Updated); // never an in-place update for a create-only field
    }

    [Fact]
    public void SyncRequestParameters_recreates_without_logicalentityname_when_the_binding_is_removed()
    {
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Target",
            ["uniquename"] = "Target", // already the bare name: no self-heal, only the binding changes
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.String), // matches desired; only the binding changes
            ["logicalentityname"] = "account", // a stale entity binding on the live row
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        // Desired parameter has no entity binding: the stale value must go, not be left behind (which would make every
        // subsequent plan/apply report the Custom API as "changed" forever). The column is create-only: recreate.
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: false, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Assert.Contains(("customapirequestparameter", existingId), svc.Deleted);
        Assert.False(Assert.Single(svc.Created).Contains("logicalentityname"));
    }

    [Fact]
    public void SyncRequestParameters_deletes_orphaned_parameter()
    {
        var orphanId = Guid.NewGuid();
        var orphan = new Entity("customapirequestparameter", orphanId) { ["name"] = "Gone" };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(orphan),
        };

        // desired is empty → the existing "Gone" row is not wanted and must be deleted.
        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [], verbose: false);

        (string entity, Guid id) = Assert.Single(svc.Deleted);
        Assert.Equal("customapirequestparameter", entity);
        Assert.Equal(orphanId, id);
    }

    [Fact]
    public void SyncResponseProperties_uses_bare_unique_name_and_no_isoptional()
    {
        var svc = new TestSupport.FakeOrgService();
        // isOptional is ignored for response properties (IncludeIsOptional = false).
        var prop = new CustomApiParameterApplyDefinition("Result", IsOptional: true, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiResponseProperties(svc, Guid.NewGuid(), "zava_DoThing", [prop], verbose: false);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("customapiresponseproperty", created.LogicalName);
        // The uniquename is the bare parameter name (no "{api}." prefix, no ".response" suffix). It is unique only
        // within the Custom API, and a dot in it would make the operation uncallable on a real org.
        Assert.Equal("Result", created.GetAttributeValue<string>("uniquename"));
        Assert.False(created.Contains("isoptional")); // response properties carry no isoptional column
        // The query targets the response-property table.
        Assert.Contains(svc.Queries, q => q.EntityName == "customapiresponseproperty");
    }

    // ---- DeleteCustomApiRequestParameters / ResponseProperties -----------------------------------

    [Fact]
    public void DeleteCustomApiRequestParameters_deletes_every_row_for_the_api()
    {
        var apiId = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(
                new Entity("customapirequestparameter", a), new Entity("customapirequestparameter", b)),
        };

        DataverseRegistration.DeleteCustomApiRequestParameters(svc, apiId);

        Assert.Equal(2, svc.Deleted.Count);
        Assert.All(svc.Deleted, d => Assert.Equal("customapirequestparameter", d.Entity));
        Assert.Contains((("customapirequestparameter", a)), svc.Deleted);
        Assert.Contains((("customapirequestparameter", b)), svc.Deleted);
        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("customapirequestparameter", q.EntityName);
        Assert.Contains("customapirequestparameterid", q.ColumnSet.Columns);
        Assert.Equal("customapiid", q.Criteria.Conditions.Single().AttributeName);
        Assert.Equal(apiId, q.Criteria.Conditions.Single().Values.Single());
    }

    [Fact]
    public void DeleteCustomApiResponseProperties_deletes_every_row_for_the_api()
    {
        var apiId = Guid.NewGuid();
        var a = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(new Entity("customapiresponseproperty", a)),
        };

        DataverseRegistration.DeleteCustomApiResponseProperties(svc, apiId);

        (string entity, Guid id) = Assert.Single(svc.Deleted);
        Assert.Equal("customapiresponseproperty", entity);
        Assert.Equal(a, id);
        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("customapiresponseproperty", q.EntityName);
        Assert.Contains("customapiresponsepropertyid", q.ColumnSet.Columns);
        Assert.Equal("customapiid", q.Criteria.Conditions.Single().AttributeName);
    }

    // ---- ResolveCustomApiTypeCode ----------------------------------------------------------------

    [Theory]
    [InlineData(null, false, SdkConstants.CustomApiParameterType.String)]         // null → "System.String" default
    [InlineData("System.String", true, SdkConstants.CustomApiParameterType.StringArray)]
    [InlineData("Microsoft.Xrm.Sdk.Entity", true, SdkConstants.CustomApiParameterType.EntityCollection)]
    [InlineData("Microsoft.Xrm.Sdk.EntityCollection", true, SdkConstants.CustomApiParameterType.EntityCollection)]
    [InlineData("System.Boolean", false, SdkConstants.CustomApiParameterType.Boolean)]
    [InlineData("Microsoft.Xrm.Sdk.EntityReference", false, SdkConstants.CustomApiParameterType.EntityReference)]
    [InlineData("Microsoft.Xrm.Sdk.Entity", false, SdkConstants.CustomApiParameterType.Entity)]
    [InlineData("System.Int32", false, SdkConstants.CustomApiParameterType.Integer)]
    public void ResolveCustomApiTypeCode_maps_type_names(string? typeName, bool isCollection, int expected)
        => Assert.Equal(expected, DataverseRegistration.ResolveCustomApiTypeCode(typeName, isCollection));
}
