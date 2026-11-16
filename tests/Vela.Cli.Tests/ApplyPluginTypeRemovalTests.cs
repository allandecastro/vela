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
/// A package update that removes a plugin type (its class deleted or renamed, or its whole assembly renamed). Dataverse
/// refuses to delete a plugin type that still has steps, even disabled ones (verified live: 0x80040265), and
/// <see cref="StatefulOrgService"/> refuses the import the same way. Vela deletes its own steps on that type before the
/// publish; a step it does not manage stops plan and apply with exit 4 before any write.
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

    [Fact]
    public void A_renamed_assembly_moves_its_steps_and_custom_api_in_one_apply()
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main"), Api("zava_MyApi")])));
        Guid oldTypeId = TypeId(AccountPlugin);

        // The project's <AssemblyName> changed: the same package now ships another assembly with the same classes.
        _org.DefinePackage(Package, NewAssembly, AccountPlugin, OtherPlugin, ApiPlugin);
        RegistrationState renamed = Local(NewAssembly, [Step("main"), Api("zava_MyApi")]);

        AssertExit(ExitChangesPending, Run("plan", renamed, detailedExitCode: true));
        JsonElement plan = Output();
        Assert.Contains(plan.GetProperty("info").EnumerateArray(), i => i.GetString()!.Contains($"Plugin assembly '{OldAssembly}' is no longer in plugin package '{Package}'"));
        Assert.Single(plan.GetProperty("diff").GetProperty("removed").EnumerateArray());

        AssertExit(ExitSuccess, Run("apply", renamed));

        Guid newTypeId = TypeId(AccountPlugin);
        Assert.NotEqual(oldTypeId, newTypeId);
        Entity step = Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.Equal(newTypeId, step.GetAttributeValue<EntityReference>("eventhandler").Id);
        Entity api = Assert.Single(_org.Rows("customapi"));
        Assert.Equal(TypeId(ApiPlugin), api.GetAttributeValue<EntityReference>("plugintypeid").Id);
        Assert.Equal(NewAssembly, Assert.Single(_org.Rows("pluginassembly")).GetAttributeValue<string>("name"));
        // The old step went before the package update.
        Assert.True(_org.Writes.IndexOf("delete:sdkmessageprocessingstep") < _org.Writes.IndexOf("update:pluginpackage"), string.Join(", ", _org.Writes));

        AssertExit(ExitSuccess, Run("plan", renamed, detailedExitCode: true));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void A_step_vela_does_not_manage_on_a_retired_assembly_stops_the_run_with_exit_4_before_any_write(string command)
    {
        AssertExit(ExitSuccess, Run("apply", Local(OldAssembly, [Step("main")])));
        // A step registered with another tool on the same plugin type, disabled.
        Guid messageId = Assert.Single(_org.Rows("sdkmessage"), m => m.GetAttributeValue<string>("name") == "Update").Id;
        _org.Create(new Entity("sdkmessageprocessingstep")
        {
            ["eventhandler"] = new EntityReference("plugintype", TypeId(AccountPlugin)),
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
            ["name"] = "Zava.Plugins.AccountPlugin: Update of account",
            ["stage"] = new OptionSetValue(40),
            ["mode"] = new OptionSetValue(0),
            ["statecode"] = new OptionSetValue(1),
            ["statuscode"] = new OptionSetValue(2),
        });
        int writes = _org.Writes.Count;
        _org.DefinePackage(Package, NewAssembly, AccountPlugin, OtherPlugin, ApiPlugin);

        int exit = Run(command, Local(NewAssembly, [Step("main")]));

        AssertExit(ExitError, exit);
        Assert.Equal(writes, _org.Writes.Count);
        Assert.Equal(2, _org.Rows("sdkmessageprocessingstep").Count);
        string error = _error.ToString();
        Assert.Contains($"The plugin package update removes plugin type '{AccountPlugin}'", error);
        Assert.Contains("'Zava.Plugins.AccountPlugin: Update of account'", error);
        Assert.Contains("0x80040265", error);
        Assert.Contains("nothing was written", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Apply failed mid-run", error);
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

    [Fact]
    public void A_retired_step_with_a_secure_configuration_and_no_value_supplied_is_a_warning()
    {
        RegistrationItem retired = Step("main");
        System.Text.Json.Nodes.JsonObject node = System.Text.Json.Nodes.JsonNode.Parse(retired.Definition.GetRawText())!.AsObject();
        node["secureConfig"] = DefinitionNormalizer.HashSecret("s3cret");
        JsonElement withSecret = JsonSerializer.SerializeToElement(node);
        RegistrationState current = new([], [], [], DateTime.UtcNow, [], [], RetiredAssemblies: [OldAssembly],
            RetiredItems: [new RegistrationItem("step", AccountPlugin, withSecret) { LiveId = Guid.NewGuid() }]);

        string warning = Assert.Single(DataverseRegistration.RetiredSecureConfigWarnings(Local(NewAssembly, [retired]), current));
        Assert.Contains("secure configuration is deleted with it", warning);

        RegistrationState supplied = Local(NewAssembly, [retired]) with
        {
            SecureConfigByStepKey = new Dictionary<string, string> { [DiffEngine.GetSecureConfigKey(retired)] = "s3cret" },
        };
        Assert.Empty(DataverseRegistration.RetiredSecureConfigWarnings(supplied, current));
    }

    [Fact]
    public void BuildDiff_deletes_retired_steps_and_moves_a_retired_custom_api_without_pairing_them()
    {
        RegistrationItem step = Step("main");
        RegistrationItem api = Api("zava_MyApi");
        RegistrationItem retiredStep = step with { LiveId = Guid.NewGuid() };
        RegistrationState current = new([], [], [], DateTime.UtcNow, [], [], RetiredAssemblies: [OldAssembly],
            RetiredItems: [retiredStep, api, Api("zava_Gone")]);

        RegistrationDiff diff = DiffEngine.BuildDiff(Local(NewAssembly, [step, api]), current);

        Assert.Equal([step], diff.Added);
        Assert.Contains(retiredStep, diff.Removed);
        Assert.Contains(diff.Removed, i => i.Kind == "custom-api" && JsonReader.TryGetString(i.Definition, "uniqueName") == "zava_Gone");
        RegistrationChange moved = Assert.Single(diff.Changed);
        Assert.True(moved.MovesFromRetiredAssembly);
        Assert.True(DataverseRegistration.IsCustomApiImplementationChange(moved));
        Assert.Contains(OldAssembly, diff.CurrentAssemblies);
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
        new(SourceAssemblies: [assembly], SourceAssemblyPayloads: [], PackagePayloads: [new PluginPackagePayload(Package, "1.0.0", WritePackage())],
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
