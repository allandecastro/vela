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

// A-37: plan -> apply -> plan through the real command, end to end. The local side is the scanned contract fixture (a
// real Vela.Plugins package with every step and Custom API shape), the environment is StatefulOrgService, so the
// second plan reads back exactly what apply wrote: an empty second plan proves that the written shape and the read
// shape agree field for field, rather than comparing a normalizer with itself.
public sealed class PlanApplyRoundTripTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string AccountPlugin = "ContractPluginFixture.AccountContractPlugin";

    private readonly string _nupkg = ContractFixturePackage.Build();
    private readonly string _out = Path.Combine(Path.GetTempPath(), $"vela-roundtrip-{Guid.NewGuid():N}.json");
    private readonly StatefulOrgService _org = NewEnvironment();

    [Fact]
    public void Plan_apply_plan_reports_no_changes_and_a_second_apply_writes_nothing()
    {
        Assert.Equal(ExitChangesPending, Run("plan", detailedExitCode: true));
        JsonElement firstPlan = Output();
        Assert.True(firstPlan.GetProperty("hasChanges").GetBoolean());
        AssertSummary(firstPlan, added: 9, changed: 0, removed: 0);

        Assert.Equal(ExitSuccess, Run("apply"));
        JsonElement applied = Output();
        Assert.Equal("apply-execute", applied.GetProperty("mode").GetString());
        // The summary of an apply is the diff left AFTER it re-read the environment: nothing.
        AssertSummary(applied, added: 0, changed: 0, removed: 0);
        JsonElement executed = applied.GetProperty("executed");
        Assert.Equal((9, 0, 0, true), (executed.GetProperty("created").GetInt32(), executed.GetProperty("updated").GetInt32(),
            executed.GetProperty("deleted").GetInt32(), executed.GetProperty("packageRepublished").GetBoolean()));

        Assert.Equal(ExitSuccess, Run("plan", detailedExitCode: true));
        JsonElement secondPlan = Output();
        Assert.False(secondPlan.GetProperty("hasChanges").GetBoolean());
        AssertSummary(secondPlan, added: 0, changed: 0, removed: 0);
        Assert.Empty(secondPlan.GetProperty("operations").EnumerateArray());

        int writes = _org.Writes.Count;
        Assert.Equal(ExitSuccess, Run("apply"));
        Assert.Equal(writes, _org.Writes.Count);
    }

    [Fact]
    public void Apply_writes_every_declared_field_to_the_environment()
    {
        Assert.Equal(ExitSuccess, Run("apply"));

        Entity[] steps = _org.Rows("sdkmessageprocessingstep").ToArray();
        Assert.Equal(7, steps.Length);

        Entity sync = Step(steps, "account-sync");
        Assert.Equal(40, sync.GetAttributeValue<OptionSetValue>("stage").Value);
        Assert.Equal(1, sync.GetAttributeValue<OptionSetValue>("mode").Value);
        Assert.Equal("revenue", sync.GetAttributeValue<string>("filteringattributes"));
        Assert.True(sync.GetAttributeValue<bool>("asyncautodelete"));
        Assert.StartsWith("Pushes revenue changes to the ERP.", sync.GetAttributeValue<string>("description"), StringComparison.Ordinal);
        Assert.False(Step(steps, "account-normalize").GetAttributeValue<bool>("asyncautodelete"));
        Assert.Equal(new Guid("6f1c2a52-0d3b-4f5e-9a41-2b7c8d9e0f11"), sync.GetAttributeValue<EntityReference>("impersonatinguserid").Id);
        Assert.Equal("{\"endpoint\":\"https://erp.example\"}", sync.GetAttributeValue<string>("configuration"));
        Entity secure = Assert.Single(_org.Rows("sdkmessageprocessingstepsecureconfig"));
        Assert.Equal("s3cret", secure.GetAttributeValue<string>("secureconfig"));
        Assert.Equal(secure.Id, sync.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid").Id);

        Entity normalize = Step(steps, "account-normalize");
        Assert.Equal(5, normalize.GetAttributeValue<int>("rank"));
        Assert.Equal("name,telephone1", normalize.GetAttributeValue<string>("filteringattributes"));

        Entity created = Step(steps, "dv-54e8605bbf31");
        Assert.Equal(1, created.GetAttributeValue<OptionSetValue>("statecode").Value);
        Assert.Equal(2, created.GetAttributeValue<OptionSetValue>("supporteddeployment").Value);

        // The global Associate step carries no message filter at all.
        Assert.False(Step(steps, "contact-associate").Contains("sdkmessagefilterid"));

        Entity[] images = _org.Rows("sdkmessageprocessingstepimage").ToArray();
        Assert.Equal(
            new[] { "After:1:revenue", "Before:0:name,revenue", "PostImage:1:name,telephone1", "PostImage:1:telephone1", "PreImage:0:name" },
            images.Select(i => $"{i.GetAttributeValue<string>("entityalias")}:{i.GetAttributeValue<OptionSetValue>("imagetype").Value}:{i.GetAttributeValue<string>("attributes")}")
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

        Assert.Equal(new[] { "contract_ExportContacts", "contract_ScoreAccount" },
            _org.Rows("customapi").Select(a => a.GetAttributeValue<string>("uniquename")).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(12, _org.Rows("customapirequestparameter").Count);
        Assert.Equal(5, _org.Rows("customapiresponseproperty").Count);
    }

    [Fact]
    public void A_change_made_in_the_environment_is_planned_then_reconciled()
    {
        Assert.Equal(ExitSuccess, Run("apply"));

        // Someone edits the rank and the filter of a deployed step outside Vela.
        Entity drifted = Step(_org.Rows("sdkmessageprocessingstep").ToArray(), "account-normalize");
        _org.Update(new Entity("sdkmessageprocessingstep", drifted.Id) { ["rank"] = 9, ["filteringattributes"] = "name" });

        Assert.Equal(ExitChangesPending, Run("plan", detailedExitCode: true));
        AssertSummary(Output(), added: 0, changed: 1, removed: 0);

        Assert.Equal(ExitSuccess, Run("apply"));
        Entity reconciled = Step(_org.Rows("sdkmessageprocessingstep").ToArray(), "account-normalize");
        Assert.Equal(drifted.Id, reconciled.Id);
        Assert.Equal(5, reconciled.GetAttributeValue<int>("rank"));
        Assert.Equal("name,telephone1", reconciled.GetAttributeValue<string>("filteringattributes"));

        Assert.Equal(ExitSuccess, Run("plan", detailedExitCode: true));
        AssertSummary(Output(), added: 0, changed: 0, removed: 0);
    }

    private static StatefulOrgService NewEnvironment()
    {
        StatefulOrgService org = new();
        org.SeedMessage("Create", "account", "contact");
        org.SeedMessage("Update", "account", "contact");
        org.SeedMessage("Delete", "account", "contact");
        org.SeedMessage("Associate");
        org.DefinePackage(
            ContractFixturePackage.PackageId,
            ContractFixturePackage.AssemblyName,
            AccountPlugin,
            "ContractPluginFixture.ContactContractPlugin",
            "ContractPluginFixture.ScoreAccountApi",
            "ContractPluginFixture.ExportContactsApi");
        return org;
    }

    private int Run(string command, bool detailedExitCode = false)
    {
        SharedOptions options = ContractFixturePackage.Options([_nupkg], _out, detailedExitCode) with
        {
            SecureConfigs = ["ContractErpKey=s3cret"],
        };
        PlanApplyHost host = new()
        {
            ScanLocal = PackageScanner.BuildLocalStateFromPackages,
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

    private JsonElement Output() => JsonDocument.Parse(File.ReadAllText(_out)).RootElement.Clone();

    private static void AssertSummary(JsonElement output, int added, int changed, int removed)
    {
        JsonElement summary = output.GetProperty("summary");
        Assert.Equal(
            (added, changed, removed),
            (summary.GetProperty("added").GetInt32(), summary.GetProperty("changed").GetInt32(), summary.GetProperty("removed").GetInt32()));
    }

    private static Entity Step(IEnumerable<Entity> steps, string stepKey) =>
        Assert.Single(steps, s => s.GetAttributeValue<string>("name").EndsWith(": " + stepKey, StringComparison.Ordinal));

    public void Dispose()
    {
        File.Delete(_nupkg);
        File.Delete(_out);
    }
}
