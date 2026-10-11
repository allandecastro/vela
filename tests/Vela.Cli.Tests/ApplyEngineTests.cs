using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text.Json;
using Vela.Cli;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

[Collection("ConsoleRedirect")]
public class ApplyEngineTests
{
    private const string PluginType = "Zava.Plugins.AccountPlugin";

    // A service that resolves the plugin assembly/type successfully, then fails on the first
    // step query — simulating a mid-apply failure after the publish/resolve phase.
    private static Mock<IOrganizationService> ServiceThatFailsAfterResolve()
    {
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                if (entity == "pluginassembly")
                {
                    var ec = new EntityCollection();
                    ec.Entities.Add(new Entity("pluginassembly", Guid.NewGuid()));
                    return ec;
                }

                if (entity == "plugintype")
                {
                    var type = new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType };
                    var ec = new EntityCollection();
                    ec.Entities.Add(type);
                    return ec;
                }

                throw new InvalidOperationException("simulated dataverse failure");
            });
        return mock;
    }

    private static RegistrationDiff DiffWithOneChangedStep()
    {
        var after = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x" });
        var before = Json(new { pluginTypeName = PluginType, message = "Update", stage = 20, mode = 0, stepKey = "dv-x" });
        var change = new RegistrationChange("step", PluginType, before, after);
        return new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [],
            Removed: [],
            Changed: [change],
            LocalItems: [],
            CurrentItems: []);
    }

    [Fact]
    public void ApplyDiff_resolves_each_message_once_across_many_steps()
    {
        // Three added steps that all use the "Update" message. The sdkmessage lookup must be
        // cached for the run, so sdkmessage is queried once rather than once per step (N+1).
        int sdkMessageQueries = 0;
        var mock = new Mock<IOrganizationService>(MockBehavior.Strict);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                switch (entity)
                {
                    case "pluginassembly":
                        var asm = new EntityCollection();
                        asm.Entities.Add(new Entity("pluginassembly", Guid.NewGuid()));
                        return asm;
                    case "plugintype":
                        var types = new EntityCollection();
                        types.Entities.Add(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType });
                        return types;
                    case "sdkmessage":
                        sdkMessageQueries++;
                        var msg = new EntityCollection();
                        msg.Entities.Add(new Entity("sdkmessage", Guid.NewGuid()));
                        return msg;
                    case "sdkmessageprocessingstep":
                        return new EntityCollection(); // no existing step → create path
                    case "sdkmessageprocessingstepimage":
                        return new EntityCollection(); // no images to sync
                    default:
                        throw new InvalidOperationException($"unexpected query: {entity}");
                }
            });
        mock.Setup(s => s.Create(It.IsAny<Entity>())).Returns(Guid.NewGuid());
        // New steps have no secure config: the reconcile reads the (empty) link and does nothing.
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", It.IsAny<Guid>(), It.IsAny<ColumnSet>()))
            .Returns((string e, Guid id, ColumnSet _) => new Entity(e, id));
        // Any other request.
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());

        RegistrationDiff diff = DiffWithAddedSteps("dv-a", "dv-b", "dv-c");

        DataverseRegistration.ApplyDiff(mock.Object, diff, [], null, [], verbose: false);

        Assert.Equal(1, sdkMessageQueries);
    }

    private static RegistrationDiff DiffWithAddedSteps(params string[] stepKeys)
    {
        var added = new List<RegistrationItem>();
        foreach (string stepKey in stepKeys)
        {
            // No entityLogicalName → message-filter resolution short-circuits without a query,
            // keeping the test focused on the sdkmessage cache.
            JsonElement def = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey });
            added.Add(new RegistrationItem("step", PluginType, def));
        }

        return new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: added.ToArray(),
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);
    }

    [Fact]
    public void ApplyDiff_disables_step_and_reconciles_configuration()
    {
        // Driven through the batched write path (FakeOrgService dispatches each ExecuteMultiple child), so the step
        // row, its secure-config row + link, and the disabling update still all land.
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType }),
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                _ => new EntityCollection(), // no existing step/images
            },
            OnRetrieve = (e, id, _) => new Entity(e, id), // no existing secure-config link
        };

        JsonElement def = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", enabled = false, unsecureConfig = "u-cfg", secureConfig = "hmac-sha256:redacted" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", PluginType, def)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false,
            secureConfigByStepKey: new Dictionary<string, string> { [PluginType + ":stepkey:dv-x"] = "s-cfg" });

        // Unsecure config is written directly onto the step entity.
        Entity step = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.Equal("u-cfg", step.GetAttributeValue<string>("configuration"));

        // Secure config lives on its own row, created with the value and linked back to the step.
        Entity secure = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig");
        Assert.Equal("s-cfg", secure.GetAttributeValue<string>("secureconfig"));
        Assert.Contains(svc.Updated, e => e.LogicalName == "sdkmessageprocessingstep"
            && e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")?.Id == secure.Id);

        // Disabled step transitions to the inactive state through a batched, state-only update.
        Entity? disable = FindBatchedStateUpdate(svc);
        Assert.NotNull(disable);
        Assert.Equal(step.Id, disable!.Id);
        Assert.Equal(1, disable.GetAttributeValue<OptionSetValue>("statecode").Value);
        Assert.Equal(2, disable.GetAttributeValue<OptionSetValue>("statuscode").Value);
    }

    [Fact]
    public void ApplyDiff_disables_a_created_step_in_the_same_batch_right_behind_its_create()   // #445
    {
        // Dataverse ignores statecode on Create, so a .Disabled() step is born enabled. The update that disables it must
        // not wait for tier 2 (images, secure config): it runs in tier 1, immediately after the create.
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType }),
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };

        JsonElement def = Json(new
        {
            pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", enabled = false,
            preImages = new[] { new { alias = "pre", attributes = new[] { "name" } } },
        });
        var diff = new RegistrationDiff(["Zava.Plugins"], ["Zava.Plugins"], [new RegistrationItem("step", PluginType, def)], [], [], [], []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false);

        Assert.Equal(2, svc.Batches.Count);
        OrganizationRequest[] tier1 = svc.Batches[0].Requests.ToArray();
        Assert.Collection(tier1,
            r => Assert.Equal("sdkmessageprocessingstep", Assert.IsType<CreateRequest>(r).Target.LogicalName),
            r => Assert.Equal(1, Assert.IsType<UpdateRequest>(r).Target.GetAttributeValue<OptionSetValue>("statecode").Value));
        Assert.All(svc.Batches[1].Requests, r => Assert.Equal("sdkmessageprocessingstepimage", Assert.IsType<CreateRequest>(r).Target.LogicalName));
    }

    [Fact]
    public void ApplyDiff_disables_an_existing_step_in_the_same_update_as_its_other_columns()   // #445
    {
        // An existing step needs no separate state write: its update carries statecode/statuscode with the rest.
        Guid existingStep = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType }),
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", existingStep) { ["name"] = $"{PluginType} - PostOpSync: dv-x" }),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };

        JsonElement before = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x" });
        JsonElement after = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", enabled = false });
        var diff = new RegistrationDiff(["Zava.Plugins"], ["Zava.Plugins"], [], [], [new RegistrationChange("step", PluginType, before, after)], [], []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false);

        Entity update = Assert.IsType<UpdateRequest>(Assert.Single(Assert.Single(svc.Batches).Requests)).Target;
        Assert.Equal(existingStep, update.Id);
        Assert.Equal(1, update.GetAttributeValue<OptionSetValue>("statecode").Value);
        Assert.Equal(2, update.GetAttributeValue<OptionSetValue>("statuscode").Value);
        Assert.True(update.Contains("rank"));
    }

    [Fact]
    public void ApplyDiff_falls_back_to_one_by_one_when_ExecuteMultiple_is_unsupported()   // #305 batched-fault gap
    {
        // A whole ExecuteMultiple batch throws (as an org without the ExecuteMultiple privilege would): the apply
        // must fall back to issuing each write individually, so the step still lands.
        var svc = new TestSupport.FakeOrgService
        {
            FailWholeBatch = true,
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType }),
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
        };

        JsonElement def = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", PluginType, def)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false);

        // The batch was attempted (and threw), then the step was created through the one-by-one fallback.
        Assert.NotEmpty(svc.Batches);
        Assert.Contains(svc.Created, e => e.LogicalName == "sdkmessageprocessingstep");
    }

    [Fact]
    public void ApplyDiff_deletes_removed_step_through_the_service_seam()
    {
        // Demonstrates the apply orchestration is fully drivable through the IOrganizationService seam
        // (no live org): a removed step is located under its plugin type (resolved from the applied assembly only)
        // and deleted via a batched DeleteRequest.
        Guid stepId = Guid.NewGuid();
        Guid assemblyId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", assemblyId)),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType }),
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", stepId)),
                _ => new EntityCollection(),
            },
        };

        var removed = new RegistrationItem("step", PluginType,
            Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", name = "MyStep" }));
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [],
            Removed: [removed],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false);

        Assert.Contains(("sdkmessageprocessingstep", stepId), svc.Deleted);
        // Every plugin-type lookup is scoped to the applied assembly; none is environment-wide.
        Assert.All(svc.Queries.Where(q => q.EntityName == "plugintype"),
            q => Assert.Contains(q.Criteria.Conditions, c => c.AttributeName == "pluginassemblyid" && Equals(c.Values[0], assemblyId)));
    }

    [Fact]
    public void ApplyDiff_does_not_delete_a_removed_step_of_a_type_outside_the_applied_assemblies()
    {
        // The removed step's plugin type is not in the assembly being applied (it lives elsewhere in the environment):
        // apply must not hunt for it environment-wide, so nothing is deleted.
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Zava.Plugins.Other" }),
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", Guid.NewGuid())),
                _ => new EntityCollection(),
            },
        };
        var removed = new RegistrationItem("step", PluginType,
            Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", name = "MyStep" }));
        var diff = new RegistrationDiff(["Zava.Plugins"], ["Zava.Plugins"], [], [removed], [], [], []);

        DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false);

        Assert.Empty(svc.Deleted);
    }

    // A Custom API whose implementing class was renamed: same uniqueName, the live row implemented by the old type.
    private static readonly Guid OldTypeId = Guid.NewGuid();
    private static readonly Guid NewTypeId = Guid.NewGuid();
    private static readonly Guid ForeignTypeId = Guid.NewGuid();
    private static readonly Guid DeletedTypeId = Guid.NewGuid();

    // The live Custom API starts implemented by liveImplementation; a customapi update that writes plugintypeid
    // changes it, so later reads (the ownership check of the re-point) see the detach.
    private static (TestSupport.FakeOrgService Svc, Guid ApiId, Guid NewTypeId) RenamedCustomApiService(Guid? liveImplementation = null)
    {
        Guid apiId = Guid.NewGuid();
        Guid? initial = liveImplementation == Guid.Empty ? null : liveImplementation ?? OldTypeId; // Guid.Empty: implemented by nothing
        TestSupport.FakeOrgService svc = null!;
        svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                // After the upload only the new type exists (the old one was removed with the package update).
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", NewTypeId) { ["typename"] = "Zava.Plugins.ScoreApiV2" }),
                "customapi" => TestSupport.FakeOrgService.Collection(new Entity("customapi", apiId)),
                _ => new EntityCollection(), // no package yet, no parameters
            },
            OnRetrieve = (entity, id, _) =>
            {
                if (entity == "customapi")
                {
                    Entity? written = svc.Updated.LastOrDefault(e => e.LogicalName == "customapi" && e.Contains("plugintypeid"));
                    EntityReference? current = written != null
                        ? written["plugintypeid"] as EntityReference
                        : initial is Guid typeId ? new EntityReference("plugintype", typeId) : null;
                    return new Entity("customapi", id) { ["plugintypeid"] = current };
                }

                if (entity == "plugintype" && id == DeletedTypeId)
                {
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = DataverseRegistration.ObjectDoesNotExistErrorCode }, "plugintype does not exist");
                }

                if (entity == "plugintype")
                {
                    string typeName = id == OldTypeId ? "Zava.Plugins.ScoreApi" : id == NewTypeId ? "Zava.Plugins.ScoreApiV2" : "Other.Solution.ScoreApi";
                    return new Entity("plugintype", id) { ["typename"] = typeName, ["assemblyname"] = id == ForeignTypeId ? "Other.Solution" : "Zava.Plugins" };
                }

                return new Entity(entity, id);
            },
        };
        return (svc, apiId, NewTypeId);
    }

    private static RegistrationChange CustomApiChange(string beforeType, string afterType, bool beforePrivate = false)
        => new RegistrationChange("custom-api", afterType,
            Json(new { pluginTypeName = beforeType, uniqueName = "zava_Score", isPrivate = beforePrivate }),
            Json(new { pluginTypeName = afterType, uniqueName = "zava_Score", isPrivate = false }));

    private static RegistrationDiff ChangedApiDiff(RegistrationChange change) => new RegistrationDiff(
        LocalAssemblies: ["Zava.Plugins"],
        CurrentAssemblies: ["Zava.Plugins"],
        Added: [],
        Removed: [],
        Changed: [change],
        LocalItems: [],
        CurrentItems: []);

    [Fact]
    public void ApplyDiff_detaches_a_renamed_custom_api_before_the_package_upload_then_repoints_it()   // A-41
    {
        (TestSupport.FakeOrgService svc, Guid apiId, Guid newTypeId) = RenamedCustomApiService();
        string package = Path.Combine(Path.GetTempPath(), $"vela-a41-{Guid.NewGuid():N}.nupkg");
        File.WriteAllBytes(package, [1, 2, 3]);
        try
        {
            DataverseRegistration.ApplyDiff(svc, ChangedApiDiff(CustomApiChange("Zava.Plugins.ScoreApi", "Zava.Plugins.ScoreApiV2")),
                [new PluginPackagePayload("zava_Plugins", "1.0.0", package)], null, [], verbose: false);
        }
        finally
        {
            File.Delete(package);
        }

        // Detach (customapi update), then the upload (pluginpackage create), then the re-point (customapi update).
        Assert.Equal(["update:customapi", "create:pluginpackage", "update:customapi"], svc.Writes);
        Entity detach = svc.Updated[0];
        Assert.Equal(apiId, detach.Id);
        Assert.True(detach.Contains("plugintypeid"));
        Assert.Null(detach["plugintypeid"]);
        Entity repoint = svc.Updated[1];
        Assert.Equal(apiId, repoint.Id);
        Assert.Equal(newTypeId, repoint.GetAttributeValue<EntityReference>("plugintypeid").Id);
    }

    [Fact]
    public void ApplyDiff_refuses_to_detach_a_custom_api_now_implemented_by_another_type()   // A-41, PR #428 review
    {
        // Between plan and apply, another solution took over the unique name: the live API no longer points at the
        // old type the plan saw, so it must not be detached (nor anything uploaded).
        (TestSupport.FakeOrgService svc, _, _) = RenamedCustomApiService(liveImplementation: ForeignTypeId);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.ApplyDiff(svc, ChangedApiDiff(CustomApiChange("Zava.Plugins.ScoreApi", "Zava.Plugins.ScoreApiV2")),
                [], null, [], verbose: false));

        Assert.Equal(
            "Custom API 'zava_Score' is now implemented by 'Other.Solution.ScoreApi', not by 'Zava.Plugins.ScoreApi' as when the plan was computed. " +
            "Vela will not detach it; re-run plan/apply against the current environment.",
            ex.Message);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public void ApplyDiff_detaches_a_custom_api_whose_implementing_type_no_longer_exists()   // A-41
    {
        // A dangling plugintypeid (the type is gone) is owned by nothing, so the detach goes ahead.
        (TestSupport.FakeOrgService svc, Guid apiId, _) = RenamedCustomApiService(liveImplementation: DeletedTypeId);

        DataverseRegistration.ApplyDiff(svc, ChangedApiDiff(CustomApiChange("Zava.Plugins.ScoreApi", "Zava.Plugins.ScoreApiV2")),
            [], null, [], verbose: false);

        Assert.Equal(2, svc.Updated.Count);
        Assert.Equal(apiId, svc.Updated[0].Id);
        Assert.Null(svc.Updated[0]["plugintypeid"]);
    }

    [Fact]
    public void ApplyDiff_skips_the_detach_of_a_custom_api_already_implemented_by_nothing()   // A-41
    {
        (TestSupport.FakeOrgService svc, Guid apiId, Guid newTypeId) = RenamedCustomApiService(liveImplementation: Guid.Empty);

        DataverseRegistration.ApplyDiff(svc, ChangedApiDiff(CustomApiChange("Zava.Plugins.ScoreApi", "Zava.Plugins.ScoreApiV2")),
            [], null, [], verbose: false);

        Entity repoint = Assert.Single(svc.Updated); // only the re-point, no detach
        Assert.Equal(apiId, repoint.Id);
        Assert.Equal(newTypeId, repoint.GetAttributeValue<EntityReference>("plugintypeid").Id);
    }

    [Fact]
    public void ApplyDiff_does_not_detach_a_custom_api_whose_plugin_type_is_unchanged()   // A-41
    {
        (TestSupport.FakeOrgService svc, Guid apiId, Guid newTypeId) = RenamedCustomApiService(liveImplementation: NewTypeId);

        DataverseRegistration.ApplyDiff(svc, ChangedApiDiff(CustomApiChange("Zava.Plugins.ScoreApiV2", "Zava.Plugins.ScoreApiV2", beforePrivate: true)),
            [], null, [], verbose: false);

        Entity update = Assert.Single(svc.Updated);
        Assert.Equal(apiId, update.Id);
        Assert.Equal(newTypeId, update.GetAttributeValue<EntityReference>("plugintypeid").Id);
    }

    [Theory]
    [InlineData("Zava.Plugins.Old", "Zava.Plugins.New", true)]
    [InlineData("Zava.Plugins.Same", "Zava.Plugins.Same", false)]
    [InlineData("Zava.Plugins.same", "Zava.Plugins.Same", true)]   // type names are case-sensitive
    [InlineData("", "Zava.Plugins.New", false)]                    // not implemented by anything yet: nothing to detach
    public void IsCustomApiImplementationChange_compares_the_plugin_type_names(string before, string after, bool expected)
        => Assert.Equal(expected, DataverseRegistration.IsCustomApiImplementationChange(CustomApiChange(before, after)));

    // The state-only update that disables a step just created, wherever it was batched.
    private static Entity? FindBatchedStateUpdate(TestSupport.FakeOrgService svc) => svc.Batches
        .SelectMany(batch => batch.Requests)
        .OfType<UpdateRequest>()
        .Select(update => update.Target)
        .FirstOrDefault(target => target.Attributes.Count == 2 && target.Contains("statecode"));

    [Fact]
    public void ApplyDiff_on_midrun_failure_writes_resume_report_and_rethrows()
    {
        Mock<IOrganizationService> mock = ServiceThatFailsAfterResolve();
        RegistrationDiff diff = DiffWithOneChangedStep();

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Exception ex = Assert.ThrowsAny<Exception>(() =>
                DataverseRegistration.ApplyDiff(mock.Object, diff, [], null, [], verbose: false));
            Assert.Contains("simulated dataverse failure", ex.Message);
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("apply failed mid-run", stderr);
        Assert.Contains($"update step {PluginType}", stderr);
        // The publish/resolve phase completed before the failure.
        Assert.Contains("publish plugin package(s) and resolve plugin types", stderr);
        Assert.Contains("re-run 'apply' (add --auto-approve in CI) to resume", stderr);
    }

    [Fact]
    public void ApplyDiff_capture_phase_abort_does_not_over_report_earlier_owners_as_completed()   // #305
    {
        const string accountPlugin = "Zava.Plugins.AccountPlugin";
        const string contactPlugin = "Zava.Plugins.ContactPlugin";

        // Two changed steps. The first (AccountPlugin/Update) captures cleanly; the second (ContactPlugin/Create)
        // throws synchronously during capture because its message cannot be resolved. Because captured writes flush
        // only after the loop, nothing landed - so the resume report must NOT claim the first step completed. Before
        // the fix, done advanced during capture and the first step was falsely reported as applied.
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q =>
            {
                switch (q.EntityName)
                {
                    case "pluginassembly":
                        return TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()));
                    case "plugintype":
                        return TestSupport.FakeOrgService.Collection(
                            new Entity("plugintype", Guid.NewGuid()) { ["typename"] = accountPlugin },
                            new Entity("plugintype", Guid.NewGuid()) { ["typename"] = contactPlugin });
                    case "sdkmessage":
                        string? name = q.Criteria.Conditions
                            .FirstOrDefault(c => c.AttributeName == "name")?.Values.FirstOrDefault() as string;
                        return name == "Update"
                            ? TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid()))
                            : new EntityCollection(); // "Create" not found -> ResolveMessageId throws during capture
                    default:
                        return new EntityCollection(); // no existing steps/images/filters
                }
            },
        };

        JsonElement accountDef = Json(new { pluginTypeName = accountPlugin, message = "Update", stage = 40, mode = 0, stepKey = "acc-x" });
        JsonElement contactDef = Json(new { pluginTypeName = contactPlugin, message = "Create", stage = 40, mode = 0, stepKey = "con-x" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [],
            Removed: [],
            Changed:
            [
                new RegistrationChange("step", accountPlugin, accountDef, accountDef),
                new RegistrationChange("step", contactPlugin, contactDef, contactDef),
            ],
            LocalItems: [],
            CurrentItems: []);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("apply failed mid-run", stderr);
        // The un-flushed first step must not be listed as completed (the bug: done advanced during capture).
        Assert.DoesNotContain($"+ update step {accountPlugin}", stderr);
        // Only the publish/resolve phase completed.
        // The report names the step whose capture threw; the earlier, un-flushed one is "not applied".
        Assert.Contains($"failed at: update step {contactPlugin} (con-x)", stderr);
        Assert.Contains($"- update step {accountPlugin} (acc-x)", stderr);
        Assert.Contains("+ publish plugin package(s) and resolve plugin types", stderr);
    }

    [Fact]
    public void ApplyDiff_batched_failure_surfaces_the_half_applied_report_even_without_verbose()   // #305
    {
        // The step row create faults inside the ExecuteMultiple batch. The per-owner half-applied report must be
        // rendered even without --verbose so the operator sees a partially-updated org; before the fix it was gated
        // on --verbose and stayed silent.
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = PluginType }),
                "sdkmessage" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessage", Guid.NewGuid())),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id),
            OnCreate = e => e.LogicalName == "sdkmessageprocessingstep"
                ? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = "step row rejected" }, "step row rejected")
                : Guid.NewGuid(),
        };

        RegistrationDiff diff = DiffWithAddedSteps("dv-1");

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("[apply]", stderr);   // the per-owner report rendered despite verbose: false
        Assert.Contains("FAILED", stderr);
    }
}
