using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

public class DefinitionNormalizerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Step_filtering_attributes_are_order_insensitive()
    {
        // Documents the existing guarantee that attribute order never produces a false "changed" diff.
        JsonElement a = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, filteringAttributes = new[] { "name", "telephone1", "address1_city" } }),
            Options);
        JsonElement b = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, filteringAttributes = new[] { "telephone1", "address1_city", "name" } }),
            Options);

        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    [Fact]
    public void Step_pre_and_post_images_are_alias_order_insensitive()
    {
        // Image ordering is owned by the shared shape builder (DefinitionNormalizer.BuildStepShape), used by
        // both the live read and this local path, so alias order can never produce a false "changed" diff.
        JsonElement a = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, preImages = new object[] { new { alias = "PreA", attributes = new[] { "name" } }, new { alias = "PreB", attributes = new[] { "telephone1" } } } }),
            Options);
        JsonElement b = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, preImages = new object[] { new { alias = "PreB", attributes = new[] { "telephone1" } }, new { alias = "PreA", attributes = new[] { "name" } } } }),
            Options);

        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    [Fact]
    public void Step_name_uses_stepkey_when_present()
    {
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "Zava.AccountPlugin", message = "Update", stage = 40, mode = 0, stepKey = "dv-abc123" }),
            Options);

        Assert.Equal("Zava.AccountPlugin - PostOpSync: dv-abc123", n.GetProperty("name").GetString());
    }

    [Fact]
    public void Step_name_includes_entity_when_no_stepkey()
    {
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "Zava.AccountPlugin", message = "Update", stage = 20, mode = 0, entityLogicalName = "account" }),
            Options);

        Assert.Equal("Zava.AccountPlugin - PreOpSync: Update of account", n.GetProperty("name").GetString());
    }

    [Fact]
    public void Async_step_is_normalized_to_postoperation_so_plan_apply_stay_idempotent()
    {
        // Async steps are forced to PostOperation (stage 40) at write time. The local diff shape must apply
        // the same coercion to both the stage field AND the derived name, otherwise plan reports a permanent
        // spurious change and apply re-writes the step on every run.
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "Zava.AccountPlugin", message = "Update", stage = 20, mode = 1, entityLogicalName = "account" }),
            Options);

        Assert.Equal(40, n.GetProperty("stage").GetInt32());
        Assert.Equal("Zava.AccountPlugin - PostOpAsync: Update of account", n.GetProperty("name").GetString());
    }

    [Fact]
    public void Step_rank_is_clamped_to_positive_so_plan_apply_stay_idempotent()
    {
        // Dataverse clamps rank to >= 1 at write time; the local shape must match or the step churns.
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, executionOrder = 0 }),
            Options);

        Assert.Equal(1, n.GetProperty("executionOrder").GetInt32());
    }

    [Fact]
    public void Step_is_enabled_by_default_when_flag_absent()
    {
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0 }),
            Options);

        Assert.True(n.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Step_disabled_flag_is_carried_through()
    {
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, enabled = false }),
            Options);

        Assert.False(n.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Step_carries_unsecure_and_secure_configuration()
    {
        JsonElement n = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", stage = 40, mode = 0, unsecureConfig = "u-cfg", secureConfig = "s-cfg" }),
            Options);

        Assert.Equal("u-cfg", n.GetProperty("unsecureConfig").GetString());
        // Unsecure config stays plaintext (non-sensitive by design); secure config is redacted to a stable
        // hash so the secret never reaches plan/inspect output, yet a change is still diff-detectable.
        string secure = n.GetProperty("secureConfig").GetString()!;
        Assert.NotEqual("s-cfg", secure);
        Assert.Equal(DefinitionNormalizer.HashSecret("s-cfg"), secure);
    }

    [Fact]
    public void CustomApi_binding_type_defaults_from_isBound()
    {
        JsonElement bound = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", isBound = true }), Options);
        JsonElement unbound = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", isBound = false }), Options);

        Assert.Equal(1, bound.GetProperty("bindingType").GetInt32());
        Assert.Equal(0, unbound.GetProperty("bindingType").GetInt32());
    }

    [Fact]
    public void CustomApi_parameters_are_order_insensitive()
    {
        JsonElement a = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", inputParameters = new object[] { new { name = "B" }, new { name = "A" } } }),
            Options);
        JsonElement b = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", inputParameters = new object[] { new { name = "A" }, new { name = "B" } } }),
            Options);

        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }
}
