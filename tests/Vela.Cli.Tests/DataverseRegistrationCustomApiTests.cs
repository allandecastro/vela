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
            isFunction = true,
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
        Assert.True(api.GetAttributeValue<bool>("isfunction"));
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
        Assert.Empty(svc.Updated);
        Assert.Empty(svc.Created);
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
        Assert.Equal("zava_DoThing.Target", created.GetAttributeValue<string>("uniquename"));
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
    public void SyncRequestParameters_updates_existing_parameter_by_name()
    {
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Target",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.Integer), // matches desired: this test exercises the update path, not a type change
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: true, ParameterTypeName: "System.Int32", IsCollection: false, LogicalEntityName: "contact");

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Entity updated = Assert.Single(svc.Updated);
        Assert.Equal("customapirequestparameter", updated.LogicalName);
        Assert.Equal(existingId, updated.Id);
        Assert.True(updated.GetAttributeValue<bool>("isoptional"));
        Assert.False(updated.Contains("type")); // type is immutable, so it is not written on update
        Assert.Equal("contact", updated.GetAttributeValue<string>("logicalentityname")); // written when set
        Assert.Empty(svc.Created);
    }

    [Fact]
    public void SyncRequestParameters_throws_when_an_existing_parameter_type_changes()
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
        // Desired type differs (String vs Integer). A parameter's type is create-only, so this must fail fast
        // rather than emit an update Dataverse ignores (which would never converge).
        var param = new CustomApiParameterApplyDefinition("Amount", IsOptional: false, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        Assert.Throws<InvalidOperationException>(
            () => DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false));
        Assert.Empty(svc.Updated); // nothing written when the type can't change
    }

    [Fact]
    public void SyncRequestParameters_clears_logicalentityname_when_blank_on_update()
    {
        var existingId = Guid.NewGuid();
        var existing = new Entity("customapirequestparameter", existingId)
        {
            ["name"] = "Target",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.String), // matches desired; only the binding changes
            ["logicalentityname"] = "account", // a stale entity binding on the live row
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(existing),
        };
        // Desired parameter has no entity binding: the stale value must be cleared, not left behind (which would
        // make every subsequent plan/apply report the Custom API as "changed" forever).
        var param = new CustomApiParameterApplyDefinition("Target", IsOptional: false, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiRequestParameters(svc, Guid.NewGuid(), "zava_DoThing", [param], verbose: false);

        Entity updated = Assert.Single(svc.Updated);
        Assert.True(updated.Contains("logicalentityname"));                 // the attribute is written ...
        Assert.Null(updated.GetAttributeValue<string>("logicalentityname")); // ... as null, to clear the stale value
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
    public void SyncResponseProperties_uses_response_entity_suffix_and_no_isoptional()
    {
        var svc = new TestSupport.FakeOrgService();
        // isOptional is ignored for response properties (IncludeIsOptional = false).
        var prop = new CustomApiParameterApplyDefinition("Result", IsOptional: true, ParameterTypeName: "System.String", IsCollection: false, LogicalEntityName: null);

        DataverseRegistration.SyncCustomApiResponseProperties(svc, Guid.NewGuid(), "zava_DoThing", [prop], verbose: false);

        Entity created = Assert.Single(svc.Created);
        Assert.Equal("customapiresponseproperty", created.LogicalName);
        Assert.Equal("zava_DoThing.Result.response", created.GetAttributeValue<string>("uniquename"));
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
    [InlineData("System.Int32", true, SdkConstants.CustomApiParameterType.Integer)] // collection of a non-string/non-entity scalar falls through to Integer
    [InlineData("System.Boolean", false, SdkConstants.CustomApiParameterType.Boolean)]
    [InlineData("Microsoft.Xrm.Sdk.EntityReference", false, SdkConstants.CustomApiParameterType.EntityReference)]
    [InlineData("Microsoft.Xrm.Sdk.Entity", false, SdkConstants.CustomApiParameterType.Entity)]
    [InlineData("System.Int32", false, SdkConstants.CustomApiParameterType.Integer)]
    public void ResolveCustomApiTypeCode_maps_type_names(string? typeName, bool isCollection, int expected)
        => Assert.Equal(expected, DataverseRegistration.ResolveCustomApiTypeCode(typeName, isCollection));
}
