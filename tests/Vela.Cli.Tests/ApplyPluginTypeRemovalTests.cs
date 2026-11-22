using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>
/// A package update that removes a plugin type (its class deleted or renamed) or changes the package's set of
/// assemblies. Dataverse refuses to delete a plugin type that still has steps, even disabled ones (verified live:
/// 0x80040265), and <see cref="StatefulOrgService"/> refuses the import the same way. Vela deletes its own steps on that
/// type before the publish; a step it does not manage stops plan and apply with exit 4 before any write. The assemblies
/// of a package are fixed at create (verified live: a rename or a drop is refused, an addition is ignored), so a changed
/// set stops plan and apply with exit 4 before any write, whatever the steps.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ApplyPluginTypeRemovalTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Package = "zava_Plugins";
    private const string OldAssembly = "Zava.Plugins";
    private const string NewAssembly = "Zava.Core.Plugins";
    private const string AccountPlugin = "Zava.Plugins.AccountPlugin";
    private const string OtherPlugin = "Zava.Plugins.OtherPlugin";
    private const string ApiPlugin = "Zava.Plugins.MyApiPlugin";

    private readonly string _out = Path.Combine(Path.GetTempPath(), $"vela-typeremoval-{Guid.NewGuid():N}.json");
    private readonly List<string> _files = [];
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _error = new();
    private readonly StatefulOrgService _org = new();
    private int _build;

    public ApplyPluginTypeRemovalTests()
    {
        Console.SetError(_error);
        _org.SeedMessage("Update", "account");
        _org.DefinePackage(Package, OldAssembly, AccountPlugin, OtherPlugin, ApiPlugin);
    }

    public void Dispose()
    {
        Console.SetError(_originalError);
        foreach (string file in _files.Append(_out).Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void A_renamed_assembly_stops_the_run_with_exit_4_before_any_write(string command)
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main"), Api("zava_MyApi")])));
        int writes = _org.Writes.Count;

        // The project's <AssemblyName> changed: the same package now ships another assembly with the same classes.
        _org.DefinePackage(Package, NewAssembly, AccountPlugin, OtherPlugin, ApiPlugin);
        int exit = Run(command, Local(NewAssembly, [Step("main"), Api("zava_MyApi")]));

        AssertExit(ExitError, exit);
        Assert.Equal(writes, _org.Writes.Count);
        string error = _error.ToString();
        Assert.Contains($"Plugin package '{Package}' changes its set of assemblies: plugin assembly '{OldAssembly}' is renamed to '{NewAssembly}'", error);
        Assert.Contains("0x80040265", error);
        Assert.Contains($"then plugin package '{Package}'", error);
        Assert.Contains("Nothing was written", error);
        Assert.DoesNotContain("Apply failed mid-run", error);
        // The type check would repeat the same cause once per type: only the assembly-set problem is reported.
        Assert.DoesNotContain("removes plugin type", error);
        Assert.Equal(OldAssembly, Assert.Single(_org.Rows("pluginassembly")).GetAttributeValue<string>("name"));
        Assert.Single(_org.Rows("sdkmessageprocessingstep"));
    }

    [Fact]
    public void A_renamed_assembly_without_steps_still_stops_the_plan()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Api("zava_MyApi")])));
        _org.DefinePackage(Package, NewAssembly, AccountPlugin, OtherPlugin, ApiPlugin);

        AssertExit(ExitError, Run("plan", Local(NewAssembly, [Api("zava_MyApi")])));

        Assert.Contains($"plugin assembly '{OldAssembly}' is renamed to '{NewAssembly}'", _error.ToString());
    }

    [Fact]
    public void After_the_package_is_deleted_by_hand_apply_creates_it_with_the_renamed_assembly()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main"), Api("zava_MyApi")])));
        Guid apiId = Assert.Single(_org.Rows("customapi")).Id;

        // The documented route: delete the steps, clear the Custom API's plugin type, delete the package, apply again.
        foreach (Entity step in _org.Rows("sdkmessageprocessingstep"))
        {
            _org.Delete("sdkmessageprocessingstep", step.Id);
        }

        _org.Update(new Entity("customapi", apiId) { ["plugintypeid"] = null });

        _org.Delete("pluginpackage", Assert.Single(_org.Rows("pluginpackage")).Id);
        Assert.Empty(_org.Rows("pluginassembly"));
        _org.DefinePackage(Package, NewAssembly, AccountPlugin, OtherPlugin, ApiPlugin);
        RegistrationState renamed = Local(NewAssembly, [Step("main"), Api("zava_MyApi")]);

        AssertExit(ExitSuccess, Run("apply", renamed));

        Assert.Equal(NewAssembly, Assert.Single(_org.Rows("pluginassembly")).GetAttributeValue<string>("name"));
        Entity created = Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.Equal(TypeId(AccountPlugin), created.GetAttributeValue<EntityReference>("eventhandler").Id);
        // The existing Custom API row is kept and pointed at the new plugin type.
        Entity api = Assert.Single(_org.Rows("customapi"));
        Assert.Equal(apiId, api.Id);
        Assert.Equal(TypeId(ApiPlugin), api.GetAttributeValue<EntityReference>("plugintypeid").Id);
        AssertExit(ExitSuccess, Run("plan", renamed, detailedExitCode: true));
    }

    // The fake follows the platform (verified live 2026-11-16): a rename is refused atomically, with the type error while
    // a step exists and the assembly error without one; an added assembly is accepted and not registered.
    [Fact]
    public void The_fake_refuses_a_renamed_assembly_and_ignores_an_added_one_on_package_update()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main")])));
        Guid packageId = Assert.Single(_org.Rows("pluginpackage")).Id;
        Entity Content() => new("pluginpackage", packageId) { ["content"] = "UEs=" };
        _org.DefinePackage(Package, NewAssembly, AccountPlugin);

        var withStep = Assert.Throws<System.ServiceModel.FaultException<OrganizationServiceFault>>(() => _org.Update(Content()));
        Assert.Equal(unchecked((int)0x80040265), withStep.Detail.ErrorCode);
        Assert.Contains($"'{AccountPlugin}' plugintype due to 1 step(s)", withStep.Detail.Message);

        _org.Delete("sdkmessageprocessingstep", Assert.Single(_org.Rows("sdkmessageprocessingstep")).Id);
        var withoutStep = Assert.Throws<System.ServiceModel.FaultException<OrganizationServiceFault>>(() => _org.Update(Content()));
        Assert.Equal(unchecked((int)0x80040265), withoutStep.Detail.ErrorCode);
        Assert.Equal("Unable to delete plug-in assembly as it is part of plugin package", withoutStep.Detail.Message);
        Assert.Equal(OldAssembly, Assert.Single(_org.Rows("pluginassembly")).GetAttributeValue<string>("name"));
        Assert.Equal(3, _org.Rows("plugintype").Count);

        // An added assembly: the update succeeds and registers nothing.
        Guid oldAssemblyId = Assert.Single(_org.Rows("pluginassembly")).Id;
        _org.Delete("pluginassembly", oldAssemblyId);
        foreach (Entity type in _org.Rows("plugintype"))
        {
            _org.Delete("plugintype", type.Id);
        }

        _org.Update(Content());
        Assert.Empty(_org.Rows("pluginassembly"));
        Assert.Empty(_org.Rows("plugintype"));
    }

    [Fact]
    public void FindPackageAssemblySetChanges_reports_a_dropped_and_an_added_assembly()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main")])));
        Guid packageId = Assert.Single(_org.Rows("pluginpackage")).Id;
        _org.Create(new Entity("pluginassembly") { ["name"] = "Zava.Extra", ["packageid"] = new EntityReference("pluginpackage", packageId) });
        PackagePublishAction[] update = [new(Package, IsCreate: false)];

        // Zava.Extra is deployed in the package but no longer built.
        string dropped = Assert.Single(DataverseRegistration.FindPackageAssemblySetChanges(_org, Local(OldAssembly, []), update, null));
        Assert.Contains("plugin assembly 'Zava.Extra' is no longer in the local package", dropped);

        // A local plugin assembly the deployed package does not have; the deployed ones are still built (shipped as
        // dependencies), so nothing is removed.
        RegistrationState adding = Local("Zava.New", []) with
        {
            SourceAssemblyPayloads = [new AssemblyPayload(OldAssembly, "a.dll"), new AssemblyPayload("Zava.Extra", "b.dll")],
        };
        string added = Assert.Single(DataverseRegistration.FindPackageAssemblySetChanges(_org, adding, update, null));
        Assert.Contains($"plugin assembly 'Zava.New' is added to it (the environment has 'Zava.Extra', '{OldAssembly}')", added);
        Assert.Contains("does not register an assembly added to one", added);

        // Both at once, with plural wording.
        string both = Assert.Single(DataverseRegistration.FindPackageAssemblySetChanges(_org, Local("Zava.New", []) with { SourceAssemblies = ["Zava.New"] }, update, null));
        Assert.Contains($"plugin assemblies 'Zava.Extra', '{OldAssembly}' are no longer in the local package and plugin assembly 'Zava.New' is added", both);
    }

    [Fact]
    public void FindPackageAssemblySetChanges_ignores_a_created_or_unchanged_package_and_an_unknown_local_assembly()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main")])));

        Assert.Empty(DataverseRegistration.FindPackageAssemblySetChanges(_org, Local(OldAssembly, []), [new(Package, IsCreate: false)], null));
        // A package the run creates or does not republish is not compared.
        Assert.Empty(DataverseRegistration.FindPackageAssemblySetChanges(_org, Local(NewAssembly, []), [new(Package, IsCreate: true)], null));
        Assert.Empty(DataverseRegistration.FindPackageAssemblySetChanges(_org, Local(NewAssembly, []), [], null));
        // A payload that does not name its assembly (built by hand) skips the added half.
        RegistrationState unnamed = Local(OldAssembly, []) with { PackagePayloads = [new PluginPackagePayload(Package, "1.0.0", WritePackage())] };
        Assert.Empty(DataverseRegistration.FindPackageAssemblySetChanges(_org, unnamed with { SourceAssemblies = [OldAssembly] }, [new(Package, IsCreate: false)], null));
    }

    [Fact]
    public void Removing_a_plugin_class_deletes_its_steps_before_the_package_update()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main"), Step("other", OtherPlugin)])));

        // OtherPlugin is deleted from the project: the package update removes its plugin type.
        _org.DefinePackage(Package, OldAssembly, AccountPlugin, ApiPlugin);
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main")], types: [AccountPlugin, ApiPlugin])));

        Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.DoesNotContain(_org.Rows("plugintype"), t => t.GetAttributeValue<string>("typename") == OtherPlugin);
        Assert.True(_org.Writes.IndexOf("delete:sdkmessageprocessingstep") < _org.Writes.IndexOf("update:pluginpackage"), string.Join(", ", _org.Writes));
    }

    [Fact]
    public void The_pre_flight_names_a_removed_type_whose_steps_the_run_does_not_delete()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main"), Step("other", OtherPlugin)])));
        RegistrationState local = Local(OldAssembly, [Step("main")], types: [AccountPlugin, ApiPlugin]);
        RegistrationState current = DataverseRegistration.ReadDataverseState(_org, [OldAssembly], Options(), Web);
        RegistrationDiff diff = DiffEngine.BuildDiff(local, current);

        // The run deletes the step (it is in diff.Removed by its live id): nothing blocks.
        Assert.Empty(DataverseRegistration.FindPluginTypeRemovalBlockers(_org, local, current, diff, packageUpdated: true));
        // The same step, not deleted by the run, blocks the update; without a package update nothing is removed.
        RegistrationDiff withoutDelete = diff with { Removed = [] };
        string problem = Assert.Single(DataverseRegistration.FindPluginTypeRemovalBlockers(_org, local, current, withoutDelete, packageUpdated: true));
        Assert.Contains($"'{OtherPlugin}'", problem);
        Assert.Contains("1 step(s)", problem);
        Assert.Empty(DataverseRegistration.FindPluginTypeRemovalBlockers(_org, local, current, withoutDelete, packageUpdated: false));
    }

    private Guid TypeId(string typeName) =>
        Assert.Single(_org.Rows("plugintype"), t => t.GetAttributeValue<string>("typename") == typeName).Id;

    private static RegistrationItem Step(string key, string plugin = AccountPlugin) =>
        new("step", plugin, DefinitionNormalizer.NormalizeStepDefinition(JsonSerializer.SerializeToElement(new
        {
            pluginTypeName = plugin, message = "Update", entityLogicalName = "account", stepKey = key, stage = 40, mode = 0,
            filteringAttributes = new[] { "name" },
        }), Web));

    private static RegistrationItem Api(string uniqueName) =>
        new("custom-api", ApiPlugin, DefinitionNormalizer.NormalizeCustomApiDefinition(JsonSerializer.SerializeToElement(new
        {
            pluginTypeName = ApiPlugin, uniqueName, isFunction = false,
            inputParameters = new object[] { new { name = "Input" } },
        }), Web));

    // A local state as the scan builds it: one package, its assembly, its plugin types. Each call writes a new package
    // file, so every apply republishes the package.
    private RegistrationState Local(string assembly, RegistrationItem[] items, string[]? types = null) =>
        new(SourceAssemblies: [assembly], SourceAssemblyPayloads: [], PackagePayloads: [new PluginPackagePayload(Package, "1.0.0", WritePackage(), AssemblyName: assembly)],
            GeneratedAtUtc: DateTime.UtcNow, Items: items, Diagnostics: [], PluginTypeNames: types ?? [AccountPlugin, OtherPlugin, ApiPlugin]);

    private string WritePackage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Package}.{Guid.NewGuid():N}.nupkg");
        _files.Add(path);
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (StreamWriter writer = new(zip.CreateEntry($"{Package}.nuspec").Open()))
        {
            writer.Write($"<?xml version=\"1.0\"?><package><metadata><id>{Package}</id><version>1.0.0</version></metadata></package>");
        }

        using (StreamWriter writer = new(zip.CreateEntry("build.txt").Open()))
        {
            writer.Write(++_build);
        }

        return path;
    }

    private SharedOptions Options(bool detailedExitCode = false) =>
        new(PackagePaths: [], PackageAssembly: null, ConnectionString: null, OAuthUrl: null, OAuthToken: null,
            SolutionUniqueName: null, Environment: null, OutPath: _out, AutoApprove: true, Interactive: false, Strict: false,
            Verbose: false, DetailedExitCode: detailedExitCode);

    private int Run(string command, RegistrationState local, bool detailedExitCode = false)
    {
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => local,
            Connect = _ => new OrganizationConnection(_org),
            IsInputRedirected = () => true,
            IsOutputRedirected = () => true,
            ReadLine = () => throw new InvalidOperationException("apply must not prompt with --auto-approve"),
        };
        if (File.Exists(_out))
        {
            File.Delete(_out);
        }

        return PlanApplyCommand.Run(command, Options(detailedExitCode), Web, host);
    }

    private void AssertExit(int expected, int actual) => Assert.True(expected == actual, $"exit {actual}, expected {expected}. stderr:\n{_error}");

    private JsonElement Output() => JsonDocument.Parse(File.ReadAllText(_out)).RootElement.Clone();
}
