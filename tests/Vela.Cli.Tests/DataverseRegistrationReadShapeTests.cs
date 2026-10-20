using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using TestSupport = Vela.Cli.Tests.TestSupport;

namespace Vela.Cli.Tests;

/// <summary>
/// Read-path tests for <see cref="DataverseRegistration"/> (the reader partial) that assert the exact query
/// shapes (entity, column set, criteria, link entities) and the full mapping of Dataverse rows into the
/// diff shape — the parts the existing <c>DataverseRegistrationReaderTests</c> exercised but under-asserted,
/// plus the two large readers (`GetStepItems`, `GetCustomApiItems`) that had no direct coverage.
/// </summary>
public sealed class DataverseRegistrationReadShapeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid ImpersonatedUser = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Entity Aliased(Entity e, string alias, string entity, string attr, object value)
    {
        e[alias] = new AliasedValue(entity, attr, value);
        return e;
    }

    // ---- GetStepItems ----------------------------------------------------------------------------

    [Fact]
    public void GetStepItems_maps_a_step_with_links_images_and_secure_config()
    {
        var handlerId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var secureId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.AccountPlugin" };

        var step = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["eventhandler"] = new EntityReference("plugintype", handlerId),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["rank"] = 5,
            ["name"] = "Zava.Plugins.AccountPlugin - PostOpSync: dv-key",
            ["filteringattributes"] = "name,accountnumber",
            ["statecode"] = new OptionSetValue(0),          // enabled
            ["configuration"] = "unsecure-cfg",
            ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", secureId),
            ["impersonatinguserid"] = new EntityReference("systemuser", ImpersonatedUser),
            ["supporteddeployment"] = new OptionSetValue(2),   // Both
        };
        Aliased(step, "msg.name", "sdkmessage", "name", "Update");
        Aliased(step, "filter.primaryobjecttypecode", "sdkmessagefilter", "primaryobjecttypecode", "account");

        var preImage = new Entity("sdkmessageprocessingstepimage")
        {
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(0),
            ["entityalias"] = "PreImage",
            ["attributes"] = "name",
        };
        var postImage = new Entity("sdkmessageprocessingstepimage")
        {
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(1),
            ["entityalias"] = "PostImage",
            ["attributes"] = "accountnumber",
        };

        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(step),
                "sdkmessageprocessingstepimage" => TestSupport.FakeOrgService.Collection(preImage, postImage),
                "sdkmessageprocessingstepsecureconfig" => TestSupport.FakeOrgService.Collection(
                    new Entity("sdkmessageprocessingstepsecureconfig", secureId) { ["secureconfig"] = "topsecret" }),
                _ => new EntityCollection(),
            },
        };

        RegistrationItem item = Assert.Single(DataverseRegistration.GetStepItems(svc, pluginTypes, Json));
        Assert.Equal("step", item.Kind);
        Assert.Equal("Zava.Plugins.AccountPlugin", item.Type);

        JsonElement d = item.Definition;
        Assert.Equal("Zava.Plugins.AccountPlugin", d.GetProperty("pluginTypeName").GetString());
        Assert.Equal("Update", d.GetProperty("message").GetString());
        Assert.Equal("account", d.GetProperty("entityLogicalName").GetString());
        Assert.Equal(40, d.GetProperty("stage").GetInt32());
        Assert.Equal(0, d.GetProperty("mode").GetInt32());
        Assert.Equal(5, d.GetProperty("executionOrder").GetInt32());
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.Equal("unsecure-cfg", d.GetProperty("unsecureConfig").GetString());
        Assert.Equal(ImpersonatedUser.ToString(), d.GetProperty("impersonatingUserId").GetString());
        Assert.Equal(2, d.GetProperty("supportedDeployment").GetInt32());
        Assert.Equal(new[] { "accountnumber", "name" }, d.GetProperty("filteringAttributes").EnumerateArray().Select(x => x.GetString())); // normalizer sorts
        Assert.Equal(DefinitionNormalizer.HashSecret("topsecret"), d.GetProperty("secureConfig").GetString());
        Assert.Equal("PreImage", d.GetProperty("preImages").EnumerateArray().Single().GetProperty("alias").GetString());
        Assert.Equal("PostImage", d.GetProperty("postImages").EnumerateArray().Single().GetProperty("alias").GetString());
    }

    [Fact]
    public void GetStepItems_disabled_state_is_reported_as_not_enabled()
    {
        var handlerId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.AccountPlugin" };
        var step = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", handlerId),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(1), // disabled
        };
        Aliased(step, "msg.name", "sdkmessage", "name", "Update");
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(step) : new EntityCollection(),
        };

        RegistrationItem item = Assert.Single(DataverseRegistration.GetStepItems(svc, pluginTypes, Json));
        Assert.False(item.Definition.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void GetStepItems_reads_a_keyless_step_as_having_no_step_key()   // #305 churn
    {
        // A keyless step's live name is the computed keyless name and it carries no marker. The read must resolve
        // stepKey to "" (matching the keyless code side) rather than parsing "Update of account" as a spurious key,
        // which would make the step diff as "changed" on every plan/apply.
        var handlerId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.AccountPlugin" };
        var step = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", handlerId),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(0),
            ["name"] = "Zava.Plugins.AccountPlugin - PostOpSync: Update of account",  // keyless name, no marker
        };
        Aliased(step, "msg.name", "sdkmessage", "name", "Update");
        Aliased(step, "filter.primaryobjecttypecode", "sdkmessagefilter", "primaryobjecttypecode", "account");
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(step) : new EntityCollection(),
        };

        RegistrationItem item = Assert.Single(DataverseRegistration.GetStepItems(svc, pluginTypes, Json));
        Assert.Equal(string.Empty, item.Definition.GetProperty("stepKey").GetString());
    }

    [Fact]
    public void GetStepItems_skips_main_operation_and_unknown_handler_steps()
    {
        var handlerId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.AccountPlugin" };

        // Internal Custom API main-operation step (its message is the Custom API's unique name) — skipped.
        var mainOp = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", handlerId),
            ["msg.name"] = new AliasedValue("sdkmessage", "name", "zava_DoThing"),
            ["stage"] = new OptionSetValue(SdkConstants.PipelineStage.MainOperation),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(0),
        };
        // Step whose handler is not among the known plugin types — skipped.
        var unknown = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", Guid.NewGuid()),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(0),
        };
        var customApi = new Entity("customapi", Guid.NewGuid()) { ["uniquename"] = "zava_DoThing" };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(mainOp, unknown),
                "customapi" => TestSupport.FakeOrgService.Collection(customApi),
                _ => new EntityCollection(),
            },
        };

        Assert.Empty(DataverseRegistration.GetStepItems(svc, pluginTypes, Json));
    }

    [Fact]
    public void GetStepItems_does_not_read_custom_apis_when_no_step_is_at_main_operation()
    {
        // Only a stage-30 step can be a Custom API's internal step, so without one the customapi table is never read
        // (it used to be read in full, once per assembly).
        var handlerId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.AccountPlugin" };
        var step = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", handlerId),
            ["msg.name"] = new AliasedValue("sdkmessage", "name", "Update"),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(0),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep" ? TestSupport.FakeOrgService.Collection(step) : new EntityCollection(),
        };

        Assert.Single(DataverseRegistration.GetStepItems(svc, pluginTypes, Json));
        Assert.DoesNotContain(svc.Queries, q => q.EntityName == "customapi");
    }

    [Fact]
    public void GetStepItems_reads_only_the_custom_apis_named_by_main_operation_steps()
    {
        // A user step at MainOperation on a standard message is kept; the internal step of a Custom API is skipped.
        // The customapi read is scoped to the stage-30 messages instead of scanning the whole environment.
        var handlerId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.MyApi" };
        Entity MainOp(string? message)
        {
            var step = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
            {
                ["eventhandler"] = new EntityReference("plugintype", handlerId),
                ["stage"] = new OptionSetValue(SdkConstants.PipelineStage.MainOperation),
                ["mode"] = new OptionSetValue(0),
                ["statecode"] = new OptionSetValue(0),
            };
            if (message != null)
            {
                step["msg.name"] = new AliasedValue("sdkmessage", "name", message);
            }

            return step;
        }
        var postOp = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["eventhandler"] = new EntityReference("plugintype", handlerId),
            ["msg.name"] = new AliasedValue("sdkmessage", "name", "Create"),
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(0),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(MainOp("zava_DoThing"), MainOp("Update"), MainOp(null), postOp),
                // The platform compares case-insensitively; the row's casing differs from the step's message.
                "customapi" => TestSupport.FakeOrgService.Collection(new Entity("customapi", Guid.NewGuid()) { ["uniquename"] = "ZAVA_DoThing" }),
                _ => new EntityCollection(),
            },
        };

        List<RegistrationItem> items = DataverseRegistration.GetStepItems(svc, pluginTypes, Json);

        // A step without a message name is not a candidate (no empty value goes into the In list).
        Assert.Equal(new[] { "Update", "", "Create" }, items.Select(i => i.Definition.GetProperty("message").GetString()).ToArray());
        QueryExpression apiQuery = Assert.Single(svc.Queries, q => q.EntityName == "customapi");
        Assert.Equal(new[] { "uniquename" }, apiQuery.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(apiQuery.Criteria.Conditions);
        Assert.Equal("uniquename", c.AttributeName);
        Assert.Equal(ConditionOperator.In, c.Operator);
        Assert.Equal(new object[] { "zava_DoThing", "Update" }, c.Values.ToArray());
    }

    [Fact]
    public void GetStepImages_chunks_a_long_step_id_list_and_merges_the_images()
    {
        // 1001 steps would put 1001 parameters in one In condition; the read splits it and loses no image.
        Guid[] stepIds = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => TestSupport.FakeOrgService.Collection(
                Assert.Single(q.Criteria.Conditions).Values
                    .Select(v => new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
                    {
                        ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", (Guid)v),
                    })
                    .ToArray()),
        };

        Dictionary<Guid, Entity[]> images = DataverseRegistration.GetStepImages(svc, stepIds);

        Assert.Equal(new[] { 500, 500, 1 }, svc.Queries.Select(q => q.Criteria.Conditions[0].Values.Count).ToArray());
        Assert.Equal(stepIds.OrderBy(x => x), images.Keys.OrderBy(x => x));
    }

    [Fact]
    public void GetStepItems_returns_empty_without_querying_when_no_plugin_types()
    {
        var svc = new TestSupport.FakeOrgService();
        Assert.Empty(DataverseRegistration.GetStepItems(svc, new Dictionary<Guid, string>(), Json));
        Assert.Empty(svc.Queries);
    }

    [Fact]
    public void GetStepItems_queries_steps_by_handler_with_message_and_filter_links()
    {
        var handlerId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [handlerId] = "Zava.Plugins.AccountPlugin" };
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                if (q.EntityName == "sdkmessageprocessingstep") captured = q;
                return new EntityCollection();
            },
        };

        DataverseRegistration.GetStepItems(svc, pluginTypes, Json);

        Assert.NotNull(captured);
        Assert.Equal(
            new[] { "sdkmessageprocessingstepid", "eventhandler", "name", "description", "stage", "mode", "rank", "filteringattributes", "statecode", "configuration", "sdkmessageprocessingstepsecureconfigid", "impersonatinguserid", "supporteddeployment", "asyncautodelete" },
            captured!.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(captured.Criteria.Conditions);
        Assert.Equal("eventhandler", c.AttributeName);
        Assert.Equal(ConditionOperator.In, c.Operator);

        LinkEntity msg = captured.LinkEntities.Single(l => l.LinkToEntityName == "sdkmessage");
        Assert.Equal("sdkmessageid", msg.LinkFromAttributeName);
        Assert.Equal("sdkmessageid", msg.LinkToAttributeName);
        Assert.Equal("msg", msg.EntityAlias);
        Assert.Contains("name", msg.Columns.Columns);
        LinkEntity filter = captured.LinkEntities.Single(l => l.LinkToEntityName == "sdkmessagefilter");
        Assert.Equal("sdkmessagefilterid", filter.LinkFromAttributeName);
        Assert.Equal("sdkmessagefilterid", filter.LinkToAttributeName);
        Assert.Equal("filter", filter.EntityAlias);
        Assert.Contains("primaryobjecttypecode", filter.Columns.Columns);
    }

    // ---- GetCustomApiItems -----------------------------------------------------------------------

    [Fact]
    public void GetCustomApiItems_maps_api_with_request_and_response_parameters()
    {
        var pluginTypeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [pluginTypeId] = "Zava.Plugins.MyApi" };

        var api = new Entity("customapi", apiId)
        {
            ["uniquename"] = "zava_DoThing",
            ["isfunction"] = true,
            ["boundentitylogicalname"] = "account",
            ["executeprivilegename"] = "prvDoThing",
            ["isprivate"] = true,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(1),
            ["bindingtype"] = new OptionSetValue(2),
            ["workflowsdkstepenabled"] = true,
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
        };
        Entity ReqParam(string name) => new("customapirequestparameter")
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = name,
            ["isoptional"] = true,
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.String),
        };
        Entity RespProp(string name) => new("customapiresponseproperty")
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = name,
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.Boolean),
        };

        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "customapi" => TestSupport.FakeOrgService.Collection(api),
                // Supplied out of order → the reader emits them ordinally ascending.
                "customapirequestparameter" => TestSupport.FakeOrgService.Collection(ReqParam("Bravo"), ReqParam("Alpha")),
                "customapiresponseproperty" => TestSupport.FakeOrgService.Collection(RespProp("Yankee"), RespProp("Xray")),
                _ => new EntityCollection(),
            },
        };

        RegistrationItem item = Assert.Single(DataverseRegistration.GetCustomApiItems(svc, pluginTypes, Json));
        Assert.Equal("custom-api", item.Kind);
        Assert.Equal("zava_DoThing", item.Type);

        JsonElement d = item.Definition;
        Assert.Equal("Zava.Plugins.MyApi", d.GetProperty("pluginTypeName").GetString());
        Assert.Equal("zava_DoThing", d.GetProperty("uniqueName").GetString());
        Assert.True(d.GetProperty("isFunction").GetBoolean());
        Assert.Equal("account", d.GetProperty("boundEntityLogicalName").GetString());
        Assert.Equal("prvDoThing", d.GetProperty("executePrivilegeName").GetString());
        Assert.True(d.GetProperty("isPrivate").GetBoolean());
        Assert.Equal(1, d.GetProperty("allowedCustomProcessingStepType").GetInt32());
        Assert.Equal(2, d.GetProperty("bindingType").GetInt32());
        Assert.True(d.GetProperty("isBound").GetBoolean()); // bindingType != 0
        Assert.True(d.GetProperty("workflowSdkStepEnabled").GetBoolean());
        Assert.Equal(new[] { "Alpha", "Bravo" }, d.GetProperty("inputParameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(new[] { "Xray", "Yankee" }, d.GetProperty("outputParameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
    }

    [Fact]
    public void GetCustomApiItems_reports_unbound_api_when_binding_type_zero()
    {
        var pluginTypeId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [pluginTypeId] = "Zava.Plugins.MyApi" };
        var api = new Entity("customapi", Guid.NewGuid())
        {
            ["uniquename"] = "zava_Unbound",
            ["bindingtype"] = new OptionSetValue(0),
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "customapi" ? TestSupport.FakeOrgService.Collection(api) : new EntityCollection(),
        };

        RegistrationItem item = Assert.Single(DataverseRegistration.GetCustomApiItems(svc, pluginTypes, Json));
        JsonElement d = item.Definition;
        Assert.False(d.GetProperty("isBound").GetBoolean());
        Assert.Equal(0, d.GetProperty("bindingType").GetInt32());
        // Absent boolean columns default to false (not true).
        Assert.False(d.GetProperty("isFunction").GetBoolean());
        Assert.False(d.GetProperty("isPrivate").GetBoolean());
        Assert.False(d.GetProperty("workflowSdkStepEnabled").GetBoolean());
    }

    [Fact]
    public void GetCustomApiItems_queries_by_plugintype_with_full_column_set()
    {
        var pluginTypeId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [pluginTypeId] = "Zava.Plugins.MyApi" };
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { if (q.EntityName == "customapi") captured = q; return new EntityCollection(); },
        };

        DataverseRegistration.GetCustomApiItems(svc, pluginTypes, Json);

        Assert.NotNull(captured);
        Assert.Equal("customapi", captured!.EntityName);
        Assert.Equal(
            new[] { "customapiid", "uniquename", "displayname", "description", "isfunction", "boundentitylogicalname", "executeprivilegename", "isprivate", "allowedcustomprocessingsteptype", "bindingtype", "workflowsdkstepenabled", "plugintypeid" },
            captured.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(captured.Criteria.Conditions);
        Assert.Equal("plugintypeid", c.AttributeName);
        Assert.Equal(ConditionOperator.In, c.Operator);
    }

    // ---- GetPluginAssembly disambiguation --------------------------------------------------------

    [Fact]
    public void GetPluginAssembly_prefers_more_types_then_newer_modified_date()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var sameCountA = new Entity("pluginassembly", older) { ["name"] = "Zava.Plugins", ["modifiedon"] = new DateTime(2026, 1, 1) };
        var sameCountB = new Entity("pluginassembly", newer) { ["name"] = "Zava.Plugins", ["modifiedon"] = new DateTime(2026, 6, 1) };

        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                if (q.EntityName == "pluginassembly") return TestSupport.FakeOrgService.Collection(sameCountA, sameCountB);
                // Both assemblies report the SAME type count → the tie breaks on modifiedon.
                return TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "T" });
            },
        };

        Entity? result = DataverseRegistration.GetPluginAssembly(svc, "Zava.Plugins");
        Assert.Equal(newer, result!.Id); // newer modifiedon wins the tie
    }

    [Fact]
    public void GetPluginAssembly_falls_back_to_createdon_when_modifiedon_absent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // No modifiedon on either → the createdon fallback drives the comparison.
        var candA = new Entity("pluginassembly", a) { ["name"] = "Zava.Plugins", ["createdon"] = new DateTime(2026, 1, 1) };
        var candB = new Entity("pluginassembly", b) { ["name"] = "Zava.Plugins", ["createdon"] = new DateTime(2026, 9, 1) };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "pluginassembly"
                ? TestSupport.FakeOrgService.Collection(candA, candB)
                : TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "T" }),
        };

        Entity? result = DataverseRegistration.GetPluginAssembly(svc, "Zava.Plugins");
        Assert.Equal(b, result!.Id);
    }

    [Fact]
    public void GetPluginAssembly_queries_by_name_with_timestamp_and_package_columns()
    {
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return new EntityCollection(); },
        };

        DataverseRegistration.GetPluginAssembly(svc, "Zava.Plugins");

        Assert.Equal("pluginassembly", captured!.EntityName);
        Assert.Equal(new[] { "pluginassemblyid", "name", "createdon", "modifiedon", "packageid" }, captured.ColumnSet.Columns);
        ConditionExpression c = Assert.Single(captured.Criteria.Conditions);
        Assert.Equal("name", c.AttributeName);
        Assert.Equal("Zava.Plugins", c.Values.Single());
    }

    // ---- query-shape hardening for the small readers ---------------------------------------------

    [Fact]
    public void GetStepImages_queries_by_step_ids_with_image_columns()
    {
        var stepId = Guid.NewGuid();
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return new EntityCollection(); },
        };

        DataverseRegistration.GetStepImages(svc, new[] { stepId });

        Assert.Equal("sdkmessageprocessingstepimage", captured!.EntityName);
        Assert.Equal(new[] { "sdkmessageprocessingstepid", "imagetype", "entityalias", "attributes", "messagepropertyname" }, captured.ColumnSet.Columns);
        Assert.Equal("sdkmessageprocessingstepid", captured.Criteria.Conditions.Single().AttributeName);
    }

    [Fact]
    public void GetSecureConfigs_queries_by_ids_and_maps_values()
    {
        var id = Guid.NewGuid();
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstepsecureconfig", id) { ["secureconfig"] = "s" }); },
        };

        Dictionary<Guid, string> result = DataverseRegistration.GetSecureConfigs(svc, new[] { id });

        Assert.Equal("s", result[id]);
        Assert.Equal("sdkmessageprocessingstepsecureconfig", captured!.EntityName);
        Assert.Equal(new[] { "sdkmessageprocessingstepsecureconfigid", "secureconfig" }, captured.ColumnSet.Columns);
        Assert.Equal("sdkmessageprocessingstepsecureconfigid", captured.Criteria.Conditions.Single().AttributeName);
    }

    [Fact]
    public void GetPluginTypes_queries_by_assembly_id_with_typename_column()
    {
        QueryExpression? captured = null;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return new EntityCollection(); },
        };

        DataverseRegistration.GetPluginTypes(svc, Guid.NewGuid());

        Assert.Equal("plugintype", captured!.EntityName);
        Assert.Equal(new[] { "plugintypeid", "typename" }, captured.ColumnSet.Columns);
        Assert.Equal("pluginassemblyid", captured.Criteria.Conditions.Single().AttributeName);
    }

    [Fact]
    public void GetCustomApiRequestParameters_maps_row_and_skips_blank_name()
    {
        var apiId = Guid.NewGuid();
        QueryExpression? captured = null;
        var named = new Entity("customapirequestparameter")
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = "Target",
            ["isoptional"] = true,
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.Integer),
        };
        var blank = new Entity("customapirequestparameter")
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = "   ",
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return TestSupport.FakeOrgService.Collection(named, blank); },
        };

        var map = DataverseRegistration.GetCustomApiRequestParameters(svc, new[] { apiId });

        CustomApiParameterApplyDefinition p = Assert.Single(map[apiId]); // blank-name row skipped
        Assert.Equal("Target", p.Name);
        Assert.True(p.IsOptional);
        Assert.Equal("System.Int32", p.ParameterTypeName);
        Assert.Equal("customapirequestparameter", captured!.EntityName);
        Assert.Equal(new[] { "customapiid", "name", "isoptional", "type", "logicalentityname", "uniquename", "description" }, captured.ColumnSet.Columns);
        Assert.Equal("customapiid", captured.Criteria.Conditions.Single().AttributeName);
    }

    [Fact]
    public void GetCustomApiResponseParameters_marks_not_optional_and_skips_blank_name()
    {
        var apiId = Guid.NewGuid();
        QueryExpression? captured = null;
        var named = new Entity("customapiresponseproperty")
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = "Result",
            ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.EntityCollection),
            ["logicalentityname"] = "account",
        };
        var blank = new Entity("customapiresponseproperty")
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = null,
        };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => { captured = q; return TestSupport.FakeOrgService.Collection(named, blank); },
        };

        var map = DataverseRegistration.GetCustomApiResponseParameters(svc, new[] { apiId });

        CustomApiParameterApplyDefinition p = Assert.Single(map[apiId]);
        Assert.Equal("Result", p.Name);
        Assert.False(p.IsOptional); // response properties are never optional
        Assert.True(p.IsCollection); // EntityCollection type code
        Assert.Equal("customapiresponseproperty", captured!.EntityName);
        Assert.Equal(new[] { "customapiid", "name", "type", "logicalentityname", "uniquename", "description" }, captured.ColumnSet.Columns);
        Assert.Equal("customapiid", captured.Criteria.Conditions.Single().AttributeName);
    }

    [Fact]
    public void GetPluginAssembly_prefers_the_assembly_with_more_plugin_types()
    {
        var few = Guid.NewGuid();
        var many = Guid.NewGuid();
        // The 'few' assembly is NEWER, but 'many' has more plugin types → type count wins over date.
        var fewAsm = new Entity("pluginassembly", few) { ["name"] = "Zava.Plugins", ["modifiedon"] = new DateTime(2026, 12, 1) };
        var manyAsm = new Entity("pluginassembly", many) { ["name"] = "Zava.Plugins", ["modifiedon"] = new DateTime(2026, 1, 1) };
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                if (q.EntityName == "pluginassembly") return TestSupport.FakeOrgService.Collection(fewAsm, manyAsm);
                Guid assemblyId = (Guid)q.Criteria.Conditions.Single().Values.Single();
                // 'few' → 1 type; 'many' → 2 types.
                return assemblyId == few
                    ? TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "A" })
                    : TestSupport.FakeOrgService.Collection(
                        new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "A" },
                        new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "B" });
            },
        };

        Entity? result = DataverseRegistration.GetPluginAssembly(svc, "Zava.Plugins");
        Assert.Equal(many, result!.Id);
    }
}
