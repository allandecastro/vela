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
