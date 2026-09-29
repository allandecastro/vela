using System;
using System.Text.RegularExpressions;

namespace Vela.Cli;

/// <summary>
/// Injects an extra plugin step — a <c>RegisterStep()…</c> registration plus a matching handler method —
/// into an existing handler-based (multi-step) plugin source file. The edit is text-based but
/// brace/string/comment-aware, and it refuses to touch a file it cannot parse cleanly so it never corrupts
/// source. Plugins whose steps do not bind a handler are rejected: converting them to handler-based is left
/// to the author.
/// </summary>
internal static class StepInjector
{
    // Convenience overload: default to a PostOperation + Sync step (the historical behaviour).
    public static bool TryInject(string source, string entityType, string message, string registrationCall, string handlerName, out string result, out string error)
        => TryInject(source, entityType, message, registrationCall, handlerName, ".PostOperation()", ".Sync()", true, out result, out error);

    public static bool TryInject(string source, string entityType, string message, string registrationCall, string handlerName, string stageCall, string modeCall, bool postStage, out string result, out string error)
    {
        result = source;
        error = string.Empty;

        // Anchor on the method declaration (return type + name + empty parens), not the first textual mention:
        // a doc-comment, a base.RegisterPluginSteps() call, or a string reference would otherwise mis-anchor the
        // injection into the wrong brace block.
        Match method = Regex.Match(source, @"\bvoid\s+RegisterPluginSteps\s*\(\s*\)");
        if (!method.Success)
        {
            error = "No RegisterPluginSteps() method found — 'vela add step' targets a plugin that overrides RegisterPluginSteps().";
            return false;
        }

        int openBrace = source.IndexOf('{', method.Index + method.Length);
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
        if (!body.Contains(".Handle("))
        {
            error = "Target plugin has no handler-bound step yet. Add a step that ends with .Handle(...) first; 'vela add step' adds to handler-based multi-step plugins.";
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

        // FilterOn is optional, and we cannot know the entity's columns here. Do NOT emit a selector: a
        // placeholder such as e => e.Id is a VELA001 error (Id is declared on Entity, not a Dataverse column),
        // so the generated file would not compile. Leave a TODO comment instead - the step compiles as-is and
        // the author adds the real filter. Only Create/Update (and their bulk variants) get the hint.
        bool needsFiltering = string.Equals(message, "Create", StringComparison.Ordinal)
            || string.Equals(message, "Update", StringComparison.Ordinal)
            || string.Equals(message, "CreateMultiple", StringComparison.Ordinal)
            || string.Equals(message, "UpdateMultiple", StringComparison.Ordinal);
        string filtering = needsFiltering
            ? "                // TODO: add .FilterOn(e => e.Column) to scope this step to the attribute changes it needs.\n"
            : string.Empty;

        string registration =
            $"\n\n            RegisterStep().{registrationCall}\n" +
            $"                {stageCall}\n" +
            $"                {modeCall}\n" +
            filtering +
            $"                .Handle({handlerName});";

        // A modelled message binds the strongly-typed stage context (pre or post to match the step's stage);
        // a custom OnMessage step is untyped.
        string handlerContextType = registrationCall.StartsWith("OnMessage(", StringComparison.Ordinal)
            ? "ILocalPluginContext"
            : $"I{message}{(postStage ? "Post" : "Pre")}Context<{entityType}>";

        // Point the author at the idiomatic, typed access for this message rather than the low-level
        // ctx.GetTarget<T>(): a single record is ctx.Request.Target, a bulk message is ctx.Request.Targets,
        // and a custom message reads its own input parameters.
        bool isMultiple = message.EndsWith("Multiple", StringComparison.Ordinal);
        string accessHint = registrationCall.StartsWith("OnMessage(", StringComparison.Ordinal)
            ? "read the message inputs via ctx.PluginExecutionContext.InputParameters"
            : isMultiple
                ? "read the records via ctx.Request.Targets"
                : "read the record via ctx.Request.Target";

        string handler =
            $"\n\n        private void {handlerName}({handlerContextType} ctx)\n" +
            "        {\n" +
            $"            // TODO: implement the {message} step ({accessHint}).\n" +
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
