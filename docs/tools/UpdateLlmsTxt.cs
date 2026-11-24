// Regenerates the page list of docs/llms.txt from the docs tables of contents, so the index cannot drift from the
// site navigation. Run from the repository root (a .NET 10 file-based app, nothing to install):
//
//   dotnet run docs/tools/UpdateLlmsTxt.cs
//
// What it keeps and what it writes:
// - The hand-written header (everything before the first "## " heading) and the hand-written "## Optional" section
//   (from that heading to the end) are kept as they are.
// - Between them, it writes one "## " section per entry of docs/toc.yml, in toc order, with one link per page of that
//   section's toc.yml. A section whose toc groups its pages ("items:", as Guides does) gets one "## " section per
//   group instead, headed "<section>: <group>" so that two sections' groups never share a heading. Landing pages (topicHref) are not listed, also when the section's toc.yml lists its landing page
//   as a page before its groups (as the AI agents section, from vela-agentic, does).
// - A link's title is the page's toc name. Its description is the page's `_description` front matter (which DocFX
//   also uses as the page's meta description, in place of the site-wide one in docfx.json); a page without one falls
//   back to the first sentence of its first paragraph. Keep a description on one line, in double quotes.
//
// The Docs workflow runs this script and fails when docs/llms.txt changes, so commit the regenerated file.
#:property IsPackable=false
#:property GenerateDocumentationFile=false
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

const string SiteUrl = "https://allandecastro.github.io/vela/";
const string OptionalHeading = "## Optional";

var docs = Path.GetFullPath("docs");
var llmsPath = Path.Combine(docs, "llms.txt");
if (!File.Exists(llmsPath) || !File.Exists(Path.Combine(docs, "toc.yml")))
{
    Console.Error.WriteLine("Run this script from the repository root: docs/llms.txt or docs/toc.yml not found.");
    return 1;
}

var original = File.ReadAllText(llmsPath);
var newline = original.Contains("\r\n") ? "\r\n" : "\n";
var lines = original.Replace("\r\n", "\n").Split('\n');

var firstSection = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal));
var optional = Array.FindIndex(lines, l => l.TrimEnd() == OptionalHeading);
if (firstSection < 0 || optional < firstSection)
{
    Console.Error.WriteLine($"docs/llms.txt must have a header, then \"## \" sections, then a \"{OptionalHeading}\" section.");
    return 1;
}

var output = new List<string>(lines.Take(firstSection));
foreach (var section in Toc.Read(Path.Combine(docs, "toc.yml")))
{
    if (section.Href is null || !section.Href.EndsWith("/", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"docs/toc.yml: '{section.Name}' must point to a folder with its own toc.yml.");
    }

    var folder = section.Href.TrimEnd('/');
    var entries = Toc.Read(Path.Combine(docs, folder, "toc.yml"))
        .Where(e => !(e.Items.Count == 0 && e.Href is not null && section.TopicHref == folder + "/" + e.Href))
        .ToList();
    var groups = entries.All(e => e.Items.Count > 0)
        ? entries.Select(e => (section.Name + ": " + e.Name, e.Items))
        : new[] { (section.Name, entries) };
    foreach (var (name, pages) in groups)
    {
        output.Add("## " + name);
        output.Add("");
        foreach (var page in pages)
        {
            if (page.Href is null || page.Items.Count > 0)
            {
                throw new InvalidOperationException($"{folder}/toc.yml: '{page.Name}' must be a page (href to a .md file).");
            }

            var path = folder + "/" + page.Href;
            output.Add($"- [{page.Name}]({SiteUrl}{path}): {Pages.Describe(Path.Combine(docs, path))}");
        }

        output.Add("");
    }
}

output.AddRange(lines.Skip(optional));
var updated = string.Join(newline, output);
if (updated == original)
{
    Console.WriteLine("docs/llms.txt is up to date.");
}
else
{
    File.WriteAllText(llmsPath, updated, new UTF8Encoding(false));
    Console.WriteLine("docs/llms.txt updated.");
}

return 0;

/// <summary>A toc.yml entry: a page (href) or a group of pages (items).</summary>
internal sealed record TocEntry(string Name, string? Href, List<TocEntry> Items, string? TopicHref = null);

/// <summary>
/// Reads the subset of YAML the docs toc.yml files use: a list of entries with name, href, topicHref and nested
/// items, and comments.
/// </summary>
internal static class Toc
{
    public static List<TocEntry> Read(string file)
    {
        var lines = File.ReadAllLines(file)
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToList();
        var index = 0;
        return ReadList(file, lines, ref index, indent: 0);
    }

    private static List<TocEntry> ReadList(string file, List<string> lines, ref int index, int indent)
    {
        var entries = new List<TocEntry>();
        while (index < lines.Count && Indent(lines[index]) == indent && lines[index].TrimStart().StartsWith("- "))
        {
            string? name = null, href = null, topicHref = null;
            var items = new List<TocEntry>();
            var keyIndent = indent + 2;
            var first = true;
            while (index < lines.Count)
            {
                var line = lines[index];
                string content;
                if (first)
                {
                    content = line.TrimStart()[2..];
                    first = false;
                }
                else if (Indent(line) == keyIndent && !line.TrimStart().StartsWith("- "))
                {
                    content = line.TrimStart();
                }
                else
                {
                    break;
                }

                index++;
                var colon = content.IndexOf(':');
                if (colon < 0)
                {
                    throw new FormatException($"{file}: cannot read '{line.Trim()}'.");
                }

                var key = content[..colon].Trim();
                var value = content[(colon + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "name": name = value; break;
                    case "href": href = value; break;
                    case "topicHref": topicHref = value; break;
                    case "items":
                        var childIndent = index < lines.Count ? Indent(lines[index]) : keyIndent;
                        items = ReadList(file, lines, ref index, childIndent);
                        break;
                }
            }

            entries.Add(new TocEntry(name ?? throw new FormatException($"{file}: an entry has no name."), href, items, topicHref));
        }

        return entries;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}

/// <summary>Reads the one-line description of a docs page.</summary>
internal static class Pages
{
    public static string Describe(string file)
    {
        var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines.Length > 0 && lines[0] == "---")
        {
            var end = Array.IndexOf(lines, "---", 1);
            foreach (var line in lines.Skip(1).Take(end - 1))
            {
                var match = Regex.Match(line, @"^_?description:(.*)$");
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim().Trim('"', '\'');
                }
            }

            start = end + 1;
        }

        // Fallback: the first sentence of the first plain paragraph after the H1 (no heading, note, list or code).
        var paragraph = new List<string>();
        var inFence = false;
        foreach (var line in lines.Skip(start).SkipWhile(l => !l.StartsWith("# ", StringComparison.Ordinal)).Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (paragraph.Count > 0) break;
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (paragraph.Count > 0) break;
                continue;
            }

            if (paragraph.Count == 0 && (trimmed[0] is '#' or '>' or '|' or '<' || Regex.IsMatch(trimmed, @"^([-*+]|\d+\.)\s")))
            {
                continue;
            }

            paragraph.Add(trimmed);
        }

        if (paragraph.Count == 0)
        {
            throw new InvalidOperationException($"{file}: no description front matter and no first paragraph.");
        }

        var text = Regex.Replace(string.Join(" ", paragraph), @"\[([^\]]+)\]\([^)]+\)", "$1");
        var period = Regex.Match(text, @"\.(\s|$)");
        return period.Success ? text[..period.Index] : text;
    }
}
