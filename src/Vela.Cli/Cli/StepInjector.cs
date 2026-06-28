using System;
using System.Text.RegularExpressions;

namespace Vela.Cli;

/// <summary>
/// Injects an extra plugin step — a <c>RegisterSteps()…</c> registration plus a matching handler method —
/// into an existing handler-based (multi-step) plugin source file. The edit is text-based but
/// brace/string/comment-aware, and it refuses to touch a file it cannot parse cleanly so it never corrupts
/// source. Single-step plugins (those using the <c>Execute(...)</c> override) are rejected: converting them
/// to multi-step is left to the author.
/// </summary>
internal static class StepInjector
{
    public static bool TryInject(string source, string entityType, string message, string registrationCall, string handlerName, out string result, out string error)
    {
        result = source;
        error = string.Empty;

        int methodIdx = source.IndexOf("RegisterPluginSteps", StringComparison.Ordinal);
        if (methodIdx < 0)
        {
            error = "No RegisterPluginSteps() method found — 'vela add step' targets a plugin that overrides RegisterPluginSteps().";
            return false;
        }

        int openBrace = source.IndexOf('{', methodIdx);
        if (openBrace < 0)
        {
            error = "Could not locate the RegisterPluginSteps() body.";
            return false;
        }

        int closeBrace = FindMatchingBrace(source, openBrace);
        if (closeBrace < 0)
        {
            error = "Unbalanced braces around RegisterPluginSteps() — refusing to edit.";
            return false;
        }

        string body = source.Substring(openBrace, closeBrace - openBrace + 1);
        if (!body.Contains(".Handler("))
        {
            error = "Target plugin is single-step (it uses the Execute(...) override). Convert its existing step to .Handler(nameof(...)) first; 'vela add step' adds to handler-based multi-step plugins.";
            return false;
        }

        if (Regex.IsMatch(source, $@"\b{Regex.Escape(handlerName)}\s*\("))
        {
            error = $"A member named '{handlerName}' already exists in this file. Pass a different --handler.";
            return false;
        }

        int lineStart = source.LastIndexOf('\n', closeBrace);
        if (lineStart < 0)
        {
            error = "Unexpected file layout — refusing to edit.";
            return false;
        }

        // Create/Update steps require filtering attributes at compile time; we don't know the entity's
        // columns here, so emit a TODO placeholder on Id (present on every early-bound type) so it compiles.
        bool needsFiltering = string.Equals(message, "Create", StringComparison.Ordinal)
            || string.Equals(message, "Update", StringComparison.Ordinal);
        string filtering = needsFiltering
            ? "                .FilteringAttributes(e => e.Id /* TODO: choose the attributes that should trigger this step */)\n"
            : string.Empty;

        string registration =
            $"\n\n            RegisterSteps().{registrationCall}\n" +
            "                .Stage(PluginStages.PostOperation)\n" +
            "                .Mode(ExecutionMode.Synchronous)\n" +
            filtering +
            $"                .Handler(nameof({handlerName}));";

        string handler =
            $"\n\n        private void {handlerName}(ILocalPluginContext ctx, {entityType} target)\n" +
            "        {\n" +
            $"            // TODO: implement the {message} step.\n" +
            "        }";

        result = source.Substring(0, lineStart)
            + registration
            + source.Substring(lineStart, closeBrace + 1 - lineStart)
            + handler
            + source.Substring(closeBrace + 1);
        return true;
    }

    // Returns the index of the '}' matching the '{' at openIndex, skipping braces inside comments,
    // string literals (regular and verbatim) and char literals.
    private static int FindMatchingBrace(string source, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int nl = source.IndexOf('\n', i);
                if (nl < 0)
                {
                    return -1;
                }

                i = nl;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return -1;
                }

                i = end + 1;
                continue;
            }

            if (c == '"')
            {
                i = SkipString(source, i);
                if (i < 0)
                {
                    return -1;
                }

                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(source, i);
                if (i < 0)
                {
                    return -1;
                }

                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    // Given the index of the opening quote, returns the index of the closing quote.
    private static int SkipString(string s, int i)
    {
        bool verbatim = i > 0 && s[i - 1] == '@';
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i;
                }
            }
            else
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    private static int SkipCharLiteral(string s, int i)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (s[i] == '\'')
            {
                return i;
            }

            i++;
        }

        return -1;
    }
}
