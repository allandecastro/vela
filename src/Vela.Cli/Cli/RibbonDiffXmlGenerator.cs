using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Vela.Cli;

/// <summary>
/// An enable rule: a JavaScript CustomRule function plus its ordered CrmParameter values. Each parameter value must
/// be a Dataverse <c>CrmParameterValue</c> (e.g. <c>PrimaryControl</c>, <c>SelectedControl</c>), or the emitted
/// RibbonDiffXml fails XSD validation - literal-argument parameters are not modelled.
/// </summary>
public sealed record RibbonRule(string Id, string FunctionName, bool Default, IReadOnlyList<string> Parameters);

/// <summary>
/// A ribbon command: the JavaScript action function, its CrmParameters and its enable-rule bindings. Only enable
/// rules can be JavaScript <c>CustomRule</c> functions; display rules are declarative and out of scope here.
/// </summary>
public sealed record RibbonCommand(
    string Id,
    string FunctionName,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<RibbonRule> EnableRules);

/// <summary>
/// A table's ribbon customization: the command web resource library and the commands to wire. Analogous to a form
/// registration; the deploy generates the RibbonDiffXml wiring from it so the exposed globals and the deploy agree.
/// </summary>
public sealed record RibbonRegistration(string Entity, string LibraryWebResourceName, IReadOnlyList<RibbonCommand> Commands);

/// <summary>
/// Pure classic-ribbon emitter: turns a <see cref="RibbonRegistration"/> into a <c>RibbonDiffXml</c> fragment with
/// <c>JavaScriptFunction</c> command actions and <c>CustomRule</c> enable/display rules. Each action and rule
/// references its command web resource via <c>Library="$webresource:&lt;name&gt;"</c>, which declares the
/// web-resource solution dependency. Output validates against the official Ribbon XSDs (see <see cref="RibbonSchema"/>).
/// </summary>
public static class RibbonDiffXmlGenerator
{
    public static string Generate(RibbonRegistration registration)
    {
        if (registration == null)
        {
            throw new ArgumentNullException(nameof(registration));
        }

        string library = "$webresource:" + registration.LibraryWebResourceName;

        XElement commandDefinitions = new XElement("CommandDefinitions",
            registration.Commands.Select(command => CommandDefinition(command, library)));

        // Enable rules are the JavaScript CustomRule functions, defined once each (deduped by id) even if several
        // commands reference the same rule. Display/tab-display rules are declarative and left empty here.
        XElement ruleDefinitions = new XElement("RuleDefinitions",
            new XElement("TabDisplayRules"),
            new XElement("DisplayRules"),
            new XElement("EnableRules", DedupeById(registration.Commands.SelectMany(c => c.EnableRules))
                .Select(rule => EnableRuleElement(rule, library))));

        XElement root = new XElement("RibbonDiffXml", commandDefinitions, ruleDefinitions);
        return root.ToString();
    }

    private static XElement CommandDefinition(RibbonCommand command, string library) =>
        new XElement("CommandDefinition",
            new XAttribute("Id", command.Id),
            new XElement("EnableRules", command.EnableRules.Select(r => new XElement("EnableRule", new XAttribute("Id", r.Id)))),
            new XElement("DisplayRules"),
            new XElement("Actions",
                new XElement("JavaScriptFunction",
                    new XAttribute("FunctionName", command.FunctionName),
                    new XAttribute("Library", library),
                    command.Parameters.Select(CrmParameter))));

    private static XElement EnableRuleElement(RibbonRule rule, string library) =>
        new XElement("EnableRule",
            new XAttribute("Id", rule.Id),
            new XElement("CustomRule",
                new XAttribute("FunctionName", rule.FunctionName),
                new XAttribute("Library", library),
                new XAttribute("Default", rule.Default ? "true" : "false"),
                rule.Parameters.Select(CrmParameter)));

    private static XElement CrmParameter(string value) => new XElement("CrmParameter", new XAttribute("Value", value));

    private static IEnumerable<RibbonRule> DedupeById(IEnumerable<RibbonRule> rules)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RibbonRule rule in rules)
        {
            if (seen.Add(rule.Id))
            {
                yield return rule;
            }
        }
    }
}
