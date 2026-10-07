using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text.Json;
using Vela.Cli;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Characterization tests for the reader and writer methods of <see cref="DataverseRegistration"/>
/// that take an <see cref="IOrganizationService"/>. The service is simulated with Moq following the
/// patterns in <c>ServiceQueryExtensionsTests</c>/<c>ApplyEngineTests</c>: stubbed
/// <c>RetrieveMultiple</c>/<c>Retrieve</c> over hand-built <see cref="EntityCollection"/>s, and
/// <c>Verify</c> over <c>Create</c>/<c>Update</c>/<c>Delete</c>/<c>Execute</c>.
/// </summary>
public class DataverseRegistrationReaderTests
{
    private static EntityCollection Collection(params Entity[] rows) => new EntityCollection(new List<Entity>(rows));

    private static OptionSetValue Opt(int value) => new OptionSetValue(value);

    // Returns a single page for any query against the given entity name; empty for everything else.
    private static Mock<IOrganizationService> ServiceReturning(string entityName, params Entity[] rows)
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
                (q as QueryExpression)?.EntityName == entityName ? Collection(rows) : new EntityCollection());
        return mock;
    }

    // --- ReadAssembliesState (current-state assembly list, G#4b) -------------------------------

    [Fact]
    public void ReadAssembliesState_excludes_an_assembly_not_found_in_the_org()
    {
        // pluginassembly query returns nothing -> the assembly is absent from the org.
        Mock<IOrganizationService> mock = ServiceReturning("pluginassembly");

        DataverseRegistration.AssembliesReadResult result =
            DataverseRegistration.ReadAssembliesState(mock.Object, ["Zava.Plugins"], new JsonSerializerOptions());

        // It must NOT appear in the current assemblies while also being reported not found (the G#4b contradiction).
        Assert.Empty(result.FoundAssemblies);
        Assert.Contains(result.Info, d => d.Contains("Zava.Plugins") && d.Contains("not found"));
        Assert.Empty(result.Diagnostics); // expected on a first deployment: info, so --strict does not fail on it
    }

    [Fact]
    public void ReadAssembliesState_includes_a_found_assembly_even_with_no_plugin_types()
    {
        // pluginassembly is present; plugintype (and everything else) returns empty -> exists but no types yet.
        Mock<IOrganizationService> mock = ServiceReturning("pluginassembly", new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "Zava.Plugins" });

        DataverseRegistration.AssembliesReadResult result =
            DataverseRegistration.ReadAssembliesState(mock.Object, ["Zava.Plugins"], new JsonSerializerOptions());

        Assert.Equal(["Zava.Plugins"], result.FoundAssemblies);
        Assert.Contains(result.Info, d => d.Contains("No plugin types") && d.Contains("Zava.Plugins"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ReadAssembliesState_lists_only_the_assemblies_present_in_the_org()
    {
        // Name-aware mock: only "Found.Plugins" exists; "Missing.Plugins" is absent.
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                var query = (QueryExpression)q;
                if (query.EntityName != "pluginassembly")
                {
                    return new EntityCollection();
                }

                string? name = query.Criteria.Conditions.FirstOrDefault(c => c.AttributeName == "name")?.Values.FirstOrDefault() as string;
                return name == "Found.Plugins"
                    ? Collection(new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "Found.Plugins" })
                    : new EntityCollection();
            });

        DataverseRegistration.AssembliesReadResult result =
            DataverseRegistration.ReadAssembliesState(mock.Object, ["Found.Plugins", "Missing.Plugins"], new JsonSerializerOptions());

        Assert.Equal(["Found.Plugins"], result.FoundAssemblies);
        Assert.Contains(result.Info, d => d.Contains("Missing.Plugins") && d.Contains("not found"));
        Assert.DoesNotContain(result.Info, d => d.Contains("'Found.Plugins' not found"));
    }

    // --- GetPluginAssembly ---------------------------------------------------------------------

    [Fact]
    public void GetPluginAssembly_returns_null_when_no_candidates()
    {
        Mock<IOrganizationService> mock = ServiceReturning("pluginassembly");
        Assert.Null(DataverseRegistration.GetPluginAssembly(mock.Object, "Zava.Plugins"));
    }

    [Fact]
    public void GetPluginAssembly_returns_the_single_candidate()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceReturning("pluginassembly", new Entity("pluginassembly", id) { ["name"] = "Zava.Plugins" });

        Entity? result = DataverseRegistration.GetPluginAssembly(mock.Object, "Zava.Plugins");

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
    }

    [Fact]
    public void GetPluginAssembly_disambiguates_by_type_count_then_modified_date()
    {
        // Two assemblies with the same name: the one with more plugin types wins.
        Guid fewTypes = Guid.NewGuid();
        Guid manyTypes = Guid.NewGuid();

        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                var query = (QueryExpression)q;
                if (query.EntityName == "pluginassembly")
                {
                    return Collection(
                        new Entity("pluginassembly", fewTypes) { ["modifiedon"] = new DateTime(2026, 1, 1) },
                        new Entity("pluginassembly", manyTypes) { ["modifiedon"] = new DateTime(2025, 1, 1) });
                }

                // plugintype query is keyed by pluginassemblyid in the criteria.
                Guid assemblyId = (Guid)query.Criteria.Conditions
                    .First(c => c.AttributeName == "pluginassemblyid").Values[0];
                if (assemblyId == manyTypes)
                {
                    return Collection(
                        new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.A" },
                        new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.B" });
                }

                return Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.A" });
            });

        Entity? result = DataverseRegistration.GetPluginAssembly(mock.Object, "Zava.Plugins");

        // manyTypes has 2 types vs 1, so it wins despite being older.
        Assert.Equal(manyTypes, result!.Id);
    }

    // --- GetPluginTypes ------------------------------------------------------------------------

    [Fact]
    public void GetPluginTypes_maps_id_to_typename_and_skips_blank_names()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid blank = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceReturning("plugintype",
            new Entity("plugintype", a) { ["typename"] = "Zava.Plugins.A" },
            new Entity("plugintype", b) { ["typename"] = "Zava.Plugins.B" },
            new Entity("plugintype", blank) { ["typename"] = "   " });

        Dictionary<Guid, string> result = DataverseRegistration.GetPluginTypes(mock.Object, Guid.NewGuid());

        Assert.Equal(2, result.Count);
        Assert.Equal("Zava.Plugins.A", result[a]);
        Assert.Equal("Zava.Plugins.B", result[b]);
        Assert.False(result.ContainsKey(blank));
    }

    // --- GetStepImages -------------------------------------------------------------------------

    [Fact]
    public void GetStepImages_returns_empty_for_no_step_ids()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict); // no query expected
        Assert.Empty(DataverseRegistration.GetStepImages(mock.Object, Array.Empty<Guid>()));
        mock.Verify(s => s.RetrieveMultiple(It.IsAny<QueryBase>()), Times.Never);
    }

    [Fact]
    public void GetStepImages_groups_images_by_step_and_drops_orphans()
    {
        Guid stepA = Guid.NewGuid();
        Guid stepB = Guid.NewGuid();
        Entity Image(Guid stepId) => new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["sdkmessageprocessingstepid"] = stepId == Guid.Empty ? null : new EntityReference("sdkmessageprocessingstep", stepId)
        };
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessageprocessingstepimage",
            Image(stepA), Image(stepA), Image(stepB), Image(Guid.Empty));

        Dictionary<Guid, Entity[]> result = DataverseRegistration.GetStepImages(mock.Object, new[] { stepA, stepB });

        Assert.Equal(2, result[stepA].Length);
        Assert.Single(result[stepB]);
        Assert.False(result.ContainsKey(Guid.Empty));
    }

    // --- GetSecureConfigs ----------------------------------------------------------------------

    [Fact]
    public void GetSecureConfigs_returns_empty_for_no_ids()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        Assert.Empty(DataverseRegistration.GetSecureConfigs(mock.Object, Array.Empty<Guid>()));
        mock.Verify(s => s.RetrieveMultiple(It.IsAny<QueryBase>()), Times.Never);
    }

    [Fact]
    public void GetSecureConfigs_maps_id_to_value()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessageprocessingstepsecureconfig",
            new Entity("sdkmessageprocessingstepsecureconfig", id) { ["secureconfig"] = "secret" });

        Dictionary<Guid, string> result = DataverseRegistration.GetSecureConfigs(mock.Object, new[] { id });

        Assert.Equal("secret", result[id]);
    }

    // --- GetCustomApiRequestParameters / GetCustomApiResponseParameters ------------------------

    [Fact]
    public void GetCustomApiRequestParameters_returns_empty_for_no_api_ids()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        Assert.Empty(DataverseRegistration.GetCustomApiRequestParameters(mock.Object, Array.Empty<Guid>()));
    }

    [Fact]
    public void GetCustomApiRequestParameters_maps_type_and_skips_blank_names()
    {
        Guid apiId = Guid.NewGuid();
        Entity Param(string name, int type, bool optional) => new Entity("customapirequestparameter", Guid.NewGuid())
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = name,
            ["isoptional"] = optional,
            ["type"] = Opt(type),
        };
        Mock<IOrganizationService> mock = ServiceReturning("customapirequestparameter",
            Param("Amount", SdkConstants.CustomApiParameterType.Decimal, optional: true),
            Param("", SdkConstants.CustomApiParameterType.String, optional: false)); // dropped

        Dictionary<Guid, CustomApiParameterApplyDefinition[]> result =
            DataverseRegistration.GetCustomApiRequestParameters(mock.Object, new[] { apiId });

        CustomApiParameterApplyDefinition param = Assert.Single(result[apiId]);
        Assert.Equal("Amount", param.Name);
        Assert.True(param.IsOptional);
        Assert.Equal("System.Decimal", param.ParameterTypeName);
        Assert.False(param.IsCollection);
    }

    [Fact]
    public void GetCustomApiResponseParameters_maps_collection_types_and_marks_not_optional()
    {
        Guid apiId = Guid.NewGuid();
        var row = new Entity("customapiresponseproperty", Guid.NewGuid())
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = "Items",
            ["type"] = Opt(SdkConstants.CustomApiParameterType.EntityCollection),
        };
        Mock<IOrganizationService> mock = ServiceReturning("customapiresponseproperty", row);

        Dictionary<Guid, CustomApiParameterApplyDefinition[]> result =
            DataverseRegistration.GetCustomApiResponseParameters(mock.Object, new[] { apiId });

        CustomApiParameterApplyDefinition param = Assert.Single(result[apiId]);
        Assert.Equal("Items", param.Name);
        Assert.False(param.IsOptional); // response properties are never optional
        Assert.Equal("Microsoft.Xrm.Sdk.Entity", param.ParameterTypeName);
        Assert.True(param.IsCollection);
    }

    // --- FindCustomApiId / FindStepId -----------------------------------------

    [Fact]
    public void FindCustomApiId_returns_id_when_found_else_null()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> found = ServiceReturning("customapi", new Entity("customapi", id));
        Assert.Equal(id, DataverseRegistration.FindCustomApiId(found.Object, "zava_DoThing"));

        Mock<IOrganizationService> missing = ServiceReturning("customapi");
        Assert.Null(DataverseRegistration.FindCustomApiId(missing.Object, "zava_DoThing"));
    }

    [Fact]
    public void FindStepId_matches_by_step_key()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        var step = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = "Zava.Plugins.AccountPlugin - PostOpSync: my-key",
            ["stage"] = Opt(40),
            ["mode"] = Opt(0),
        };
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessageprocessingstep", step);

        var definition = new StepApplyDefinition(
            "Zava.Plugins.AccountPlugin", "Update", "account", "my-key",
            40, 0, 1, [], [], [], true, string.Empty, string.Empty);

        Assert.Equal(stepId, DataverseRegistration.FindStepId(mock.Object, pluginTypeId, definition));
    }

    [Fact]
    public void FindStepId_matches_by_message_stage_mode_when_no_step_key()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        var step = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = "legacy name without parseable key",
            ["msg.name"] = new AliasedValue("sdkmessage", "name", "Update"),
            ["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", "account"),
            ["stage"] = Opt(40),
            ["mode"] = Opt(0),
        };
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessageprocessingstep", step);

        // No stepKey → identity match on message/entity/stage/mode.
        var definition = new StepApplyDefinition(
            "Zava.Plugins.AccountPlugin", "Update", "account", string.Empty,
            40, 0, 1, [], [], [], true, string.Empty, string.Empty);

        Assert.Equal(stepId, DataverseRegistration.FindStepId(mock.Object, pluginTypeId, definition));
    }

    [Fact]
    public void FindStepId_returns_null_when_no_row_matches()
    {
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessageprocessingstep");
        var definition = new StepApplyDefinition(
            "Zava.Plugins.AccountPlugin", "Update", "account", "missing-key",
            40, 0, 1, [], [], [], true, string.Empty, string.Empty);

        Assert.Null(DataverseRegistration.FindStepId(mock.Object, Guid.NewGuid(), definition));
    }

    // --- ResolveMessageId / ResolveMessageFilterId ---------------------------------------------

    [Fact]
    public void ResolveMessageId_returns_id_when_found()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessage", new Entity("sdkmessage", id));
        Assert.Equal(id, DataverseRegistration.ResolveMessageId(mock.Object, "Update"));
    }

    [Fact]
    public void ResolveMessageId_throws_when_message_not_found()
    {
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessage");
        var ex = Assert.Throws<InvalidOperationException>(() => DataverseRegistration.ResolveMessageId(mock.Object, "Bogus"));
        Assert.Contains("Bogus", ex.Message);
    }

    [Fact]
    public void ResolveMessageFilterId_returns_null_for_blank_entity_without_querying()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        Assert.Null(DataverseRegistration.ResolveMessageFilterId(mock.Object, Guid.NewGuid(), "   "));
        mock.Verify(s => s.RetrieveMultiple(It.IsAny<QueryBase>()), Times.Never);
    }

    [Fact]
    public void ResolveMessageFilterId_returns_id_when_filter_found()
    {
        Guid filterId = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessagefilter", new Entity("sdkmessagefilter", filterId));
        Assert.Equal(filterId, DataverseRegistration.ResolveMessageFilterId(mock.Object, Guid.NewGuid(), "account"));
    }

    [Fact]
    public void ResolveMessageFilterId_returns_null_when_filter_absent()
    {
        Mock<IOrganizationService> mock = ServiceReturning("sdkmessagefilter");
        Assert.Null(DataverseRegistration.ResolveMessageFilterId(mock.Object, Guid.NewGuid(), "account"));
    }

    // --- StepMetadataCache ---------------------------------------------------------------------

    [Fact]
    public void StepMetadataCache_resolves_message_id_once_per_name()
    {
        Guid id = Guid.NewGuid();
        int queries = 0;
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                queries++;
                return Collection(new Entity("sdkmessage", id));
            });

        var cache = new DataverseRegistration.StepMetadataCache();
        Assert.Equal(id, cache.ResolveMessageId(mock.Object, "Update"));
        Assert.Equal(id, cache.ResolveMessageId(mock.Object, "Update"));

        Assert.Equal(1, queries);
    }

    [Fact]
    public void StepMetadataCache_filter_blank_entity_short_circuits_and_caches_lookups()
    {
        Guid messageId = Guid.NewGuid();
        Guid filterId = Guid.NewGuid();
        int filterQueries = 0;
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                filterQueries++;
                return Collection(new Entity("sdkmessagefilter", filterId));
            });

        var cache = new DataverseRegistration.StepMetadataCache();
        Assert.Null(cache.ResolveMessageFilterId(mock.Object, messageId, ""));
        Assert.Equal(filterId, cache.ResolveMessageFilterId(mock.Object, messageId, "account"));
        Assert.Equal(filterId, cache.ResolveMessageFilterId(mock.Object, messageId, "account"));

        Assert.Equal(1, filterQueries); // blank skipped, second "account" served from cache
    }

    // --- GetPluginPackage ----------------------------------------------------------------------

    [Fact]
    public void GetPluginPackage_returns_first_match_or_null()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> found = ServiceReturning("pluginpackage", new Entity("pluginpackage", id));
        Assert.Equal(id, DataverseRegistration.GetPluginPackage(found.Object, "zava_pkg")!.Id);

        Mock<IOrganizationService> missing = ServiceReturning("pluginpackage");
        Assert.Null(DataverseRegistration.GetPluginPackage(missing.Object, "zava_pkg"));
    }

    // --- GetPluginTypeIdByNameForAssemblies ----------------------------------------------------

    [Fact]
    public void GetPluginTypeIdByNameForAssemblies_resolves_types_per_assembly_and_dedupes_names()
    {
        Guid assemblyId = Guid.NewGuid();
        Guid typeId = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                return entity switch
                {
                    "pluginassembly" => Collection(new Entity("pluginassembly", assemblyId)),
                    "plugintype" => Collection(new Entity("plugintype", typeId) { ["typename"] = "Zava.Plugins.A" }),
                    _ => new EntityCollection(),
                };
            });

        Dictionary<string, Guid> result = DataverseRegistration.GetPluginTypeIdByNameForAssemblies(
            mock.Object, new[] { "Zava.Plugins", "Zava.Plugins" }); // duplicate assembly name deduped

        Assert.Single(result);
        Assert.Equal(typeId, result["Zava.Plugins.A"]);
    }

    [Fact]
    public void GetPluginTypeIdByNameForAssemblies_skips_assembly_not_in_dataverse()
    {
        Mock<IOrganizationService> mock = ServiceReturning("pluginassembly"); // never found
        Assert.Empty(DataverseRegistration.GetPluginTypeIdByNameForAssemblies(mock.Object, new[] { "Missing.Asm" }));
    }

    // --- GetStepItems / GetCustomApiItems (end-to-end read shaping) -----------------------------

    [Fact]
    public void GetStepItems_returns_empty_for_no_plugin_types()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        Assert.Empty(DataverseRegistration.GetStepItems(mock.Object, new Dictionary<Guid, string>(), new JsonSerializerOptions()));
    }

    [Fact]
    public void GetStepItems_shapes_a_step_and_skips_main_operation_steps()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        Guid mainOpStepId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [pluginTypeId] = "Zava.Plugins.AccountPlugin" };

        Entity StepRow(Guid id, int stage, string message) => new Entity("sdkmessageprocessingstep", id)
        {
            ["eventhandler"] = new EntityReference("plugintype", pluginTypeId),
            ["msg.name"] = new AliasedValue("sdkmessage", "name", message),
            ["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", "account"),
            ["stage"] = Opt(stage),
            ["mode"] = Opt(0),
            ["rank"] = 1,
            ["name"] = "Zava.Plugins.AccountPlugin - PostOpSync: my-key",
            ["statecode"] = Opt(0),
        };

        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                return entity switch
                {
                    // The stage-30 step is the internal Custom API step (message == the Custom API unique name),
                    // so it is skipped; the stage-40 Update step is kept.
                    "sdkmessageprocessingstep" => Collection(StepRow(stepId, 40, "Update"), StepRow(mainOpStepId, 30, "zava_DoThing")),
                    "customapi" => Collection(new Entity("customapi", Guid.NewGuid()) { ["uniquename"] = "zava_DoThing" }),
                    _ => new EntityCollection(),
                };
            });

        List<RegistrationItem> items = DataverseRegistration.GetStepItems(mock.Object, pluginTypes, new JsonSerializerOptions());

        RegistrationItem item = Assert.Single(items);
        Assert.Equal("step", item.Kind);
        Assert.Equal("Zava.Plugins.AccountPlugin", item.Type);
        Assert.Equal("Update", item.Definition.GetProperty("message").GetString());
        Assert.Equal("account", item.Definition.GetProperty("entityLogicalName").GetString());
        Assert.Equal("my-key", item.Definition.GetProperty("stepKey").GetString());
        Assert.Equal(40, item.Definition.GetProperty("stage").GetInt32());
    }

    [Fact]
    public void GetStepItems_keeps_a_main_operation_step_on_a_non_custom_api_message()   // A7
    {
        Guid pluginTypeId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [pluginTypeId] = "Zava.Plugins.AccountPlugin" };

        Entity Step(Guid id, int stage, string message) => new Entity("sdkmessageprocessingstep", id)
        {
            ["eventhandler"] = new EntityReference("plugintype", pluginTypeId),
            ["msg.name"] = new AliasedValue("sdkmessage", "name", message),
            ["stage"] = Opt(stage),
            ["mode"] = Opt(0),
            ["rank"] = 1,
            ["name"] = "Zava.Plugins.AccountPlugin - MainOp: k",
            ["statecode"] = Opt(0),
        };

        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) => (q as QueryExpression)?.EntityName switch
            {
                // A stage-30 step on a standard message with NO matching Custom API must be kept (not skipped).
                "sdkmessageprocessingstep" => Collection(Step(Guid.NewGuid(), 30, "Create")),
                "customapi" => new EntityCollection(),   // no Custom APIs
                _ => new EntityCollection(),
            });

        RegistrationItem item = Assert.Single(DataverseRegistration.GetStepItems(mock.Object, pluginTypes, new JsonSerializerOptions()));
        Assert.Equal("Create", item.Definition.GetProperty("message").GetString());
        Assert.Equal(30, item.Definition.GetProperty("stage").GetInt32());
    }

    [Fact]
    public void GetCustomApiItems_shapes_an_api_with_its_parameters()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid apiId = Guid.NewGuid();
        var pluginTypes = new Dictionary<Guid, string> { [pluginTypeId] = "Zava.Plugins.MyApiPlugin" };

        var apiRow = new Entity("customapi", apiId)
        {
            ["uniquename"] = "zava_DoThing",
            ["isfunction"] = false,
            ["isprivate"] = true,
            ["bindingtype"] = Opt(1),
            ["allowedcustomprocessingsteptype"] = Opt(0),
            ["workflowsdkstepenabled"] = false,
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
        };
        var requestParam = new Entity("customapirequestparameter", Guid.NewGuid())
        {
            ["customapiid"] = new EntityReference("customapi", apiId),
            ["name"] = "Target",
            ["isoptional"] = false,
            ["type"] = Opt(SdkConstants.CustomApiParameterType.EntityReference),
        };

        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                return entity switch
                {
                    "customapi" => Collection(apiRow),
                    "customapirequestparameter" => Collection(requestParam),
                    _ => new EntityCollection(),
                };
            });

        List<RegistrationItem> items = DataverseRegistration.GetCustomApiItems(mock.Object, pluginTypes, new JsonSerializerOptions());

        RegistrationItem item = Assert.Single(items);
        Assert.Equal("custom-api", item.Kind);
        Assert.Equal("zava_DoThing", item.Type);
        Assert.Equal("zava_DoThing", item.Definition.GetProperty("uniqueName").GetString());
        Assert.True(item.Definition.GetProperty("isBound").GetBoolean()); // bindingType 1 → isBound true
        Assert.True(item.Definition.GetProperty("isPrivate").GetBoolean());
        JsonElement inputs = item.Definition.GetProperty("inputParameters");
        Assert.Equal(1, inputs.GetArrayLength());
        Assert.Equal("Target", inputs[0].GetProperty("name").GetString());
        Assert.Equal("Microsoft.Xrm.Sdk.EntityReference", inputs[0].GetProperty("parameterTypeName").GetString());
    }

    // --- DeleteIfExists ------------------------------------------------------------------------

    [Fact]
    public void DeleteIfExists_deletes_the_record()
    {
        Guid id = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        DataverseRegistration.DeleteIfExists(mock.Object, "customapi", id);
        mock.Verify(s => s.Delete("customapi", id), Times.Once);
    }

    [Fact]
    public void DeleteIfExists_swallows_object_does_not_exist_fault()
    {
        Guid id = Guid.NewGuid();
        var fault = new OrganizationServiceFault { ErrorCode = -2147220969 };
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.Delete("customapi", id))
            .Throws(new FaultException<OrganizationServiceFault>(fault, "does not exist"));

        // Already-gone delete is treated as success (no throw).
        DataverseRegistration.DeleteIfExists(mock.Object, "customapi", id);
    }

    [Fact]
    public void DeleteIfExists_rethrows_other_faults()
    {
        Guid id = Guid.NewGuid();
        var fault = new OrganizationServiceFault { ErrorCode = -1 };
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.Delete("customapi", id))
            .Throws(new FaultException<OrganizationServiceFault>(fault, "some other error"));

        Assert.Throws<FaultException<OrganizationServiceFault>>(
            () => DataverseRegistration.DeleteIfExists(mock.Object, "customapi", id));
    }

    // --- SyncStepState (write) -----------------------------------------------------------------

    [Fact]
    public void SyncStepState_enabled_transitions_to_active()
    {
        Guid stepId = Guid.NewGuid();
        SetStateRequest? captured = null;
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback((OrganizationRequest r) => captured = r as SetStateRequest)
            .Returns(new OrganizationResponse());

        DataverseRegistration.SyncStepState(mock.Object, stepId, enabled: true);

        Assert.NotNull(captured);
        Assert.Equal(stepId, captured!.EntityMoniker.Id);
        Assert.Equal(0, captured.State.Value);
        Assert.Equal(1, captured.Status.Value);
    }

    [Fact]
    public void SyncStepState_disabled_transitions_to_inactive()
    {
        Guid stepId = Guid.NewGuid();
        SetStateRequest? captured = null;
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback((OrganizationRequest r) => captured = r as SetStateRequest)
            .Returns(new OrganizationResponse());

        DataverseRegistration.SyncStepState(mock.Object, stepId, enabled: false);

        Assert.Equal(1, captured!.State.Value);
        Assert.Equal(2, captured.Status.Value);
    }

    // --- SyncStepSecureConfig (write) ----------------------------------------------------------

    [Fact]
    public void SyncStepSecureConfig_creates_and_links_when_value_present_and_no_existing()
    {
        Guid stepId = Guid.NewGuid();
        Guid newConfigId = Guid.NewGuid();
        var created = new List<Entity>();
        var updated = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", stepId, It.IsAny<ColumnSet>()))
            .Returns(new Entity("sdkmessageprocessingstep", stepId)); // no existing link
        mock.Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns((Entity e) => { created.Add(e); return newConfigId; });
        mock.Setup(s => s.Update(It.IsAny<Entity>())).Callback((Entity e) => updated.Add(e));

        DataverseRegistration.SyncStepSecureConfig(mock.Object, stepId, "secret");

        Entity config = Assert.Single(created);
        Assert.Equal("sdkmessageprocessingstepsecureconfig", config.LogicalName);
        Assert.Equal("secret", config.GetAttributeValue<string>("secureconfig"));
        Entity link = Assert.Single(updated);
        Assert.Equal(newConfigId, link.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")?.Id);
    }

    [Fact]
    public void SyncStepSecureConfig_updates_existing_secure_config_row()
    {
        Guid stepId = Guid.NewGuid();
        Guid existingConfigId = Guid.NewGuid();
        var updated = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", stepId, It.IsAny<ColumnSet>()))
            .Returns(new Entity("sdkmessageprocessingstep", stepId)
            {
                ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", existingConfigId),
            });
        mock.Setup(s => s.Update(It.IsAny<Entity>())).Callback((Entity e) => updated.Add(e));

        DataverseRegistration.SyncStepSecureConfig(mock.Object, stepId, "rotated");

        Entity update = Assert.Single(updated);
        Assert.Equal(existingConfigId, update.Id);
        Assert.Equal("rotated", update.GetAttributeValue<string>("secureconfig"));
        // Updates the value in place — no create.
        mock.Verify(s => s.Create(It.IsAny<Entity>()), Times.Never);
    }

    [Fact]
    public void SyncStepSecureConfig_clears_and_deletes_when_value_empty_and_existing()
    {
        Guid stepId = Guid.NewGuid();
        Guid existingConfigId = Guid.NewGuid();
        Entity? linkUpdate = null;
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", stepId, It.IsAny<ColumnSet>()))
            .Returns(new Entity("sdkmessageprocessingstep", stepId)
            {
                ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", existingConfigId),
            });
        mock.Setup(s => s.Update(It.IsAny<Entity>())).Callback((Entity e) => linkUpdate = e);

        DataverseRegistration.SyncStepSecureConfig(mock.Object, stepId, string.Empty);

        // Link cleared on the step, then the orphaned secure-config row deleted.
        Assert.NotNull(linkUpdate);
        Assert.Null(linkUpdate!.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid"));
        mock.Verify(s => s.Delete("sdkmessageprocessingstepsecureconfig", existingConfigId), Times.Once);
    }

    [Fact]
    public void SyncStepSecureConfig_no_value_and_no_existing_is_a_noop()
    {
        Guid stepId = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", stepId, It.IsAny<ColumnSet>()))
            .Returns(new Entity("sdkmessageprocessingstep", stepId));

        DataverseRegistration.SyncStepSecureConfig(mock.Object, stepId, string.Empty);
        // Strict mock: any Create/Update/Delete would have thrown.
    }

    // --- UpsertStep (write orchestration) ------------------------------------------------------

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void UpsertStep_throws_when_plugin_type_unknown()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.Missing", message = "Update", stage = 40, mode = 0, stepKey = "k" });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.UpsertStep(mock.Object, new Dictionary<string, Guid>(), def, verbose: false));
        Assert.Contains("Zava.Plugins.Missing", ex.Message);
    }

    [Fact]
    public void UpsertStep_creates_a_new_step_with_normalized_stage_and_rank()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        Guid newStepId = Guid.NewGuid();
        var created = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                return entity switch
                {
                    "sdkmessage" => Collection(new Entity("sdkmessage", messageId)),
                    _ => new EntityCollection(), // no existing step, no filter, no images
                };
            });
        mock.Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns((Entity e) => { created.Add(e); return e.LogicalName == "sdkmessageprocessingstep" ? newStepId : Guid.NewGuid(); });
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", It.IsAny<Guid>(), It.IsAny<ColumnSet>()))
            .Returns((string en, Guid id, ColumnSet _) => new Entity(en, id));
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());

        var pluginTypeIds = new Dictionary<string, Guid> { ["Zava.Plugins.AccountPlugin"] = pluginTypeId };
        // Async (mode 1) with PreOperation stage (20) and negative order: must normalize to PostOperation + rank 1.
        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", stage = 20, mode = 1, executionOrder = -3, stepKey = "k" });

        DataverseRegistration.UpsertStep(mock.Object, pluginTypeIds, def, verbose: false);

        Entity step = created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.Equal(40, step.GetAttributeValue<OptionSetValue>("stage").Value); // async coerced to PostOperation
        Assert.Equal(1, step.GetAttributeValue<OptionSetValue>("mode").Value);
        Assert.Equal(1, step.GetAttributeValue<int>("rank")); // negative order clamped to 1
        Assert.Equal(messageId, step.GetAttributeValue<EntityReference>("sdkmessageid").Id);
    }

    [Fact]
    public void UpsertStep_updates_when_an_existing_step_is_found()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        Guid existingStepId = Guid.NewGuid();
        var updated = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                return entity switch
                {
                    "sdkmessage" => Collection(new Entity("sdkmessage", messageId)),
                    "sdkmessageprocessingstep" => Collection(new Entity("sdkmessageprocessingstep", existingStepId)
                    {
                        ["name"] = "Zava.Plugins.AccountPlugin - PostOpSync: k",
                        ["stage"] = Opt(40),
                        ["mode"] = Opt(0),
                    }),
                    _ => new EntityCollection(),
                };
            });
        mock.Setup(s => s.Update(It.IsAny<Entity>())).Callback((Entity e) => updated.Add(e));
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", It.IsAny<Guid>(), It.IsAny<ColumnSet>()))
            .Returns((string en, Guid id, ColumnSet _) => new Entity(en, id));
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());

        var pluginTypeIds = new Dictionary<string, Guid> { ["Zava.Plugins.AccountPlugin"] = pluginTypeId };
        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", stage = 40, mode = 0, stepKey = "k" });

        DataverseRegistration.UpsertStep(mock.Object, pluginTypeIds, def, verbose: false);

        Assert.Contains(updated, e => e.LogicalName == "sdkmessageprocessingstep" && e.Id == existingStepId);
        mock.Verify(s => s.Create(It.IsAny<Entity>()), Times.Never);
    }

    // --- UpsertCustomApi + parameter sync (write orchestration) --------------------------------

    [Fact]
    public void UpsertCustomApi_creates_api_and_syncs_request_and_response_parameters()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid newApiId = Guid.NewGuid();
        var created = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(new EntityCollection()); // api not found, no existing params
        mock.Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns((Entity e) => { created.Add(e); return e.LogicalName == "customapi" ? newApiId : Guid.NewGuid(); });

        var pluginTypeIds = new Dictionary<string, Guid> { ["Zava.Plugins.MyApi"] = pluginTypeId };
        JsonElement def = Json(new
        {
            pluginTypeName = "Zava.Plugins.MyApi",
            uniqueName = "zava_DoThing",
            isFunction = false,
            isBound = false,
            inputParameters = new[] { new { name = "Amount", parameterTypeName = "System.Decimal", isOptional = true } },
            outputParameters = new[] { new { name = "Result", parameterTypeName = "System.String" } },
        });

        DataverseRegistration.UpsertCustomApi(mock.Object, pluginTypeIds, def, verbose: false);

        Entity api = created.Single(e => e.LogicalName == "customapi");
        Assert.Equal("zava_DoThing", api.GetAttributeValue<string>("uniquename"));
        Assert.Equal(pluginTypeId, api.GetAttributeValue<EntityReference>("plugintypeid").Id);

        Entity reqParam = created.Single(e => e.LogicalName == "customapirequestparameter");
        Assert.Equal("Amount", reqParam.GetAttributeValue<string>("name"));
        Assert.Equal("Amount", reqParam.GetAttributeValue<string>("uniquename")); // bare identifier, no "{api}." prefix
        Assert.Equal(SdkConstants.CustomApiParameterType.Decimal, reqParam.GetAttributeValue<OptionSetValue>("type").Value);

        Entity respParam = created.Single(e => e.LogicalName == "customapiresponseproperty");
        Assert.Equal("Result", respParam.GetAttributeValue<string>("name"));
        Assert.Equal("Result", respParam.GetAttributeValue<string>("uniquename")); // bare identifier, no "{api}." prefix
    }

    [Fact]
    public void UpsertCustomApi_throws_when_plugin_type_unknown()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.Missing", uniqueName = "zava_X" });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.UpsertCustomApi(mock.Object, new Dictionary<string, Guid>(), def, verbose: false));
        Assert.Contains("Zava.Plugins.Missing", ex.Message);
    }

    [Fact]
    public void SyncParameterCollection_updates_existing_and_deletes_undeclared()
    {
        Guid apiId = Guid.NewGuid();
        Guid keepId = Guid.NewGuid();
        Guid dropId = Guid.NewGuid();
        var updated = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(Collection(
                new Entity("customapirequestparameter", keepId) { ["name"] = "Keep", ["uniquename"] = "Keep", ["type"] = new OptionSetValue(SdkConstants.CustomApiParameterType.String), ["isoptional"] = true }, // matches desired: kept as is
                new Entity("customapirequestparameter", dropId) { ["name"] = "Drop" }));
        mock.Setup(s => s.Update(It.IsAny<Entity>())).Callback((Entity e) => updated.Add(e));

        var desired = new[]
        {
            new CustomApiParameterApplyDefinition("Keep", true, "System.String", false, null),
        };

        DataverseRegistration.SyncCustomApiRequestParameters(mock.Object, apiId, "zava_X", desired, verbose: false);

        // "Keep" exists unchanged → untouched; "Drop" not desired → deleted.
        Assert.DoesNotContain(updated, e => e.Id == keepId);
        mock.Verify(s => s.Delete("customapirequestparameter", keepId), Times.Never);
        mock.Verify(s => s.Delete("customapirequestparameter", dropId), Times.Once);
        mock.Verify(s => s.Create(It.IsAny<Entity>()), Times.Never);
    }

    // --- DeleteStepByDefinition / DeleteCustomApi ----------------------------------------------

    [Fact]
    public void DeleteStepByDefinition_deletes_by_stored_name_scoped_to_our_plugin_type()
    {
        // The step key does not resolve (FindStepId finds no row) but the stored name does - scoped to the type.
        Guid pluginTypeId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        List<QueryExpression> queries = [];
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                var qe = (QueryExpression)q;
                queries.Add(qe);
                bool byName = qe.Criteria.Conditions.Any(c => c.AttributeName == "name");
                return qe.EntityName == "sdkmessageprocessingstep" && byName
                    ? Collection(new Entity("sdkmessageprocessingstep", stepId))
                    : new EntityCollection();
            });

        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.AccountPlugin", name = "MyStep", message = "Update", stage = 40, mode = 0, stepKey = "k" });

        DataverseRegistration.DeleteStepByDefinition(mock.Object, def, new Dictionary<string, Guid> { ["Zava.Plugins.AccountPlugin"] = pluginTypeId });

        mock.Verify(s => s.Delete("sdkmessageprocessingstep", stepId), Times.Once);
        QueryExpression nameQuery = Assert.Single(queries, q => q.Criteria.Conditions.Any(c => c.AttributeName == "name"));
        Assert.Contains(nameQuery.Criteria.Conditions, c => c.AttributeName == "eventhandler" && Equals(c.Values[0], pluginTypeId));
        Assert.DoesNotContain(queries, q => q.EntityName == "plugintype"); // never an environment-wide type lookup
    }

    [Fact]
    public void DeleteStepByDefinition_resolves_the_step_by_key_under_our_plugin_type()
    {
        Guid pluginTypeId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
                (q as QueryExpression)?.EntityName == "sdkmessageprocessingstep"
                    ? Collection(new Entity("sdkmessageprocessingstep", stepId)
                    {
                        ["name"] = "Zava.Plugins.AccountPlugin - PostOpSync: k",
                        ["stage"] = Opt(40),
                        ["mode"] = Opt(0),
                    })
                    : new EntityCollection());

        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", stage = 40, mode = 0, stepKey = "k" });

        DataverseRegistration.DeleteStepByDefinition(mock.Object, def, new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["Zava.Plugins.AccountPlugin"] = pluginTypeId });

        mock.Verify(s => s.Delete("sdkmessageprocessingstep", stepId), Times.Once);
    }

    [Fact]
    public void DeleteStepByDefinition_never_deletes_a_step_whose_type_is_not_in_the_applied_assemblies()
    {
        // A same-named step exists in the environment, but its plugin type is not one of ours: no lookup, no delete.
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(Collection(new Entity("sdkmessageprocessingstep", Guid.NewGuid())));

        JsonElement def = Json(new { pluginTypeName = "Other.Plugins.AccountPlugin", name = "MyStep", message = "Update", stage = 40, mode = 0, stepKey = "k" });

        DataverseRegistration.DeleteStepByDefinition(mock.Object, def, new Dictionary<string, Guid> { ["Zava.Plugins.AccountPlugin"] = Guid.NewGuid() });

        mock.Verify(s => s.Delete(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        mock.Verify(s => s.RetrieveMultiple(It.IsAny<QueryBase>()), Times.Never);
    }

    [Fact]
    public void DeleteCustomApi_deletes_parameters_then_the_api()
    {
        Guid apiId = Guid.NewGuid();
        Guid reqParamId = Guid.NewGuid();
        Guid respParamId = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                return entity switch
                {
                    "customapi" => Collection(new Entity("customapi", apiId)),
                    "customapirequestparameter" => Collection(new Entity("customapirequestparameter", reqParamId)),
                    "customapiresponseproperty" => Collection(new Entity("customapiresponseproperty", respParamId)),
                    _ => new EntityCollection(),
                };
            });

        JsonElement def = Json(new { pluginTypeName = "Zava.Plugins.MyApi", uniqueName = "zava_DoThing" });

        DataverseRegistration.DeleteCustomApi(mock.Object, def);

        mock.Verify(s => s.Delete("customapirequestparameter", reqParamId), Times.Once);
        mock.Verify(s => s.Delete("customapiresponseproperty", respParamId), Times.Once);
        mock.Verify(s => s.Delete("customapi", apiId), Times.Once);
    }

    [Fact]
    public void DeleteCustomApi_does_nothing_when_api_not_found()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>())).Returns(new EntityCollection());

        DataverseRegistration.DeleteCustomApi(mock.Object, Json(new { uniqueName = "zava_Absent" }));

        mock.Verify(s => s.Delete(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    // --- SyncStepImages (write) ----------------------------------------------------------------

    [Fact]
    public void SyncStepImages_creates_pre_and_post_images_and_deletes_stale()
    {
        Guid stepId = Guid.NewGuid();
        Guid staleImageId = Guid.NewGuid();
        var created = new List<Entity>();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(Collection(new Entity("sdkmessageprocessingstepimage", staleImageId)
            {
                ["entityalias"] = "old",
                ["imagetype"] = Opt(0),
            }));
        mock.Setup(s => s.Create(It.IsAny<Entity>())).Callback((Entity e) => created.Add(e)).Returns(Guid.NewGuid());

        var pre = new[] { new ImageApplyDefinition("preimg", new[] { "name" }) };
        var post = new[] { new ImageApplyDefinition("postimg", new[] { "name" }) };

        DataverseRegistration.SyncStepImages(mock.Object, stepId, "Create", pre, post);

        // Create message + PostImage → messagepropertyname "id"; everything else "Target".
        Entity preCreate = created.Single(e => e.GetAttributeValue<string>("entityalias") == "preimg");
        Assert.Equal("Target", preCreate.GetAttributeValue<string>("messagepropertyname"));
        Entity postCreate = created.Single(e => e.GetAttributeValue<string>("entityalias") == "postimg");
        Assert.Equal("id", postCreate.GetAttributeValue<string>("messagepropertyname"));

        // The pre-existing "old" image isn't desired → deleted.
        mock.Verify(s => s.Delete("sdkmessageprocessingstepimage", staleImageId), Times.Once);
    }
}
