using System;
using System.Collections.Generic;
using Vela.Cli;
using Microsoft.Xrm.Sdk;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

/// <summary>
/// Characterization tests for the pure-logic helpers of <see cref="DataverseRegistration"/>
/// (no <see cref="IOrganizationService"/> involved). These pin the exact outputs of the stage/rank
/// normalization, step naming, image identity, package prefixing, and the Custom API type-code mapping.
/// </summary>
public class DataverseRegistrationHelperTests
{
    // --- NormalizeRegistrationStage(int) -------------------------------------------------------

    [Theory]
    [InlineData(10, 10)] // PreValidation kept
    [InlineData(20, 20)] // PreOperation kept
    [InlineData(30, 30)] // MainOperation kept
    [InlineData(40, 40)] // PostOperation kept
    public void NormalizeRegistrationStage_keeps_recognized_stages(int stage, int expected)
    {
        Assert.Equal(expected, DataverseRegistration.NormalizeRegistrationStage(stage));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(50)]
    [InlineData(-1)]
    public void NormalizeRegistrationStage_unknown_defaults_to_post_operation(int stage)
    {
        Assert.Equal(40, DataverseRegistration.NormalizeRegistrationStage(stage));
    }

    // --- NormalizeRegistrationStage(int, int) --------------------------------------------------

    [Fact]
    public void NormalizeRegistrationStage_sync_keeps_the_normalized_stage()
    {
        // Sync (mode 0) leaves a valid stage untouched.
        Assert.Equal(20, DataverseRegistration.NormalizeRegistrationStage(20, mode: 0));
    }

    [Fact]
    public void NormalizeRegistrationStage_async_is_coerced_to_post_operation()
    {
        // Async (mode 1) must run post-commit: PreOperation is forced to PostOperation.
        Assert.Equal(40, DataverseRegistration.NormalizeRegistrationStage(20, mode: 1));
    }

    [Fact]
    public void NormalizeRegistrationStage_async_already_post_operation_is_unchanged()
    {
        Assert.Equal(40, DataverseRegistration.NormalizeRegistrationStage(40, mode: 1));
    }

    [Fact]
    public void NormalizeRegistrationStage_async_unknown_stage_collapses_to_post_operation()
    {
        // Unknown stage first defaults to PostOperation, then the async rule is a no-op.
        Assert.Equal(40, DataverseRegistration.NormalizeRegistrationStage(99, mode: 1));
    }

    // --- NormalizeRegistrationRank -------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void NormalizeRegistrationRank_clamps_non_positive_to_one(int input, int expected)
    {
        Assert.Equal(expected, DataverseRegistration.NormalizeRegistrationRank(input));
    }

    // --- BuildStepName -------------------------------------------------------------------------

    private static StepApplyDefinition Definition(
        string pluginTypeName = "Zava.Plugins.AccountPlugin",
        string message = "Update",
        string entityLogicalName = "account",
        string stepKey = "",
        int stage = 40,
        int mode = 0)
    {
        return new StepApplyDefinition(
            PluginTypeName: pluginTypeName,
            Message: message,
            EntityLogicalName: entityLogicalName,
            StepKey: stepKey,
            HandlerMethodName: string.Empty,
            Stage: stage,
            Mode: mode,
            ExecutionOrder: 1,
            FilteringAttributes: [],
            PreImages: [],
            PostImages: [],
            Enabled: true,
            UnsecureConfig: string.Empty,
            SecureConfig: string.Empty);
    }

    [Fact]
    public void BuildStepName_with_step_key_uses_the_key_and_lifecycle_label()
    {
        string name = DataverseRegistration.BuildStepName(Definition(stepKey: "my-key", stage: 40, mode: 0));
        Assert.Equal("Zava.Plugins.AccountPlugin - PostOpSync: my-key", name);
    }

    [Fact]
    public void BuildStepName_without_step_key_uses_message_and_entity()
    {
        string name = DataverseRegistration.BuildStepName(Definition(stepKey: "", message: "Update", entityLogicalName: "account", stage: 20, mode: 0));
        Assert.Equal("Zava.Plugins.AccountPlugin - PreOpSync: Update of account", name);
    }

    [Fact]
    public void BuildStepName_without_step_key_or_entity_omits_the_of_clause()
    {
        string name = DataverseRegistration.BuildStepName(Definition(stepKey: "", message: "Update", entityLogicalName: "", stage: 10, mode: 1));
        // Stage 10 (PreVal) + async mode → "PreValAsync"
        Assert.Equal("Zava.Plugins.AccountPlugin - PreValAsync: Update", name);
    }

    // --- BuildImageIdentityKey -----------------------------------------------------------------

    [Fact]
    public void BuildImageIdentityKey_composes_type_and_alias()
    {
        Assert.Equal("0:preimg", DataverseRegistration.BuildImageIdentityKey("preimg", 0));
        Assert.Equal("1:postimg", DataverseRegistration.BuildImageIdentityKey("postimg", 1));
        Assert.Equal("-1:", DataverseRegistration.BuildImageIdentityKey("", -1));
    }

    // --- NormalizePackageNameWithPrefix --------------------------------------------------------

    [Fact]
    public void NormalizePackageNameWithPrefix_null_prefix_returns_name_unchanged()
    {
        Assert.Equal("MyPackage", DataverseRegistration.NormalizePackageNameWithPrefix("MyPackage", null));
    }

    [Fact]
    public void NormalizePackageNameWithPrefix_whitespace_prefix_returns_name_unchanged()
    {
        Assert.Equal("MyPackage", DataverseRegistration.NormalizePackageNameWithPrefix("MyPackage", "   "));
    }

    [Fact]
    public void NormalizePackageNameWithPrefix_prepends_prefix_when_absent()
    {
        Assert.Equal("zava_MyPackage", DataverseRegistration.NormalizePackageNameWithPrefix("MyPackage", "zava"));
    }

    [Fact]
    public void NormalizePackageNameWithPrefix_does_not_double_prefix()
    {
        // Already prefixed (case-insensitively) → left as-is.
        Assert.Equal("zava_MyPackage", DataverseRegistration.NormalizePackageNameWithPrefix("zava_MyPackage", "zava"));
        Assert.Equal("ZAVA_MyPackage", DataverseRegistration.NormalizePackageNameWithPrefix("ZAVA_MyPackage", "zava"));
    }

    // --- IsPackageSigned / ReadPackageBytesForImport (signed-package guard, E8) ----------------

    [Fact]
    public void IsPackageSigned_false_for_unsigned_package()
    {
        string path = WriteTempNupkg("MyPackage", signed: false);
        try
        {
            Assert.False(DataverseRegistration.IsPackageSigned(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void IsPackageSigned_true_when_signature_entry_present()
    {
        string path = WriteTempNupkg("MyPackage", signed: true);
        try
        {
            Assert.True(DataverseRegistration.IsPackageSigned(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ReadPackageBytesForImport_matching_name_returns_bytes_even_if_signed()
    {
        // No rename needed → the signed package is passed through untouched (signature stays valid).
        string path = WriteTempNupkg("zava_MyPackage", signed: true);
        var temps = new List<string>();
        try
        {
            byte[] bytes = DataverseRegistration.ReadPackageBytesForImport(path, "zava_MyPackage", "zava_MyPackage", temps);
            Assert.Equal(System.IO.File.ReadAllBytes(path), bytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ReadPackageBytesForImport_signed_package_needing_rename_throws_actionable_error()
    {
        // Applying with --solution would rename (add the publisher prefix) and repack, invalidating the signature.
        string path = WriteTempNupkg("MyPackage", signed: true);
        var temps = new List<string>();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DataverseRegistration.ReadPackageBytesForImport(path, "MyPackage", "zava_MyPackage", temps));
            Assert.Contains("signed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("zava_MyPackage", ex.Message);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static string WriteTempNupkg(string id, bool signed)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vela-test-{Guid.NewGuid():N}.nupkg");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var entry = new System.IO.StreamWriter(zip.CreateEntry($"{id}.nuspec").Open()))
            {
                entry.Write($"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{id}</id><version>1.0.0</version></metadata></package>");
            }

            if (signed)
            {
                using var sig = new System.IO.StreamWriter(zip.CreateEntry(".signature.p7s").Open());
                sig.Write("dummy-signature");
            }
        }

        return path;
    }

    // --- ResolveCustomApiTypeCode --------------------------------------------------------------

    [Fact]
    public void ResolveCustomApiTypeCode_collection_string_is_string_array()
    {
        Assert.Equal(11, DataverseRegistration.ResolveCustomApiTypeCode("System.String", isCollection: true));
    }

    [Fact]
    public void ResolveCustomApiTypeCode_collection_entity_is_entity_collection()
    {
        Assert.Equal(4, DataverseRegistration.ResolveCustomApiTypeCode("Microsoft.Xrm.Sdk.Entity", isCollection: true));
    }

    [Theory]
    [InlineData("System.Boolean", 0)]
    [InlineData("System.DateTime", 1)]
    [InlineData("System.Decimal", 2)]
    [InlineData("Microsoft.Xrm.Sdk.EntityReference", 5)] // EntityReference before Entity
    [InlineData("Microsoft.Xrm.Sdk.EntityCollection", 4)]
    [InlineData("Microsoft.Xrm.Sdk.Entity", 3)]
    [InlineData("System.Double", 6)]
    [InlineData("System.Single", 6)]
    [InlineData("System.Int32", 7)]
    [InlineData("System.Int64", 7)]
    [InlineData("System.Int16", 7)]
    [InlineData("Microsoft.Xrm.Sdk.Money", 8)]
    [InlineData("Microsoft.Xrm.Sdk.OptionSetValue", 9)] // OptionSet
    [InlineData("Picklist", 9)]
    [InlineData("System.Guid", 12)]
    [InlineData("System.String", 10)]
    [InlineData("SomethingUnknown", 10)] // default → String
    public void ResolveCustomApiTypeCode_scalar_mapping(string typeName, int expected)
    {
        Assert.Equal(expected, DataverseRegistration.ResolveCustomApiTypeCode(typeName, isCollection: false));
    }

    [Fact]
    public void ResolveCustomApiTypeCode_null_defaults_to_string()
    {
        Assert.Equal(10, DataverseRegistration.ResolveCustomApiTypeCode(null, isCollection: false));
    }

    // --- ResolveCustomApiTypeName --------------------------------------------------------------

    [Theory]
    [InlineData(0, "System.Boolean", false)]
    [InlineData(1, "System.DateTime", false)]
    [InlineData(2, "System.Decimal", false)]
    [InlineData(3, "Microsoft.Xrm.Sdk.Entity", false)]
    [InlineData(4, "Microsoft.Xrm.Sdk.Entity", true)]
    [InlineData(5, "Microsoft.Xrm.Sdk.EntityReference", false)]
    [InlineData(6, "System.Double", false)]
    [InlineData(7, "System.Int32", false)]
    [InlineData(8, "Microsoft.Xrm.Sdk.Money", false)]
    [InlineData(9, "Microsoft.Xrm.Sdk.OptionSetValue", false)]
    [InlineData(11, "System.String", true)]
    [InlineData(12, "System.Guid", false)]
    [InlineData(10, "System.String", false)]
    [InlineData(999, "System.String", false)] // unknown → String
    public void ResolveCustomApiTypeName_maps_type_codes(int typeCode, string expectedName, bool expectedCollection)
    {
        (string typeName, bool isCollection) = DataverseRegistration.ResolveCustomApiTypeName(typeCode, null);
        Assert.Equal(expectedName, typeName);
        Assert.Equal(expectedCollection, isCollection);
    }

    [Fact]
    public void ResolveCustomApiType_round_trips_every_scalar_code_back_to_itself()
    {
        // For each scalar code, resolving to a type name and back must yield the same code.
        foreach (int code in new[] { 0, 1, 2, 5, 6, 7, 8, 9, 12, 10 })
        {
            (string typeName, bool isCollection) = DataverseRegistration.ResolveCustomApiTypeName(code, null);
            Assert.Equal(code, DataverseRegistration.ResolveCustomApiTypeCode(typeName, isCollection));
        }
    }

    // --- GetRequiredPluginTypeNames / AddRequiredPluginTypeName --------------------------------

    [Fact]
    public void GetRequiredPluginTypeNames_collects_from_added_and_changed()
    {
        var added = Step(new { pluginTypeName = "Zava.Plugins.A" });
        var changed = new RegistrationChange(
            "step",
            "Zava.Plugins.B",
            Json(new { pluginTypeName = "Zava.Plugins.B-before" }),
            Json(new { pluginTypeName = "Zava.Plugins.B" }));

        var diff = new RegistrationDiff(
            LocalAssemblies: [],
            CurrentAssemblies: [],
            Added: [added],
            Removed: [],
            Changed: [changed],
            LocalItems: [],
            CurrentItems: []);

        HashSet<string> required = DataverseRegistration.GetRequiredPluginTypeNames(diff);

        // Uses the After of a change, not the Before.
        Assert.Equal(new[] { "Zava.Plugins.A", "Zava.Plugins.B" }, Sorted(required));
    }

    [Fact]
    public void GetRequiredPluginTypeNames_is_case_insensitive_and_dedupes()
    {
        var a = Step(new { pluginTypeName = "Zava.Plugins.A" });
        var aDup = Step(new { pluginTypeName = "zava.plugins.a" });

        var diff = new RegistrationDiff(
            LocalAssemblies: [],
            CurrentAssemblies: [],
            Added: [a, aDup],
            Removed: [],
            Changed: [],
            LocalItems: [],
            CurrentItems: []);

        Assert.Single(DataverseRegistration.GetRequiredPluginTypeNames(diff));
    }

    [Fact]
    public void AddRequiredPluginTypeName_ignores_missing_or_blank_names()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DataverseRegistration.AddRequiredPluginTypeName(set, Json(new { other = "x" }));
        DataverseRegistration.AddRequiredPluginTypeName(set, Json(new { pluginTypeName = "   " }));
        Assert.Empty(set);

        DataverseRegistration.AddRequiredPluginTypeName(set, Json(new { pluginTypeName = "Zava.Plugins.X" }));
        Assert.Equal(new[] { "Zava.Plugins.X" }, Sorted(set));
    }

    private static string[] Sorted(IEnumerable<string> values)
    {
        var list = new List<string>(values);
        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }

    // --- GetAliasedString ----------------------------------------------------------------------

    [Fact]
    public void GetAliasedString_returns_the_underlying_string_value()
    {
        var row = new Entity("sdkmessageprocessingstep");
        row["msg.name"] = new AliasedValue("sdkmessage", "name", "Update");

        Assert.Equal("Update", DataverseRegistration.GetAliasedString(row, "msg.name"));
    }

    [Fact]
    public void GetAliasedString_returns_null_when_alias_absent()
    {
        var row = new Entity("sdkmessageprocessingstep");
        Assert.Null(DataverseRegistration.GetAliasedString(row, "msg.name"));
    }

    [Fact]
    public void GetAliasedString_returns_null_when_aliased_value_is_not_a_string()
    {
        var row = new Entity("sdkmessageprocessingstep");
        row["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", 42);

        Assert.Null(DataverseRegistration.GetAliasedString(row, "filter.primaryobjecttypecode"));
    }
}
