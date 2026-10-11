using System.Collections.Generic;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class RibbonDiffXmlGeneratorTests
{
    private static RibbonRegistration Sample() => new RibbonRegistration(
        Entity: "contact",
        LibraryWebResourceName: "adc_/js/contact/Ribbon/commands.js",
        Commands: new[]
        {
            new RibbonCommand(
                Id: "adc.contact.requireEmail.Command",
                FunctionName: "adc.contact.requireEmail",
                Parameters: new[] { "PrimaryControl" },
                EnableRules: new[]
                {
                    new RibbonRule("adc.contact.canSave.EnableRule", "adc.contact.canSave", Default: false, Parameters: new[] { "PrimaryControl" }),
                }),
        });

    [Fact]
    public void Generates_JavaScriptFunction_With_WebResource_Dependency_And_Parameters()
    {
        string xml = RibbonDiffXmlGenerator.Generate(Sample());

        Assert.Contains("<CommandDefinition Id=\"adc.contact.requireEmail.Command\">", xml);
        Assert.Contains("FunctionName=\"adc.contact.requireEmail\"", xml);
        // The $webresource: dependency declares the web-resource solution link.
        Assert.Contains("Library=\"$webresource:adc_/js/contact/Ribbon/commands.js\"", xml);
        Assert.Contains("<CrmParameter Value=\"PrimaryControl\" />", xml);
    }

    [Fact]
    public void Generates_CustomRule_For_Enable_Rules()
    {
        string xml = RibbonDiffXmlGenerator.Generate(Sample());

        Assert.Contains("<EnableRule Id=\"adc.contact.canSave.EnableRule\">", xml);
        Assert.Contains("<CustomRule FunctionName=\"adc.contact.canSave\"", xml);
        Assert.Contains("Default=\"false\"", xml);
    }

    [Fact]
    public void Generated_RibbonDiffXml_Is_Schema_Valid()
    {
        string xml = RibbonDiffXmlGenerator.Generate(Sample());
        Assert.True(RibbonSchema.IsValid(xml), string.Join("\n", RibbonSchema.Validate(xml)));
    }

    [Fact]
    public void Rules_Shared_Across_Commands_Are_Defined_Once()
    {
        RibbonRule shared = new RibbonRule("adc.shared.EnableRule", "adc.shared", false, new[] { "PrimaryControl" });
        RibbonRegistration registration = new RibbonRegistration("account", "adc_/js/account/Ribbon/commands.js", new[]
        {
            new RibbonCommand("adc.a.Command", "adc.a", new[] { "PrimaryControl" }, new[] { shared }),
            new RibbonCommand("adc.b.Command", "adc.b", new[] { "PrimaryControl" }, new[] { shared }),
        });

        string xml = RibbonDiffXmlGenerator.Generate(registration);

        // Referenced twice (a self-closing <EnableRule Id=.. /> per command) but its CustomRule is defined once.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "<EnableRule Id=\"adc.shared.EnableRule\" />").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, "<CustomRule FunctionName=\"adc.shared\""));
        Assert.True(RibbonSchema.IsValid(xml), string.Join("\n", RibbonSchema.Validate(xml)));
    }

    [Fact]
    public void Enable_Rule_Default_True_Is_Emitted()
    {
        RibbonRegistration registration = new RibbonRegistration("account", "adc_/js/account/Ribbon/commands.js", new[]
        {
            new RibbonCommand(
                Id: "adc.account.show.Command",
                FunctionName: "adc.account.show",
                Parameters: new[] { "PrimaryControl" },
                EnableRules: new[]
                {
                    new RibbonRule("adc.account.enabled.EnableRule", "adc.account.enabled", Default: true, Parameters: new[] { "PrimaryControl" }),
                }),
        });

        string xml = RibbonDiffXmlGenerator.Generate(registration);

        Assert.Contains("<CustomRule FunctionName=\"adc.account.enabled\"", xml);
        Assert.Contains("Default=\"true\"", xml);
        Assert.True(RibbonSchema.IsValid(xml), string.Join("\n", RibbonSchema.Validate(xml)));
    }

    [Fact]
    public void Generate_Null_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() => RibbonDiffXmlGenerator.Generate(null!));
    }
}
