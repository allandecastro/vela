using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Vela.Analyzers.Tests;

// Every rule the analyzers ship must match its row in docs/reference/analyzers.md (id, category, severity), be
// enabled by default, and carry the title, message and description the IDE shows. A rule whose severity or category
// drifts from the documentation, or whose text goes missing, fails here.
public sealed class DiagnosticCatalogTests
{
    private static readonly Regex Row = new Regex(@"^\|\s*\[(VELA\d{3})\]\(#vela\d{3}\)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|", RegexOptions.Multiline);

    private static string RepoFile(params string[] parts)
    {
        for (DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join("/", parts));
    }

    private static Dictionary<string, (string Category, string Severity)> DocumentedRules()
    {
        string markdown = File.ReadAllText(RepoFile("docs", "reference", "analyzers.md"));
        return Row.Matches(markdown).ToDictionary(m => m.Groups[1].Value, m => (m.Groups[2].Value, m.Groups[3].Value));
    }

    private static IReadOnlyList<DiagnosticDescriptor> ShippedRules()
    {
        List<DiagnosticDescriptor> rules = typeof(ImageProjectionGenerator).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
            .SelectMany(t => ((DiagnosticAnalyzer)Activator.CreateInstance(t)!).SupportedDiagnostics)
            .ToList();
        rules.Add(ImageProjectionGenerator.NotGeneratedRule);
        return rules;
    }

    [Fact]
    public void Every_shipped_rule_matches_its_documented_category_and_severity()
    {
        Dictionary<string, (string Category, string Severity)> documented = DocumentedRules();
        IReadOnlyList<DiagnosticDescriptor> rules = ShippedRules();

        Assert.Equal(documented.Keys.OrderBy(k => k), rules.Select(r => r.Id).Distinct().OrderBy(k => k));
        foreach (DiagnosticDescriptor rule in rules)
        {
            (string category, string severity) = documented[rule.Id];
            Assert.True(category == rule.Category, $"{rule.Id}: category {rule.Category}, documented {category}");
            Assert.True(severity == rule.DefaultSeverity.ToString(), $"{rule.Id}: severity {rule.DefaultSeverity}, documented {severity}");
        }
    }

    [Fact]
    public void Every_shipped_rule_is_enabled_by_default_and_has_a_title_message_and_description()
    {
        foreach (DiagnosticDescriptor rule in ShippedRules())
        {
            Assert.True(rule.IsEnabledByDefault, $"{rule.Id} is disabled by default");
            Assert.False(string.IsNullOrWhiteSpace(rule.Title.ToString()), $"{rule.Id} has no title");
            Assert.False(string.IsNullOrWhiteSpace(rule.MessageFormat.ToString()), $"{rule.Id} has no message");
            Assert.False(string.IsNullOrWhiteSpace(rule.Description.ToString()), $"{rule.Id} has no description");
            Assert.NotEqual(rule.Title.ToString(), rule.Description.ToString());
        }
    }

    [Fact]
    public void Every_documented_rule_has_its_own_section()
    {
        string markdown = File.ReadAllText(RepoFile("docs", "reference", "analyzers.md"));
        foreach (string id in DocumentedRules().Keys)
        {
            Assert.Contains("\n## " + id + "\n", markdown.Replace("\r\n", "\n"));
        }
    }
}
