using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

// CLI half of the CLI/framework contract (A-36). The framework half is Vela.Tests/PluginContractGoldenTests. Both
// read tests/contracts/plugin-contract.golden.json; here the CLI's PackageScanner scans a REAL plugin package built
// on Vela.Plugins (tests/ContractPluginFixture), so the reflection names, the camelCase keys the scanner reads and
// the normalization all have to line up with what the framework declares. Renaming StepDefinition.FilteringAttributes,
// GetStepDefinitions or the base type would leave this scan without filters, without steps or without plugins.
public sealed class PluginContractScanTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly List<string> _temps = new();
    private readonly string _nupkg = ContractFixturePackage.Build();

    private static JsonObject Golden => JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "plugin-contract.golden.json")))!.AsObject();

    [Fact]
    public void Cli_scanner_reads_exactly_the_golden_steps()
    {
        RegistrationState state = Scan();

        string[] actual = state.Items.Where(i => i.Kind == "step").Select(i => StepShape(i, state)).ToArray();

        AssertSameItems(GoldenItems("steps"), actual);
    }

    [Fact]
    public void Cli_scanner_reads_exactly_the_golden_custom_apis()
    {
        RegistrationState state = Scan();

        string[] actual = state.Items.Where(i => i.Kind == "custom-api").Select(CustomApiShape).ToArray();

        AssertSameItems(GoldenItems("customApis"), actual);
    }

    [Fact]
    public void Cli_scanner_records_the_package_and_nothing_unmanaged()
    {
        RegistrationState state = Scan();

        Assert.Equal(new[] { ContractFixturePackage.AssemblyName }, state.SourceAssemblies);
        PluginPackagePayload package = Assert.Single(state.PackagePayloads);
        Assert.Equal(ContractFixturePackage.PackageId, package.PackageName);
        Assert.Equal("1.0.0", package.Version);
        Assert.Empty(state.Diagnostics);
        Assert.Empty(state.UnmanagedPluginTypes ?? []);
        // Only the step that names a secure configuration carries one, keyed by its full step identity.
        Assert.Equal(
            new Dictionary<string, string> { ["ContractPluginFixture.AccountContractPlugin:stepkey:account-sync"] = "ContractErpKey" },
            state.SecureConfigNameByStepKey);
    }

    [Fact]
    public void Cli_step_names_follow_the_lifecycle_and_step_key()
    {
        // The deployed sdkmessageprocessingstep.name is what the live read matches on; pin its format for each shape.
        RegistrationState state = Scan();

        string[] names = state.Items.Where(i => i.Kind == "step")
            .Select(i => i.Definition.GetProperty("name").GetString()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ContractPluginFixture.AccountContractPlugin - PostOpAsync: account-sync",
                "ContractPluginFixture.AccountContractPlugin - PostOpSync: dv-54e8605bbf31",
                "ContractPluginFixture.AccountContractPlugin - PostOpSync: dv-7ac9d11b3787",
                "ContractPluginFixture.AccountContractPlugin - PostOpSync: dv-e2d046d3a07c",
                "ContractPluginFixture.AccountContractPlugin - PreOpSync: account-normalize",
                "ContractPluginFixture.AccountContractPlugin - PreValSync: account-guard-delete",
                "ContractPluginFixture.ContactContractPlugin - PostOpSync: contact-associate",
            },
            names);
    }

    private RegistrationState Scan() =>
        PackageScanner.BuildLocalStateFromPackages(ContractFixturePackage.Options([_nupkg]), Web, _temps);

    private static string[] GoldenItems(string section) =>
        Golden[section]!.AsArray().Select(n => n!.ToJsonString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // Row by row, so a failure prints the one differing row in full rather than a truncated collection.
    private static void AssertSameItems(string[] expected, IEnumerable<string> actual)
    {
        string[] sorted = actual.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < Math.Min(expected.Length, sorted.Length); i++)
        {
            Assert.Equal(expected[i], sorted[i]);
        }

        Assert.Equal(expected.Length, sorted.Length);
    }

    // Maps the CLI's normalized step (what plan diffs and apply writes) onto the golden row's field set and order.
    private static string StepShape(RegistrationItem item, RegistrationState state)
    {
        JsonElement d = item.Definition;
        state.SecureConfigNameByStepKey!.TryGetValue(DiffEngine.GetSecureConfigKey(item), out string? secureConfigName);
        return new JsonObject
        {
            ["pluginType"] = d.GetProperty("pluginTypeName").GetString(),
            ["stepKey"] = d.GetProperty("stepKey").GetString(),
            ["message"] = d.GetProperty("message").GetString(),
            ["entity"] = d.GetProperty("entityLogicalName").GetString(),
            // Apply writes the step name as the description when none is declared; the golden row holds the declared one.
            ["description"] = d.GetProperty("description").GetString() == d.GetProperty("name").GetString() ? string.Empty : d.GetProperty("description").GetString(),
            ["stage"] = d.GetProperty("stage").GetInt32(),
            ["mode"] = d.GetProperty("mode").GetInt32(),
            ["rank"] = d.GetProperty("executionOrder").GetInt32(),
            ["filteringAttributes"] = Strings(d.GetProperty("filteringAttributes")),
            ["preImages"] = Images(d.GetProperty("preImages")),
            ["postImages"] = Images(d.GetProperty("postImages")),
            ["impersonatingUserId"] = d.GetProperty("impersonatingUserId").GetString(),
            ["enabled"] = d.GetProperty("enabled").GetBoolean(),
            ["unsecureConfig"] = d.GetProperty("unsecureConfig").GetString(),
            ["secureConfigName"] = secureConfigName ?? string.Empty,
            ["supportedDeployment"] = d.GetProperty("supportedDeployment").GetInt32(),
            ["asyncAutoDelete"] = d.GetProperty("asyncAutoDelete").GetBoolean(),
        }.ToJsonString();
    }

    private static string CustomApiShape(RegistrationItem item)
    {
        JsonElement d = item.Definition;
        return new JsonObject
        {
            ["pluginType"] = d.GetProperty("pluginTypeName").GetString(),
            ["uniqueName"] = d.GetProperty("uniqueName").GetString(),
            ["isFunction"] = d.GetProperty("isFunction").GetBoolean(),
            ["bindingType"] = d.GetProperty("bindingType").GetInt32(),
            ["boundEntity"] = d.GetProperty("boundEntityLogicalName").GetString() ?? string.Empty,
            ["isPrivate"] = d.GetProperty("isPrivate").GetBoolean(),
            ["executePrivilegeName"] = d.GetProperty("executePrivilegeName").GetString() ?? string.Empty,
            ["allowedCustomProcessingStepType"] = d.GetProperty("allowedCustomProcessingStepType").GetInt32(),
            ["workflowSdkStepEnabled"] = d.GetProperty("workflowSdkStepEnabled").GetBoolean(),
            ["requestParameters"] = new JsonArray(d.GetProperty("inputParameters").EnumerateArray()
                .OrderBy(p => p.GetProperty("name").GetString(), StringComparer.Ordinal)
                .Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.GetProperty("name").GetString(),
                    ["type"] = DataverseType(p),
                    ["isOptional"] = p.GetProperty("isOptional").GetBoolean(),
                    ["logicalEntityName"] = p.GetProperty("logicalEntityName").GetString() ?? string.Empty,
                }).ToArray()),
            ["responseProperties"] = new JsonArray(d.GetProperty("outputParameters").EnumerateArray()
                .OrderBy(p => p.GetProperty("name").GetString(), StringComparer.Ordinal)
                .Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.GetProperty("name").GetString(),
                    ["type"] = DataverseType(p),
                    ["logicalEntityName"] = p.GetProperty("logicalEntityName").GetString() ?? string.Empty,
                }).ToArray()),
        }.ToJsonString();
    }

    // The Dataverse type code apply writes for the parameter (customapirequestparameter.type), as its label.
    private static string DataverseType(JsonElement parameter)
    {
        int code = DataverseRegistration.ResolveCustomApiTypeCode(
            parameter.GetProperty("parameterTypeName").GetString(), parameter.GetProperty("isCollection").GetBoolean());
        return code switch
        {
            0 => "Boolean",
            1 => "DateTime",
            2 => "Decimal",
            3 => "Entity",
            4 => "EntityCollection",
            5 => "EntityReference",
            6 => "Float",
            7 => "Integer",
            8 => "Money",
            9 => "Picklist",
            10 => "String",
            11 => "StringArray",
            12 => "Guid",
            _ => $"unknown:{code}",
        };
    }

    private static JsonArray Strings(JsonElement array) =>
        new(array.EnumerateArray().Select(x => x.GetString()).OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    private static JsonArray Images(JsonElement array) =>
        new(array.EnumerateArray()
            .OrderBy(i => i.GetProperty("alias").GetString(), StringComparer.Ordinal)
            .Select(i => (JsonNode)new JsonObject
            {
                ["alias"] = i.GetProperty("alias").GetString(),
                ["attributes"] = Strings(i.GetProperty("attributes")),
                ["messagePropertyName"] = i.GetProperty("messagePropertyName").GetString(),
            }).ToArray());

    public void Dispose()
    {
        PackageScanner.CleanupTempDirectories(_temps);
        File.Delete(_nupkg);
    }
}
