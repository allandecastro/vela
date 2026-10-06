using System;
using System.Linq;
using System.ServiceModel;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class OwnershipScopeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Vela = "Zava.Plugins.AccountPlugin";
    private const string Legacy = "Zava.Plugins.LegacyPlugin";

    private static RegistrationItem Step(string plugin, string message) =>
        new("step", plugin, DefinitionNormalizer.NormalizeStepDefinition(
            JsonSerializer.SerializeToElement(new { pluginTypeName = plugin, message, stage = 40, mode = 0, stepKey = message }, Web), Web));

    private static RegistrationItem Api(string plugin, string uniqueName) =>
        new("custom-api", uniqueName, JsonSerializer.SerializeToElement(new { pluginTypeName = plugin, uniqueName }, Web));

    private static RegistrationState State(params RegistrationItem[] items) => new([], [], [], DateTime.UtcNow, items, []);

    [Fact]
    public void Registrations_of_a_non_Vela_plugin_type_are_excluded_so_apply_never_deletes_them()
    {
        // The legacy plugin's steps and Custom API exist live but not in the local scan: without the scope they diff
        // as "removed" and apply deletes them.
        RegistrationState local = State(Step(Vela, "Update"));
        RegistrationState current = State(Step(Vela, "Update"), Step(Legacy, "Create"), Step(Legacy, "Delete"), Api(Legacy, "zava_Legacy"));

        RegistrationState scoped = OwnershipScope.ExcludeUnmanaged(current, [Legacy], out string[] unmanaged);

        Assert.Empty(DiffEngine.BuildDiff(local, scoped).Removed);
        Assert.Equal([$"{Legacy} is not a Vela plugin type; its 2 step(s) and 1 Custom API(s) are left untouched."], unmanaged);
    }

    [Fact]
    public void A_type_absent_from_the_assembly_stays_in_scope_so_a_deleted_Vela_plugin_is_cleaned_up()
    {
        RegistrationState current = State(Step("Zava.Plugins.Deleted", "Update"));

        RegistrationState scoped = OwnershipScope.ExcludeUnmanaged(current, [Legacy], out string[] unmanaged);

        Assert.Single(DiffEngine.BuildDiff(State(), scoped).Removed);
        Assert.Empty(unmanaged);
    }

    [Fact]
    public void IsUnmanagedPluginType_recognizes_a_classic_plugin_only()
    {
        Assert.True(PackageScanner.IsUnmanagedPluginType(typeof(SamplePluginFixture.LegacyPlugin)));
        Assert.False(PackageScanner.IsUnmanagedPluginType(typeof(SamplePluginFixture.SampleAccountPlugin))); // a Vela plugin
        Assert.False(PackageScanner.IsUnmanagedPluginType(typeof(SamplePluginFixture.EntityPluginBase)));    // abstract
        Assert.False(PackageScanner.IsUnmanagedPluginType(typeof(OwnershipScopeTests)));
    }

    // ---- Custom API take-over guard -------------------------------------------------------------------------

    private static TestSupport.FakeOrgService Service(Guid implementation, Func<Entity>? owner)
    {
        return new TestSupport.FakeOrgService
        {
            OnRetrieve = (entity, id, _) => entity switch
            {
                "customapi" => new Entity("customapi", id) { ["plugintypeid"] = new EntityReference("plugintype", implementation) },
                "plugintype" => owner?.Invoke() ?? throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = DataverseRegistration.ObjectDoesNotExistErrorCode }),
                _ => new Entity(entity, id),
            },
        };
    }

    [Fact]
    public void An_existing_Custom_API_implemented_by_another_assembly_is_refused()
    {
        Guid foreign = Guid.NewGuid();
        var svc = Service(foreign, () => new Entity("plugintype", foreign) { ["typename"] = "Other.Plugin", ["assemblyname"] = "Other" });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.EnsureCustomApiNotOwnedElsewhere(svc, Guid.NewGuid(), "zava_Score", new System.Collections.Generic.Dictionary<string, Guid> { [Vela] = Guid.NewGuid() }));

        Assert.Contains("Other.Plugin", ex.Message);
        Assert.Contains("'Other'", ex.Message);
    }

    [Fact]
    public void An_existing_Custom_API_of_ours_or_of_a_deleted_type_is_updated()
    {
        Guid ours = Guid.NewGuid();
        var mine = new System.Collections.Generic.Dictionary<string, Guid> { [Vela] = ours };

        DataverseRegistration.EnsureCustomApiNotOwnedElsewhere(Service(ours, owner: null), Guid.NewGuid(), "zava_Score", mine);
        DataverseRegistration.EnsureCustomApiNotOwnedElsewhere(Service(Guid.NewGuid(), owner: null), Guid.NewGuid(), "zava_Score", mine); // type gone (rename)
    }

    // A type whose interfaces fail to load (a missing dependency).
    private sealed class UnloadableType : System.Reflection.TypeDelegator
    {
        public UnloadableType() : base(typeof(OwnershipScopeTests)) { }

        public override Type[] GetInterfaces() => throw new TypeLoadException("missing dependency");
    }

    [Fact]
    public void A_type_that_cannot_be_classified_aborts_the_scan()
    {
        // Treating it as managed would diff its live registrations as removed, and apply would delete them.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PackageScanner.IsUnmanagedPluginType(new UnloadableType()));
        Assert.Contains("missing dependency", ex.Message);
    }

    [Fact]
    public void Inspect_and_apply_output_render_the_unmanaged_lines()
    {
        using var inspect = new System.IO.StringWriter();
        OutputWriter.WriteInspectSummary(State(), State(), "dev", inspect, color: false, unmanaged: ["Legacy is not a Vela plugin type"]);
        Assert.Contains("unmanaged (left untouched):", inspect.ToString());
        Assert.Contains("Legacy is not a Vela plugin type", inspect.ToString());

        using var none = new System.IO.StringWriter();
        OutputWriter.WriteUnmanaged([], none, color: false);
        Assert.Equal(string.Empty, none.ToString());
    }
}
