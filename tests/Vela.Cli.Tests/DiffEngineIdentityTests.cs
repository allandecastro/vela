using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

// Covers the identity helpers used to match local vs live registrations, including the "property missing ->
// fall back to item.Type" branches that the full-definition diff tests don't reach.
public class DiffEngineIdentityTests
{
    private static RegistrationItem Step(object definition) =>
        new RegistrationItem("step", "Zava.Plugins.AccountPlugin", Json(definition));

    private static RegistrationItem Api(object definition) =>
        new RegistrationItem("custom-api", "Zava.Plugins.MyApi", Json(definition));

    [Fact]
    public void GetIdentity_uses_uniqueName_for_a_custom_api()
    {
        Assert.Equal("custom-api-id", DiffEngine.GetIdentity(Api(new { uniqueName = "custom-api-id" })));
        // uniqueName missing -> falls back to the item Type.
        Assert.Equal("Zava.Plugins.MyApi", DiffEngine.GetIdentity(Api(new { isFunction = true })));
    }

    [Fact]
    public void GetIdentity_uses_step_key_when_present()
    {
        Assert.Equal("Zava.Plugins.AccountPlugin:stepkey:dv-1",
            DiffEngine.GetIdentity(Step(new { pluginTypeName = "Zava.Plugins.AccountPlugin", stepKey = "dv-1" })));
        // pluginTypeName missing -> falls back to the item Type.
        Assert.Equal("Zava.Plugins.AccountPlugin:stepkey:dv-2",
            DiffEngine.GetIdentity(Step(new { stepKey = "dv-2" })));
    }

    [Fact]
    public void GetIdentity_falls_back_to_legacy_identity_without_a_step_key()
    {
        // No stepKey -> message/stage/mode/entity identity, with the missing-property fallbacks exercised.
        Assert.Equal("Zava.Plugins.AccountPlugin:Update:40:0:account",
            DiffEngine.GetIdentity(Step(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Update", stage = 40, mode = 0, entityLogicalName = "account" })));
        Assert.Equal("Zava.Plugins.AccountPlugin::0:0:",
            DiffEngine.GetIdentity(Step(new { something = "else" }))); // all fields missing -> Type + empties
    }

    [Fact]
    public void GetIdentityKey_prefixes_with_kind()
    {
        Assert.Equal("step:Zava.Plugins.AccountPlugin:stepkey:dv-9",
            DiffEngine.GetIdentityKey(Step(new { pluginTypeName = "Zava.Plugins.AccountPlugin", stepKey = "dv-9" })));
        Assert.Equal("custom-api:zava_Api",
            DiffEngine.GetIdentityKey(Api(new { uniqueName = "zava_Api" })));
    }

    [Fact]
    public void GetStepKey_is_empty_without_a_key_or_for_a_non_step()
    {
        Assert.Equal("Zava.Plugins.AccountPlugin:stepkey:dv-3",
            DiffEngine.GetStepKey(Step(new { pluginTypeName = "Zava.Plugins.AccountPlugin", stepKey = "dv-3" })));
        Assert.Equal(string.Empty, DiffEngine.GetStepKey(Step(new { message = "Update" }))); // no stepKey
        Assert.Equal(string.Empty, DiffEngine.GetStepKey(Api(new { uniqueName = "zava_Api", stepKey = "dv-x" }))); // not a step
    }

    [Fact]
    public void GetLegacyStepIdentity_uses_type_fallback_for_missing_fields()
    {
        Assert.Equal("Zava.Plugins.AccountPlugin:Create:20:1:contact",
            DiffEngine.GetLegacyStepIdentity(Step(new { pluginTypeName = "Zava.Plugins.AccountPlugin", message = "Create", stage = 20, mode = 1, entityLogicalName = "contact" })));
        Assert.Equal("Zava.Plugins.AccountPlugin::0:0:",
            DiffEngine.GetLegacyStepIdentity(Step(new { x = 1 }))); // everything missing
    }
}
