using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>
/// Apply over a stateful fake environment (<see cref="StatefulOrgService"/>): a step on the message of a Custom API
/// created in the same run converges in one apply, and the problems apply could only hit after its deletes and the
/// package publish (a Custom API name without the publisher prefix, a step whose message or table filter does not
/// exist) stop plan and apply in the preview, before any write.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ApplyPreWriteChecksTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Assembly = "Zava.Plugins";
    private const string AccountPlugin = "Zava.Plugins.AccountPlugin";
    private const string ApiPlugin = "Zava.Plugins.MyApiPlugin";
    private const string Solution = "ZavaSales";

    private readonly string _out = Path.Combine(Path.GetTempPath(), $"vela-prewrite-{Guid.NewGuid():N}.json");
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _error = new();
    private readonly StatefulOrgService _org = NewEnvironment();

    public ApplyPreWriteChecksTests() => Console.SetError(_error);

    public void Dispose()
    {
        Console.SetError(_originalError);
        File.Delete(_out);
    }

    [Fact]
    public void A_step_on_a_custom_api_created_in_the_same_run_converges_in_one_apply()
    {
        RegistrationItem[] local = [Api("zava_MyApi"), Step("myapi-post", message: "zava_MyApi", entity: null, plugin: ApiPlugin)];

        // plan accepts it: the API's message does not exist yet, and the step on it is not checked against the org.
        AssertExit(ExitChangesPending, Run("plan", local, detailedExitCode: true));

        AssertExit(ExitSuccess, Run("apply", local));
        Assert.Equal("apply-execute", Output().GetProperty("mode").GetString());

        Entity message = Assert.Single(_org.Rows("sdkmessage"), m => m.GetAttributeValue<string>("name") == "zava_MyApi");
        Entity step = Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.Equal(message.Id, step.GetAttributeValue<EntityReference>("sdkmessageid").Id);

        AssertExit(ExitSuccess, Run("plan", local, detailedExitCode: true));
        Assert.False(Output().GetProperty("hasChanges").GetBoolean());
    }

    [Fact]
    public void Custom_apis_are_written_before_the_steps()
    {
        AssertExit(ExitSuccess, Run("apply", [Step("account-update"), Api("zava_MyApi")]));

        List<string> writes = _org.Writes;
        Assert.True(writes.IndexOf("create:customapi") < writes.IndexOf("create:sdkmessageprocessingstep"),
            string.Join(", ", writes));
        Assert.True(writes.IndexOf("create:customapirequestparameter") < writes.IndexOf("create:sdkmessageprocessingstep"),
            string.Join(", ", writes));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void An_unprefixed_custom_api_fails_the_preview_with_exit_4_before_any_write(string command)
    {
        AssertExit(ExitSuccess, Run("apply", [Step("account-update")]));
        int writes = _org.Writes.Count;

        // The step is removed (a delete) and the API lacks the prefix: nothing may be deleted or published.
        int exit = Run(command, [Api("DoThing"), Api("NoPrefix", plugin: "Zava.Plugins.OtherApi")], solution: Solution);

        Assert.Equal(ExitError, exit);
        Assert.Equal(writes, _org.Writes.Count);
        Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.False(File.Exists(_out));
        string error = _error.ToString();
        Assert.Contains("2 problems block this run; nothing was written:", error);
        Assert.Contains("Custom API 'DoThing' must start with the solution publisher prefix 'zava_'.", error);
        Assert.Contains("Custom API 'NoPrefix' must start with the solution publisher prefix 'zava_'.", error);
        Assert.DoesNotContain("Apply failed mid-run", error);
    }

    [Fact]
    public void A_prefixed_custom_api_passes_the_preview_with_a_solution()
    {
        AssertExit(ExitChangesPending, Run("plan", [Api("ZAVA_DoThing")], detailedExitCode: true, solution: Solution));
        Assert.Empty(Output().GetProperty("diagnostics").EnumerateArray());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void A_step_on_a_missing_message_fails_the_preview_with_exit_4_before_any_write(string command)
    {
        AssertExit(ExitSuccess, Run("apply", [Step("account-update")]));
        int writes = _org.Writes.Count;

        int exit = Run(command, [Step("ghost", message: "zava_Missing", entity: null)]);

        Assert.Equal(ExitError, exit);
        Assert.Equal(writes, _org.Writes.Count);
        Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.Contains("Dataverse message not found: zava_Missing.", _error.ToString());
        Assert.Contains("Nothing was written.", _error.ToString());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public void A_step_on_a_table_without_a_message_filter_fails_the_preview_with_exit_4_before_any_write(string command)
    {
        AssertExit(ExitSuccess, Run("apply", [Step("account-update")]));
        int writes = _org.Writes.Count;

        // The live step changes (a new filter) and moves to a table Update has no filter for.
        int exit = Run(command, [Step("account-update", entity: "lead")]);

        Assert.Equal(ExitError, exit);
        Assert.Equal(writes, _org.Writes.Count);
        Assert.Contains("no message filter exists for entity 'lead' on message 'Update'", _error.ToString());
    }

    [Fact]
    public void A_step_on_an_existing_custom_api_message_is_checked_against_the_environment()
    {
        // The API exists already (an earlier run): its message is in the org, so the step resolves it there.
        AssertExit(ExitSuccess, Run("apply", [Api("zava_MyApi")]));

        AssertExit(ExitSuccess, Run("apply", [Api("zava_MyApi"), Step("myapi-post", message: "zava_MyApi", entity: null, plugin: ApiPlugin)]));
        Assert.Single(_org.Rows("sdkmessageprocessingstep"));
    }

    [Fact]
    public void A_step_with_a_table_on_a_global_custom_api_registers_without_it_with_an_info_line_and_does_not_drift()
    {
        // Dataverse gives a global API one message filter, with no table (verified live): the step's table (the
        // plugin's TEntity) is dropped. The API is created in the same run, so its binding comes from the local definition.
        RegistrationItem[] local = [Api("zava_MyApi"), Step("myapi-post", message: "zava_MyApi", entity: "account", plugin: ApiPlugin)];

        AssertExit(ExitChangesPending, Run("plan", local, detailedExitCode: true));
        const string Info = "step Zava.Plugins.MyApiPlugin on zava_MyApi: zava_MyApi is a global Custom API, registered without a table (account ignored)";
        Assert.Contains(Info, Output().GetProperty("info").EnumerateArray().Select(line => line.GetString()));
        Assert.Empty(_org.Writes);

        AssertExit(ExitSuccess, Run("apply", local));
        Entity step = Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.Null(step.GetAttributeValue<EntityReference>("sdkmessagefilterid"));

        // The live step (no filter) matches the local step (account): no perpetual update.
        AssertExit(ExitSuccess, Run("plan", local, detailedExitCode: true));
        JsonElement replan = Output();
        Assert.False(replan.GetProperty("hasChanges").GetBoolean());
        Assert.Contains(Info, replan.GetProperty("info").EnumerateArray().Select(line => line.GetString()));
    }

    [Fact]
    public void A_step_with_a_table_on_a_live_global_custom_api_defined_elsewhere_registers_without_it()
    {
        // The API is deployed by another package (no plugin type of this one): the binding comes from its live row.
        _org.Create(new Entity("customapi") { ["uniquename"] = "zava_MyApi", ["bindingtype"] = new OptionSetValue(0) });
        RegistrationItem[] local = [Step("myapi-post", message: "zava_MyApi", entity: "account", plugin: AccountPlugin)];

        AssertExit(ExitSuccess, Run("apply", local));
        Entity step = Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Assert.Null(step.GetAttributeValue<EntityReference>("sdkmessagefilterid"));

        AssertExit(ExitSuccess, Run("plan", local, detailedExitCode: true));
        Assert.False(Output().GetProperty("hasChanges").GetBoolean());
    }

    [Fact]
    public void UnscopeGlobalCustomApiSteps_keeps_a_step_on_a_bound_api_or_a_platform_message_and_renames_a_keyless_step()
    {
        RegistrationItem keyless = Step(string.Empty, message: "zava_Global", entity: "account");
        RegistrationState state = State([Step("bound", message: "zava_Bound", entity: "account"), Step("update"), keyless]);
        Dictionary<string, (int, string?)> bindings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zava_Bound"] = (1, "account"),
            ["ZAVA_GLOBAL"] = (0, null),
        };

        RegistrationState unscoped = DataverseRegistration.UnscopeGlobalCustomApiSteps(state, bindings, Web);

        Assert.Equal(new[] { "account", "account", string.Empty },
            unscoped.Items.Select(item => item.Definition.GetProperty("entityLogicalName").GetString()).ToArray());
        JsonElement renamed = unscoped.Items[2].Definition;
        Assert.DoesNotContain("account", renamed.GetProperty("name").GetString());
        Assert.Equal(renamed.GetProperty("name").GetString(), renamed.GetProperty("description").GetString());
        Assert.Single(unscoped.Info!);
        Assert.Same(state, DataverseRegistration.UnscopeGlobalCustomApiSteps(state, new Dictionary<string, (int, string?)>(), Web));
    }

    [Fact]
    public void A_step_on_a_bound_custom_api_registers_on_the_bound_table_filter_and_another_table_is_blocked()
    {
        RegistrationItem api = Api("zava_MyApi", boundEntity: "account");

        int blocked = Run("plan", [api, Step("myapi-post", message: "zava_MyApi", entity: "contact", plugin: ApiPlugin)]);
        Assert.Equal(ExitError, blocked);
        Assert.Empty(_org.Writes);
        Assert.Contains("Custom API 'zava_MyApi' is bound to 'account'", _error.ToString());
        Assert.Contains("cannot be scoped to 'contact'", _error.ToString());

        AssertExit(ExitSuccess, Run("apply", [api, Step("myapi-post", message: "zava_MyApi", entity: "account", plugin: ApiPlugin)]));
        Entity step = Assert.Single(_org.Rows("sdkmessageprocessingstep"));
        Entity filter = Assert.Single(_org.Rows("sdkmessagefilter"), f => f.Id == step.GetAttributeValue<EntityReference>("sdkmessagefilterid").Id);
        Assert.Equal("account", filter.GetAttributeValue<string>("primaryobjecttypecode"));
    }

    [Fact]
    public void FindUnregistrableSteps_exempts_only_the_messages_of_custom_apis_the_run_creates()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State([Api("zava_New"), Step("on-new", message: "ZAVA_NEW", entity: null, plugin: ApiPlugin), Step("on-gone", message: "zava_Gone"), Step("global", message: "Associate", entity: null)]),
            State([]));

        IReadOnlyList<string> problems = DataverseRegistration.FindUnregistrableSteps(_org, diff);

        string problem = Assert.Single(problems);
        Assert.Contains("Dataverse message not found: zava_Gone", problem);
    }

    [Fact]
    public void BlockingErrorsMessage_lists_one_error_inline_and_several_on_their_own_lines()
    {
        Assert.Equal("A. Nothing was written.", PlanApplyCommand.BlockingErrorsMessage(["A."]));
        Assert.Equal(
            $"2 problems block this run; nothing was written:{Environment.NewLine}  - A.{Environment.NewLine}  - B.",
            PlanApplyCommand.BlockingErrorsMessage(["A.", "B."]));
    }

    private static StatefulOrgService NewEnvironment()
    {
        StatefulOrgService org = new();
        org.SeedMessage("Update", "account", "contact");
        org.SeedMessage("Associate");
        org.DefinePackage("zava_Plugins", Assembly, AccountPlugin, ApiPlugin, "Zava.Plugins.OtherApi");
        org.Create(new Entity("pluginpackage") { ["name"] = "zava_Plugins", ["version"] = "1.0.0", ["content"] = "" });
        Guid publisherId = org.Create(new Entity("publisher") { ["customizationprefix"] = "zava" });
        org.Create(new Entity("solution")
        {
            ["uniquename"] = Solution,
            ["ismanaged"] = false,
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
        org.Writes.Clear();
        return org;
    }

    private static RegistrationItem Step(string key, string message = "Update", string? entity = "account", string plugin = AccountPlugin) =>
        new("step", plugin, DefinitionNormalizer.NormalizeStepDefinition(JsonSerializer.SerializeToElement(new
        {
            pluginTypeName = plugin, message, entityLogicalName = entity, stepKey = key, stage = 40, mode = 0,
            filteringAttributes = entity == null ? Array.Empty<string>() : ["name"],
        }), Web));

    private static RegistrationItem Api(string uniqueName, string plugin = ApiPlugin, string? boundEntity = null) =>
        new("custom-api", plugin, DefinitionNormalizer.NormalizeCustomApiDefinition(JsonSerializer.SerializeToElement(new
        {
            pluginTypeName = plugin, uniqueName, isFunction = false,
            isBound = boundEntity != null, boundEntityLogicalName = boundEntity, bindingType = boundEntity != null ? 1 : 0,
            inputParameters = new object[] { new { name = "Input" } },
        }), Web));

    private static RegistrationState State(RegistrationItem[] items) =>
        new(SourceAssemblies: [Assembly], SourceAssemblyPayloads: [], PackagePayloads: [],
            GeneratedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Items: items, Diagnostics: []);

    private int Run(string command, RegistrationItem[] local, bool detailedExitCode = false, string? solution = null)
    {
        SharedOptions options = new(PackagePaths: [], PackageAssembly: null, ConnectionString: null, OAuthUrl: null, OAuthToken: null,
            SolutionUniqueName: solution, Environment: null, OutPath: _out, AutoApprove: true, Interactive: false, Strict: false,
            Verbose: false, DetailedExitCode: detailedExitCode);
        PlanApplyHost host = new()
        {
            ScanLocal = (_, _, _) => State(local),
            Connect = _ => new OrganizationConnection(_org),
            IsInputRedirected = () => true,
            IsOutputRedirected = () => true,
            ReadLine = () => throw new InvalidOperationException("apply must not prompt with --auto-approve"),
        };
        if (File.Exists(_out))
        {
            File.Delete(_out);
        }

        return PlanApplyCommand.Run(command, options, Web, host);
    }

    private void AssertExit(int expected, int actual) => Assert.True(expected == actual, $"exit {actual}, expected {expected}. stderr:\n{_error}");

    private JsonElement Output() => JsonDocument.Parse(File.ReadAllText(_out)).RootElement.Clone();
}
