using System;
using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

public class DefinitionNormalizerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Step_impersonation_round_trips_between_read_and_code_and_defaults_to_empty()   // #305
    {
        // The live read shapes impersonatingUserId as a Guid string; the code side reads the same Guid string from
        // StepDefinition. Same user -> equal shape (idempotent); a different user -> "changed"; absent on both -> "".
        Guid user = Guid.Parse("44444444-4444-4444-4444-444444444444");

        JsonElement live = DefinitionNormalizer.BuildStepShape(
            "P", "Update", "account", "k", "n", 40, 0, 1,
            new string[0], new StepImage[0], new StepImage[0], true, "", "", user, Options);
        JsonElement code = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", entityLogicalName = "account", stepKey = "k", stage = 40, mode = 0, impersonatingUserId = user.ToString() }),
            Options);
        Assert.Equal(live.GetProperty("impersonatingUserId").GetString(), code.GetProperty("impersonatingUserId").GetString());
        Assert.Equal(user.ToString(), code.GetProperty("impersonatingUserId").GetString());

        JsonElement other = DefinitionNormalizer.BuildStepShape(
            "P", "Update", "account", "k", "n", 40, 0, 1,
            new string[0], new StepImage[0], new StepImage[0], true, "", "", Guid.NewGuid(), Options);
        Assert.NotEqual(JsonSerializer.Serialize(live), JsonSerializer.Serialize(other));

        JsonElement absent = DefinitionNormalizer.NormalizeStepDefinition(
            Json(new { pluginTypeName = "P", message = "Update", entityLogicalName = "account", stepKey = "k", stage = 40, mode = 0 }),
            Options);
        Assert.Equal(string.Empty, absent.GetProperty("impersonatingUserId").GetString());
    }

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
    public void Global_message_step_ignores_entityLogicalName_so_it_converges()
    {
        // Associate/Disassociate steps read back from Dataverse with an empty entityLogicalName (their
        // sdkmessagefilter carries no primary entity), so a local declaration of "account" must not produce a
        // perpetual "changed" diff. The live side and the local side must yield the same shape.
        JsonElement local = DefinitionNormalizer.BuildStepShape(
            "P", "Associate", "account", stepKey: "", name: "P - PostOpSync: Associate of account",
            stage: 40, mode: 0, executionOrder: 1,
            filteringAttributes: new string[0], preImages: new StepImage[0], postImages: new StepImage[0],
            enabled: true, unsecureConfig: "", secureConfig: "", impersonatingUserId: null, Options);
        JsonElement live = DefinitionNormalizer.BuildStepShape(
            "P", "Associate", "", stepKey: "", name: "P - PostOpSync: Associate of account",
            stage: 40, mode: 0, executionOrder: 1,
            filteringAttributes: new string[0], preImages: new StepImage[0], postImages: new StepImage[0],
            enabled: true, unsecureConfig: "", secureConfig: "", impersonatingUserId: null, Options);

        Assert.Equal(JsonSerializer.Serialize(local), JsonSerializer.Serialize(live));
    }

    [Fact]
    public void Non_global_message_step_still_diffs_on_entityLogicalName()
    {
        // Entity-specific messages (Create/Update/...) must keep entityLogicalName in the compared shape.
        JsonElement a = DefinitionNormalizer.BuildStepShape(
            "P", "Update", "account", "", "n", 40, 0, 1,
            new string[0], new StepImage[0], new StepImage[0], true, "", "", null, Options);
        JsonElement b = DefinitionNormalizer.BuildStepShape(
            "P", "Update", "", "", "n", 40, 0, 1,
            new string[0], new StepImage[0], new StepImage[0], true, "", "", null, Options);

        Assert.NotEqual(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
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

    [Fact]
    public void CustomApi_dotted_parameter_uniquename_diffs_from_desired_while_bare_matches()
    {
        // Desired/local side carries no uniqueName, so it canonicalizes to the bare parameter name.
        JsonElement desired = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", outputParameters = new object[] { new { name = "Result" } } }), Options);

        // A Custom API deployed by an older CLI minted a dotted response-property uniquename ("{api}.{name}.response")
        // that Dataverse cannot serve. The live read now carries that value, so it must diff against the desired shape.
        JsonElement liveDotted = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", outputParameters = new object[] { new { name = "Result", uniqueName = "zava_Api.Result.response" } } }),
            Options);

        // A correctly-deployed API carries the bare uniquename and must NOT phantom-diff.
        JsonElement liveBare = DefinitionNormalizer.NormalizeCustomApiDefinition(
            Json(new { uniqueName = "zava_Api", outputParameters = new object[] { new { name = "Result", uniqueName = "Result" } } }),
            Options);

        Assert.NotEqual(JsonSerializer.Serialize(desired), JsonSerializer.Serialize(liveDotted));
        Assert.Equal(JsonSerializer.Serialize(desired), JsonSerializer.Serialize(liveBare));
    }
}
