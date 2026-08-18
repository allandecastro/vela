using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
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
        Guid createdSecureConfigId = Guid.NewGuid();
        var created = new List<Entity>();
        var updated = new List<Entity>();
        SetStateRequest? setState = null;

        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
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
                        var msg = new EntityCollection();
                        msg.Entities.Add(new Entity("sdkmessage", Guid.NewGuid()));
                        return msg;
                    default:
                        return new EntityCollection(); // no existing step/images
                }
            });
        mock.Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns((Entity e) =>
            {
                created.Add(e);
                return e.LogicalName == "sdkmessageprocessingstepsecureconfig" ? createdSecureConfigId : Guid.NewGuid();
            });
        mock.Setup(s => s.Update(It.IsAny<Entity>())).Callback((Entity e) => updated.Add(e));
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", It.IsAny<Guid>(), It.IsAny<ColumnSet>()))
            .Returns((string en, Guid id, ColumnSet _) => new Entity(en, id)); // no existing secure config link
        mock.Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback((OrganizationRequest r) => { if (r is SetStateRequest sr) setState = sr; })
            .Returns(new OrganizationResponse());

        JsonElement def = Json(new { pluginTypeName = PluginType, message = "Update", stage = 40, mode = 0, stepKey = "dv-x", enabled = false, unsecureConfig = "u-cfg", secureConfig = "s-cfg" });
        var diff = new RegistrationDiff(
            LocalAssemblies: ["Zava.Plugins"],
            CurrentAssemblies: ["Zava.Plugins"],
            Added: [new RegistrationItem("step", PluginType, def)],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        DataverseRegistration.ApplyDiff(mock.Object, diff, [], [], null, [], verbose: false);

        // Unsecure config is written directly onto the step entity.
        Entity step = created.Single(e => e.LogicalName == "sdkmessageprocessingstep");
        Assert.Equal("u-cfg", step.GetAttributeValue<string>("configuration"));

        // Secure config lives on its own row, created with the value and linked back to the step.
        Entity secure = created.Single(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig");
        Assert.Equal("s-cfg", secure.GetAttributeValue<string>("secureconfig"));
        Assert.Contains(updated, e => e.LogicalName == "sdkmessageprocessingstep"
            && e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid")?.Id == createdSecureConfigId);

        // Disabled step transitions to the inactive state.
        Assert.NotNull(setState);
        Assert.Equal(1, setState!.State.Value);
        Assert.Equal(2, setState.Status.Value);
    }

    [Fact]
    public void ApplyDiff_deletes_removed_step_through_the_service_seam()
    {
        // Demonstrates the apply orchestration is fully drivable through the IOrganizationService seam
        // (no live org): a removed step is located by name and deleted.
        Guid stepId = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                string? entity = (q as QueryExpression)?.EntityName;
                if (entity == "sdkmessageprocessingstep")
                {
                    var ec = new EntityCollection();
                    ec.Entities.Add(new Entity("sdkmessageprocessingstep", stepId));
                    return ec;
                }

                return new EntityCollection(); // no plugin assemblies/types needed for a remove-only diff
            });

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

        DataverseRegistration.ApplyDiff(mock.Object, diff, [], [], null, [], verbose: false);

        mock.Verify(s => s.Delete("sdkmessageprocessingstep", stepId), Times.Once);
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
        Assert.Contains("re-run 'apply --execute' to resume", stderr);
    }
}
