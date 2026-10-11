using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ContractPluginFixture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Steps;
using Xunit;

namespace Vela.Tests
{
    // Framework half of the CLI/framework contract (A-36). The CLI half is Vela.Cli.Tests/PluginContractScanTests,
    // which scans the same fixture through the CLI's PackageScanner. Both compare against one checked-in file,
    // tests/contracts/plugin-contract.golden.json, so the two sides cannot drift apart without a red test: the
    // framework must declare exactly what the golden file says, and the CLI must read exactly that.
    public sealed class PluginContractGoldenTests
    {
        private static readonly JObject Golden = JObject.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "plugin-contract.golden.json")));

        private static readonly System.Reflection.Assembly Fixture = typeof(AccountContractPlugin).Assembly;

        [Fact]
        public void Framework_scanner_declares_exactly_the_golden_steps()
        {
            FrameworkManifest manifest = FrameworkManifestScanner.Scan(Fixture, new FrameworkManifestScanOptions { Strict = true });

            Assert.Empty(manifest.Diagnostics);
            AssertSameItems(GoldenItems("steps"), manifest.PluginSteps.Select(StepShape));
        }

        [Fact]
        public void Framework_scanner_declares_exactly_the_golden_custom_apis()
        {
            FrameworkManifest manifest = FrameworkManifestScanner.Scan(Fixture, new FrameworkManifestScanOptions { Strict = true });

            AssertSameItems(GoldenItems("customApis"), manifest.CustomApis.Select(CustomApiShape));
        }

        [Fact]
        public void Each_plugin_type_returns_the_same_steps_as_the_manifest()
        {
            // The scanner and a direct GetStepDefinitions() call are the two ways a definition is read; they must agree.
            string[] direct = new AccountContractPlugin(string.Empty, string.Empty).GetStepDefinitions()
                .Concat(new ContactContractPlugin(string.Empty, string.Empty).GetStepDefinitions())
                .Select(StepShape)
                .ToArray();

            AssertSameItems(GoldenItems("steps"), direct);
        }

        [Fact]
        public void Golden_file_lists_each_step_and_custom_api_once()
        {
            // A duplicated golden row would let a missing scanned item hide behind its twin.
            string[] stepIds = Golden["steps"]!.Select(s => (string)s["pluginType"]! + ":" + (string)s["stepKey"]!).ToArray();
            string[] apiNames = Golden["customApis"]!.Select(a => (string)a["uniqueName"]!).ToArray();

            Assert.Equal(stepIds.Length, stepIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(apiNames.Length, apiNames.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(7, stepIds.Length);
            Assert.Equal(2, apiNames.Length);
        }

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

        private static string[] GoldenItems(string section) =>
            Golden[section]!.Select(x => x.ToString(Formatting.None)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // The same field set and order as the golden rows. Attribute lists are sorted and images ordered by alias:
        // Dataverse stores both as sets, so their declaration order is not part of the contract.
        private static string StepShape(StepDefinition d) => new JObject
        {
            ["pluginType"] = d.PluginTypeName,
            ["stepKey"] = d.StepKey,
            ["message"] = d.Message,
            ["entity"] = d.EntityLogicalName,
            ["description"] = d.Description,
            ["stage"] = (int)d.Stage,
            ["mode"] = (int)d.Mode,
            ["rank"] = d.ExecutionOrder,
            ["filteringAttributes"] = Sorted(d.FilteringAttributes),
            ["preImages"] = Images(d.PreImages, d.Message),
            ["postImages"] = Images(d.PostImages, d.Message),
            ["impersonatingUserId"] = d.ImpersonatingUserId?.ToString() ?? string.Empty,
            ["enabled"] = d.Enabled,
            ["unsecureConfig"] = d.UnsecureConfig,
            ["secureConfigName"] = d.SecureConfigName,
            ["supportedDeployment"] = (int)d.SupportedDeployment,
            ["asyncAutoDelete"] = d.AsyncAutoDelete,
        }.ToString(Formatting.None);

        private static string CustomApiShape(CustomApiDefinition d) => new JObject
        {
            ["pluginType"] = d.PluginTypeName,
            ["uniqueName"] = d.UniqueName,
            ["isFunction"] = d.IsFunction,
            ["bindingType"] = (int)d.BindingType,
            ["boundEntity"] = d.BoundEntityLogicalName ?? string.Empty,
            ["isPrivate"] = d.IsPrivate,
            ["executePrivilegeName"] = d.ExecutePrivilegeName ?? string.Empty,
            ["allowedCustomProcessingStepType"] = (int)d.AllowedCustomProcessingStepType,
            ["workflowSdkStepEnabled"] = d.WorkflowSdkStepEnabled,
            ["requestParameters"] = new JArray(d.InputParameters.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JObject
            {
                ["name"] = p.Name,
                ["type"] = DataverseType(p.ParameterTypeName, p.IsCollection),
                ["isOptional"] = p.IsOptional,
                ["logicalEntityName"] = p.LogicalEntityName ?? string.Empty,
            })),
            ["responseProperties"] = new JArray(d.OutputParameters.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JObject
            {
                ["name"] = p.Name,
                ["type"] = DataverseType(p.ParameterTypeName, p.IsCollection),
                ["logicalEntityName"] = p.LogicalEntityName ?? string.Empty,
            })),
        }.ToString(Formatting.None);

        // The Dataverse Custom API type (customapirequestparameter.type label) a declared CLR type deploys as.
        private static string DataverseType(string typeName, bool isCollection) => (typeName, isCollection) switch
        {
            ("System.Boolean", false) => "Boolean",
            ("System.DateTime", false) => "DateTime",
            ("System.Decimal", false) => "Decimal",
            ("Microsoft.Xrm.Sdk.Entity", false) => "Entity",
            ("Microsoft.Xrm.Sdk.EntityCollection", false) => "EntityCollection",
            ("Microsoft.Xrm.Sdk.EntityReference", false) => "EntityReference",
            ("System.Double", false) => "Float",
            ("System.Int32", false) => "Integer",
            ("Microsoft.Xrm.Sdk.Money", false) => "Money",
            ("Microsoft.Xrm.Sdk.OptionSetValue", false) => "Picklist",
            ("System.String", false) => "String",
            ("System.String", true) => "StringArray",
            ("System.Guid", false) => "Guid",
            _ => $"unmapped:{typeName}{(isCollection ? "[]" : string.Empty)}",
        };

        private static JArray Sorted(IEnumerable<string> values) => new JArray(values.OrderBy(v => v, StringComparer.Ordinal));

        // The request property each image is registered on: the declared one, else the message's default (the shared
        // StepImageRules table the CLI deploys from).
        private static JArray Images(IEnumerable<ImageDefinition> images, string message) => new JArray(
            images.OrderBy(i => i.Alias, StringComparer.Ordinal).Select(i => new JObject
            {
                ["alias"] = i.Alias,
                ["attributes"] = Sorted(i.Attributes),
                ["messagePropertyName"] = StepImageRules.ResolveMessagePropertyName(message, i.MessagePropertyName) ?? string.Empty,
            }));
    }
}
