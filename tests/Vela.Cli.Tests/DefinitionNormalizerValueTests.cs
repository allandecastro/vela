using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

// Pins the exact normalized value of every field so a mutated property-name lookup (e.g. reading
// "entityLogicalName" -> "") flips the result and fails a test. Complements the structural diff tests:
// those check which fields exist, these check what they contain.
public class DefinitionNormalizerValueTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(10, 0, "PreValSync")]
    [InlineData(20, 1, "PreOpAsync")]
    [InlineData(30, 0, "MainOpSync")]
    [InlineData(40, 1, "PostOpAsync")]
    [InlineData(99, 2, "PostOpSync")] // unknown stage + unknown mode -> the default arms
    public void Lifecycle_label_maps_each_stage_and_mode(int stage, int mode, string expected)
    {
        Assert.Equal(expected, DefinitionNormalizer.BuildStepLifecycleLabel(stage, mode));
    }

    [Fact]
    public void Step_normalization_preserves_every_field_value()
    {
        JsonElement raw = Json(new
        {
            pluginTypeName = "Acme.MyPlugin",
            message = "Update",
            entityLogicalName = "account",
            stepKey = "dv-key1",
            mode = 0,
            stage = 40,
            executionOrder = 2,
            filteringAttributes = new[] { "name", "telephone1" },
            // Deliberately out of alias order so the image sort is exercised.
            preImages = new[]
            {
                new { alias = "img-b", attributes = new[] { "name" } },
                new { alias = "img-a", attributes = new[] { "telephone1" } },
            },
            postImages = new[] { new { alias = "post", attributes = new[] { "emailaddress1" } } },
            enabled = false,
            unsecureConfig = "uns-cfg",
            secureConfig = "sec-cfg",
        });

        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(raw, Web);

        Assert.Equal("Acme.MyPlugin", n.GetProperty("pluginTypeName").GetString());
        Assert.Equal("Update", n.GetProperty("message").GetString());
        Assert.Equal("account", n.GetProperty("entityLogicalName").GetString());
        Assert.Equal("dv-key1", n.GetProperty("stepKey").GetString());
        Assert.Equal("Acme.MyPlugin - PostOpSync: dv-key1", n.GetProperty("name").GetString());
        Assert.Equal(new[] { "name", "telephone1" }, n.GetProperty("filteringAttributes").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("uns-cfg", n.GetProperty("unsecureConfig").GetString());
        // Secure config is redacted to a stable hash (never the plaintext) — see the dedicated redaction test.
        Assert.Equal(DefinitionNormalizer.HashSecret("sec-cfg"), n.GetProperty("secureConfig").GetString());
        Assert.False(n.GetProperty("enabled").GetBoolean());

        // Images sorted by alias (ordinal): img-a before img-b.
        JsonElement[] pre = n.GetProperty("preImages").EnumerateArray().ToArray();
        Assert.Equal("img-a", pre[0].GetProperty("alias").GetString());
        Assert.Equal("img-b", pre[1].GetProperty("alias").GetString());
        Assert.Equal("post", n.GetProperty("postImages")[0].GetProperty("alias").GetString());
    }

    [Fact]
    public void Step_name_without_a_stepkey_uses_message_and_entity()
    {
        JsonElement raw = Json(new { pluginTypeName = "P", message = "Create", entityLogicalName = "contact", mode = 0, stage = 40 });

        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(raw, Web);

        Assert.Equal("P - PostOpSync: Create of contact", n.GetProperty("name").GetString());
    }

    [Fact]
    public void Step_name_without_stepkey_or_entity_uses_message_only()
    {
        JsonElement raw = Json(new { pluginTypeName = "P", message = "Create", mode = 0, stage = 40 });

        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(raw, Web);

        Assert.Equal("P - PostOpSync: Create", n.GetProperty("name").GetString());
    }

    [Fact]
    public void Custom_api_normalization_preserves_every_field_value()
    {
        JsonElement raw = Json(new
        {
            pluginTypeName = "Acme.Api",
            uniqueName = "zava_DoThing",
            isFunction = true,
            isBound = true,
            boundEntityLogicalName = "account",
            executePrivilegeName = "prvDoThing",
            isPrivate = true,
            allowedCustomProcessingStepType = 2,
            bindingType = 1,
            workflowSdkStepEnabled = true,
            inputParameters = new[] { new { name = "Amount", type = 10, isOptional = false } },
            outputParameters = new[] { new { name = "Result", type = 10 } },
        });

        JsonElement n = DefinitionNormalizer.NormalizeCustomApiDefinition(raw, Web);

        Assert.Equal("Acme.Api", n.GetProperty("pluginTypeName").GetString());
        Assert.Equal("zava_DoThing", n.GetProperty("uniqueName").GetString());
        Assert.True(n.GetProperty("isFunction").GetBoolean());
        Assert.True(n.GetProperty("isBound").GetBoolean());
        Assert.Equal("account", n.GetProperty("boundEntityLogicalName").GetString());
        Assert.Equal("prvDoThing", n.GetProperty("executePrivilegeName").GetString());
        Assert.True(n.GetProperty("isPrivate").GetBoolean());
        Assert.Equal(2, n.GetProperty("allowedCustomProcessingStepType").GetInt32());
        Assert.Equal(1, n.GetProperty("bindingType").GetInt32());
        Assert.True(n.GetProperty("workflowSdkStepEnabled").GetBoolean());
        Assert.Equal(1, n.GetProperty("inputParameters").GetArrayLength());
        Assert.Equal(1, n.GetProperty("outputParameters").GetArrayLength());
    }

    [Fact]
    public void Custom_api_plugin_type_falls_back_to_the_nested_plugin_step()
    {
        // No top-level pluginTypeName -> it must be read from pluginStep.pluginTypeName (the nested fallback).
        JsonElement raw = Json(new { uniqueName = "zava_X", pluginStep = new { pluginTypeName = "Nested.Type" } });

        JsonElement n = DefinitionNormalizer.NormalizeCustomApiDefinition(raw, Web);

        Assert.Equal("Nested.Type", n.GetProperty("pluginTypeName").GetString());
        Assert.Equal("zava_X", n.GetProperty("uniqueName").GetString());
    }

    [Fact]
    public void Secure_config_is_redacted_in_the_shape_but_changes_are_still_detectable()
    {
        const string secret = "super-secret-api-key=abc123;password=hunter2";

        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, secureConfig = secret }), Web);

        // The plaintext secret must NOT appear anywhere in the serialized shape.
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(n));
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(n));

        // It is replaced by a keyed digest, stable within the run (so local and live compare equal).
        string digest = n.GetProperty("secureConfig").GetString()!;
        Assert.StartsWith("hmac-sha256:", digest);
        Assert.Equal(DefinitionNormalizer.HashSecret(secret), digest);
    }

    [Fact]
    public void Secure_config_digest_is_empty_when_absent_and_changes_with_the_value()
    {
        Assert.Equal(string.Empty, DefinitionNormalizer.HashSecret(null));
        Assert.Equal(string.Empty, DefinitionNormalizer.HashSecret(string.Empty));
        Assert.NotEqual(DefinitionNormalizer.HashSecret("a"), DefinitionNormalizer.HashSecret("b")); // change-detectable
        // Idempotent: hashing an already-hashed value is a no-op (so re-normalizing a shape is a fixed point).
        string once = DefinitionNormalizer.HashSecret("a");
        Assert.Equal(once, DefinitionNormalizer.HashSecret(once));
    }
}
