using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class RibbonSchemaTests
{
    [Fact]
    public void Empty_Ribbon_Diff_Is_Valid()
    {
        // RibbonDiffXml's children are all optional, so an empty diff is schema-valid.
        Assert.True(RibbonSchema.IsValid("<RibbonDiffXml></RibbonDiffXml>"));
    }

    [Fact]
    public void JavaScriptFunction_Requires_FunctionName_And_Library()
    {
        // A JavaScriptFunction missing its required attributes must fail validation.
        const string xml = """
            <RibbonDiffXml>
              <CommandDefinitions>
                <CommandDefinition Id="c">
                  <EnableRules />
                  <DisplayRules />
                  <Actions><JavaScriptFunction /></Actions>
                </CommandDefinition>
              </CommandDefinitions>
            </RibbonDiffXml>
            """;

        Assert.NotEmpty(RibbonSchema.Validate(xml));
    }

    [Fact]
    public void Unknown_Element_Is_Invalid()
    {
        Assert.NotEmpty(RibbonSchema.Validate("<RibbonDiffXml><Bogus /></RibbonDiffXml>"));
    }

    [Fact]
    public void Malformed_Xml_Reports_A_Single_Error()
    {
        string error = Assert.Single(RibbonSchema.Validate("<RibbonDiffXml>"));
        Assert.Contains("Malformed XML", error);
    }
}
