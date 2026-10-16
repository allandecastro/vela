using System;
using System.Collections.Generic;
using System.IO;
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
    // A using directive, through its ';' and, when only spaces and a line comment follow, the end of its line.
    private static readonly Regex UsingDirective = new(@"(?<![\w\.])(?:global\s+)?using\s+(?:static\s+)?(?<ns>[\w\.]+)\s*;(?<eol>[ \t]*(?://[^\n]*)?\r?\n)?", RegexOptions.Compiled);
    private static readonly Regex OtherDeclaration = new(@"\b(?:class|struct|interface|record|enum|namespace|delegate)\b|[};]", RegexOptions.Compiled);
    private static readonly HashSet<string> NotAType = new(StringComparer.Ordinal) { "return", "await", "new", "throw", "else", "yield", "case", "in", "is", "as", "not", "and", "or", "when" };

    /// <summary>The span of a class body in a file: the offsets of its opening and closing braces, and the indentation of its declaration line.</summary>
    internal readonly record struct ClassSpan(int Open, int Close, string Indent);

    /// <summary>Reads a file and the encoding to write it back with (its byte order mark kept, UTF-8 without one by default).</summary>
    internal static string Read(string path, out Encoding encoding)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using StreamReader reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        bool utf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        encoding = reader.CurrentEncoding is UTF8Encoding ? new UTF8Encoding(utf8Bom) : reader.CurrentEncoding;
        return text;
    }

    /// <summary>
    /// Finds the single declaration of <paramref name="className"/> in <paramref name="source"/> and its body. False,
    /// with the reason, when the class is not declared, is declared more than once, has no body of its own, or its
    /// braces do not balance.
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

        // The body is the first '{' after the declaration, unless another declaration (or the end of one) comes first.
        int declaration = declarations[0].Index;
        int afterName = declaration + declarations[0].Length;
        int open = masked.IndexOf('{', afterName);
        if (open < 0 || OtherDeclaration.IsMatch(masked.Substring(afterName, open - afterName)))
        {
            error = $"the declaration of class {className} has no body";
            return false;
        }

        int close = MatchingBrace(masked, open);
        if (close < 0)
        {
            error = $"the braces of class {className} do not balance";
            return false;
        }

        int lineStart = source.LastIndexOf('\n', declaration) + 1;
        string indent = new string(source.Skip(lineStart).TakeWhile(c => c is ' ' or '\t').ToArray());
        span = new ClassSpan(open, close, indent);
        return true;
    }

    /// <summary>
    /// True when the class itself (not a nested type) declares a method named <paramref name="method"/>, outside
    /// comments and literals. A call to a method of that name does not count.
    /// </summary>
    internal static bool DeclaresMethod(string source, ClassSpan span, string method)
    {
        string masked = StepInjector.MaskCommentsAndLiterals(source);
        Regex declaration = new(@"(?<type>\b[A-Za-z_][\w\.]*(?:<[^;{}()]*>)?(?:\[\])?\??)\s+" + Regex.Escape(method) + @"\s*[\(<]");
        int depth = 0;
        int[] depthAt = new int[span.Close - span.Open];
        for (int i = span.Open; i < span.Close; i++)
        {
            depthAt[i - span.Open] = depth;
            if (masked[i] == '{')
            {
                depth++;
            }
            else if (masked[i] == '}')
            {
                depth--;
            }
        }

        return declaration.Matches(masked.Substring(span.Open, span.Close - span.Open))
            .Any(m => depthAt[m.Index] == 1 && !NotAType.Contains(m.Groups["type"].Value));
    }

    /// <summary>
    /// Inserts <paramref name="members"/> (class members indented by 8 spaces, each line ending with '\n') before the
    /// class's closing brace, re-indented to the class's member indentation and in the file's line endings, and adds the
    /// <paramref name="usings"/> the class does not see after the last <c>using</c> directive it sees (else at the top of
    /// the file).
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

        // The directives the class sees: at the top level, or in a namespace block that encloses it (both before it).
        string masked = StepInjector.MaskCommentsAndLiterals(updated);
        List<Match> visible = UsingDirective.Matches(masked)
            .Where(m => m.Index < span.Open && EnclosingBlocks(masked, m.Index).All(block => block < span.Open && MatchingBrace(masked, block) > span.Open))
            .ToList();
        HashSet<string> present = new(visible.Select(m => m.Groups["ns"].Value), StringComparer.Ordinal);
        string[] missing = usings.Where(u => !present.Contains(u)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            return updated;
        }

        StringBuilder lines = new StringBuilder();
        if (visible.Count == 0)
        {
            foreach (string ns in missing)
            {
                lines.Append("using ").Append(ns).Append(';').Append(newline);
            }

            return lines.Append(newline).ToString() + updated;
        }

        Match last = visible[visible.Count - 1];
        int directiveLine = updated.LastIndexOf('\n', last.Index) + 1;
        string indent = new string(updated.Skip(directiveLine).TakeWhile(c => c is ' ' or '\t').ToArray());
        bool endsLine = last.Groups["eol"].Success;
        foreach (string ns in missing)
        {
            if (endsLine)
            {
                lines.Append(indent).Append("using ").Append(ns).Append(';').Append(newline);
            }
            else
            {
                lines.Append(newline).Append(indent).Append("using ").Append(ns).Append(';');
            }
        }

        return updated.Insert(last.Index + last.Length, lines.ToString());
    }

    // The offset of the '}' closing the '{' at <paramref name="open"/>, or -1.
    private static int MatchingBrace(string masked, int open)
    {
        int depth = 0;
        for (int i = open; i < masked.Length; i++)
        {
            if (masked[i] == '{')
            {
                depth++;
            }
            else if (masked[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // The offsets of the '{' of every block that contains <paramref name="position"/>.
    private static List<int> EnclosingBlocks(string masked, int position)
    {
        List<int> open = new List<int>();
        for (int i = 0; i < position; i++)
        {
            if (masked[i] == '{')
            {
                open.Add(i);
            }
            else if (masked[i] == '}' && open.Count > 0)
            {
                open.RemoveAt(open.Count - 1);
            }
        }

        return open;
    }
}
