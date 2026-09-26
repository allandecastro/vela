using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

public class DefinitionParserTests
{
    [Fact]
    public void ParseStepKey_NewFormat_ReturnsKeyAfterColon()
    {
        string key = DefinitionParser.ParseStepKey("Zava.MyPlugin - 40Sync: provision", "Zava.MyPlugin");
        Assert.Equal("provision", key);
    }

    [Fact]
    public void ParseStepKey_LegacyFormat_ReturnsKeyAfterPrefix()
    {
        string key = DefinitionParser.ParseStepKey("df:Zava.MyPlugin:provision", "Zava.MyPlugin");
        Assert.Equal("provision", key);
    }

    [Fact]
    public void ParseStepKey_UnrecognizedOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DefinitionParser.ParseStepKey("Some other plugin step", "Zava.MyPlugin"));
        Assert.Equal(string.Empty, DefinitionParser.ParseStepKey(null, "Zava.MyPlugin"));
        Assert.Equal(string.Empty, DefinitionParser.ParseStepKey("   ", "Zava.MyPlugin"));
    }

    [Fact]
    public void ResolveStepKey_prefers_the_description_marker_over_a_renamed_name()   // B4
    {
        // The step was renamed in Dataverse (name no longer parses), but the stable marker in the description
        // still carries the key, so identity survives.
        string description = "Anything the admin typed\n[vela:stepkey=provision]";
        Assert.Equal("provision", DefinitionParser.ResolveStepKey(description, "Renamed by an admin", "Zava.MyPlugin"));
    }

    [Fact]
    public void ResolveStepKey_falls_back_to_the_name_when_no_marker_present()   // B4 graceful migration
    {
        // A step written before the marker existed: no marker in the description, so parse the name as before.
        Assert.Equal("provision", DefinitionParser.ResolveStepKey(null, "Zava.MyPlugin - 40Sync: provision", "Zava.MyPlugin"));
        Assert.Equal("provision", DefinitionParser.ResolveStepKey("old description", "Zava.MyPlugin - 40Sync: provision", "Zava.MyPlugin"));
    }

    [Fact]
    public void ResolveStepKey_returns_empty_for_a_keyless_name_and_does_not_invent_a_key()   // #305 churn
    {
        // A keyless step's name is "{plugin} - {lifecycle}: {message} of {entity}". Without the computed keyless name
        // the parser would treat "Update of account" as a step key (churning the diff); with it, it returns "".
        string keyless = DefinitionNormalizer.BuildKeylessStepName("Zava.MyPlugin", 40, 0, "Update", "account");
        Assert.Equal("Zava.MyPlugin - PostOpSync: Update of account", keyless);
        Assert.Equal(string.Empty, DefinitionParser.ResolveStepKey(null, keyless, "Zava.MyPlugin", keyless));

        // A global (entity-less) message keyless name likewise resolves to no key.
        string keylessGlobal = DefinitionNormalizer.BuildKeylessStepName("Zava.MyPlugin", 40, 0, "Associate", "");
        Assert.Equal(string.Empty, DefinitionParser.ResolveStepKey(null, keylessGlobal, "Zava.MyPlugin", keylessGlobal));

        // A real explicit key whose name differs from the keyless name is still parsed.
        Assert.Equal("provision", DefinitionParser.ResolveStepKey(null, "Zava.MyPlugin - PostOpSync: provision", "Zava.MyPlugin", keyless));

        // The marker still wins even when a keyless name is supplied.
        string described = "Renamed\n" + DefinitionParser.StepKeyMarker("provision");
        Assert.Equal("provision", DefinitionParser.ResolveStepKey(described, keyless, "Zava.MyPlugin", keyless));
    }

    [Fact]
    public void StepKeyMarker_roundtrips_through_ExtractStepKeyFromMarker()
    {
        Assert.Equal("my-key", DefinitionParser.ExtractStepKeyFromMarker(DefinitionParser.StepKeyMarker("my-key")));
        Assert.Null(DefinitionParser.ExtractStepKeyFromMarker("no marker here"));
        Assert.Null(DefinitionParser.ExtractStepKeyFromMarker(null));
    }

    [Fact]
    public void ParseCustomApiParameters_OrdersByName_SkipsBlank_AndFallsBackToTypeName()
    {
        JsonElement definition = Json(new
        {
            inputParameters = new object[]
            {
                new { name = "Zeta", typeName = "System.String" },
                new { name = "", parameterTypeName = "System.Int32" },
                new { name = "Alpha", parameterTypeName = "System.Boolean", isOptional = true },
            },
        });

        CustomApiParameterApplyDefinition[] result =
            DefinitionParser.ParseCustomApiParameters(definition, "inputParameters", includeOptional: true);

        Assert.Equal(2, result.Length);
        Assert.Equal("Alpha", result[0].Name);          // ordered ordinally by name
        Assert.True(result[0].IsOptional);
        Assert.Equal("Zeta", result[1].Name);
        Assert.Equal("System.String", result[1].ParameterTypeName); // typeName fallback honoured
    }

    [Fact]
    public void ParseCustomApiParameters_OutputCollection_IgnoresOptionalFlag()
    {
        JsonElement definition = Json(new
        {
            outputParameters = new object[]
            {
                new { name = "Result", parameterTypeName = "System.String", isOptional = true },
            },
        });

        CustomApiParameterApplyDefinition[] result =
            DefinitionParser.ParseCustomApiParameters(definition, "outputParameters", includeOptional: false);

        CustomApiParameterApplyDefinition only = Assert.Single(result);
        Assert.False(only.IsOptional); // response properties are never optional
    }
}
