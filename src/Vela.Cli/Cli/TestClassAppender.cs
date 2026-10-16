using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vela.Cli;

/// <summary>
/// Completes an existing test class for <c>vela add test</c>: finds the class in the file, tells which generated test
/// methods it already declares, and inserts new members before its closing brace (plus any missing <c>using</c>
/// directive), leaving every other byte of the file as it was. Text-based, over a copy with comments and literal
/// contents masked, like <see cref="StepInjector"/>.
/// </summary>
internal static class TestClassAppender
{
    private static readonly Regex UsingDirective = new(@"^[ \t]*(?:global\s+)?using\s+(?:static\s+)?(?<ns>[\w\.]+)\s*;[^\n]*\n?", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The span of a class body in a file: the offsets of its opening and closing braces, and the indentation of its declaration line.</summary>
    internal readonly record struct ClassSpan(int Open, int Close, string Indent);

    /// <summary>
    /// Finds the single declaration of <paramref name="className"/> in <paramref name="source"/> and its body. False,
    /// with the reason, when the class is not declared, is declared more than once, or its braces do not balance.
    /// </summary>
    internal static bool TryFindClass(string source, string className, out ClassSpan span, out string error)
    {
        span = default;
        error = string.Empty;
        string masked = StepInjector.MaskCommentsAndLiterals(source);
        MatchCollection declarations = Regex.Matches(masked, $@"\bclass\s+{Regex.Escape(className)}\b");
        if (declarations.Count != 1)
        {
            error = declarations.Count == 0
                ? $"no class {className} is declared in it"
                : $"class {className} is declared {declarations.Count} times in it";
            return false;
        }

        int declaration = declarations[0].Index;
        int open = masked.IndexOfAny(['{', ';'], declaration);
        if (open < 0 || masked[open] != '{')
        {
            error = $"the declaration of class {className} has no body";
            return false;
        }

        int depth = 0;
        for (int i = open; i < masked.Length; i++)
        {
            if (masked[i] == '{')
            {
                depth++;
            }
            else if (masked[i] == '}' && --depth == 0)
            {
                int lineStart = source.LastIndexOf('\n', declaration) + 1;
                string indent = new string(source.Skip(lineStart).TakeWhile(c => c is ' ' or '\t').ToArray());
                span = new ClassSpan(open, i, indent);
                return true;
            }
        }

        error = $"the braces of class {className} do not balance";
        return false;
    }

    /// <summary>True when the class body declares a method named <paramref name="method"/> (outside comments and literals).</summary>
    internal static bool DeclaresMethod(string source, ClassSpan span, string method)
    {
        string body = StepInjector.MaskCommentsAndLiterals(source).Substring(span.Open, span.Close - span.Open);
        return Regex.IsMatch(body, $@"\b{Regex.Escape(method)}\s*\(");
    }

    /// <summary>
    /// Inserts <paramref name="members"/> (class members indented by 8 spaces, each line ending with '\n') before the
    /// class's closing brace, re-indented to the class's member indentation and in the file's line endings, and adds the
    /// <paramref name="usings"/> the file lacks after its last <c>using</c> directive (else at its top).
    /// </summary>
    internal static string Insert(string source, ClassSpan span, string members, IEnumerable<string> usings)
    {
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string memberIndent = span.Indent + (span.Indent.Contains('\t') ? "\t" : "    ");
        string reindented = string.Concat(members.TrimEnd('\n').Split('\n')
            .Select(line => (line.StartsWith("        ", StringComparison.Ordinal) ? memberIndent + line.Substring(8) : line) + newline));

        // Before the closing brace: on a line of its own, the members go on the lines just above it, after a blank line;
        // after other code on the same line ("{ }"), on new lines between them.
        int lineStart = source.LastIndexOf('\n', span.Close) + 1;
        bool braceAlone = source.Substring(lineStart, span.Close - lineStart).All(c => c is ' ' or '\t');
        string updated = braceAlone
            ? source.Insert(lineStart, newline + reindented)
            : source.Insert(span.Close, newline + newline + reindented + span.Indent);

        string masked = StepInjector.MaskCommentsAndLiterals(updated);
        List<Match> directives = UsingDirective.Matches(masked).Where(m => m.Index < span.Open).ToList();
        HashSet<string> present = new(directives.Select(m => m.Groups["ns"].Value), StringComparer.Ordinal);
        string[] missing = usings.Where(u => !present.Contains(u)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            return updated;
        }

        StringBuilder lines = new StringBuilder();
        if (directives.Count == 0)
        {
            foreach (string ns in missing)
            {
                lines.Append("using ").Append(ns).Append(';').Append(newline);
            }

            return lines.Append(newline).ToString() + updated;
        }

        Match last = directives[directives.Count - 1];
        string indent = new string(updated.Skip(last.Index).TakeWhile(c => c is ' ' or '\t').ToArray());
        int after = last.Index + last.Length;
        bool endsLine = last.Value.EndsWith('\n');
        foreach (string ns in missing)
        {
            lines.Append(endsLine ? string.Empty : newline).Append(indent).Append("using ").Append(ns).Append(';').Append(endsLine ? newline : string.Empty);
        }

        return updated.Insert(after, lines.ToString());
    }
}
