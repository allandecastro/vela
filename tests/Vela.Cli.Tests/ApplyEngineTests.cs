using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // SetState transition for the enabled/disabled reconcile.
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>())).Returns(new OrganizationResponse());

        RegistrationDiff diff = DiffWithAddedSteps("dv-a", "dv-b", "dv-c");

        DataverseRegistration.ApplyDiff(mock.Object, diff, [], [], null, [], verbose: false);

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
        // row, its secure-config row + link, and the disable SetState still all land.
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

        JsonElement def = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", enabled = false, unsecureConfig = "u-cfg", secureConfig = "s-cfg" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", PluginType, def)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false);

        // Unsecure config is written directly onto the step entity.
        Entity step = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.Equal("u-cfg", step.GetAttributeValue<string>("configuration"));

        // Secure config lives on its own row, created with the value and linked back to the step.
        Entity secure = svc.Created.Single(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig");
        Assert.Equal("s-cfg", secure.GetAttributeValue<string>("secureconfig"));
        Assert.Contains(svc.Updated, e => e.LogicalName == "sdkmessageprocessingstep"
            && e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")?.Id == secure.Id);

        // Disabled step transitions to the inactive state (via a batched SetState in tier 2).
        SetStateRequest? setState = FindBatchedSetState(svc);
        Assert.NotNull(setState);
        Assert.Equal(step.Id, setState!.EntityMoniker.Id);
        Assert.Equal(1, setState.State.Value);
        Assert.Equal(2, setState.Status.Value);
    }

    [Fact]
    public void ApplyDiff_deletes_removed_step_through_the_service_seam()
    {
        // Demonstrates the apply orchestration is fully drivable through the IOrganizationService seam
        // (no live org): a removed step is located by name and deleted via a batched DeleteRequest.
        Guid stepId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName == "sdkmessageprocessingstep"
                ? TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", stepId))
                : new EntityCollection(), // no plugin assemblies/types needed for a remove-only diff
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

        DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false);

        Assert.Contains(("sdkmessageprocessingstep", stepId), svc.Deleted);
    }

    private static SetStateRequest? FindBatchedSetState(TestSupport.FakeOrgService svc)
    {
        foreach (ExecuteMultipleRequest batch in svc.Batches)
        {
            foreach (OrganizationRequest child in batch.Requests)
            {
                if (child is SetStateRequest setState)
                {
                    return setState;
                }
            }
        }

        return null;
    }

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
                DataverseRegistration.ApplyDiff(mock.Object, diff, [], [], null, [], verbose: false));
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
            Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], [], null, [], verbose: false));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = captured.ToString();
        Assert.Contains("apply failed mid-run", stderr);
        // The un-flushed first step must not be listed as completed (the bug: done advanced during capture).
        Assert.DoesNotContain($"+ update step {accountPlugin}", stderr);
        // The boundary sits at the first upsert; only the publish/resolve phase completed.
        Assert.Contains($"failed at: update step {accountPlugin}", stderr);
        Assert.Contains("+ publish plugin package(s) and resolve plugin types", stderr);
    }
}
