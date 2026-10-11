using System;
using System.Collections.Generic;
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
    // Convenience overload: default to a PostOperation + Sync step (the historical behaviour), no explicit filter.
    public static bool TryInject(string source, string entityType, string message, string registrationCall, string handlerName, string[]? explicitFilterAttributes, out string result, out string error)
        => TryInject(source, entityType, message, registrationCall, handlerName, ".PostOperation()", ".Sync()", true, explicitFilterAttributes, out result, out error, out _);

    public static bool TryInject(string source, string entityType, string message, string registrationCall, string handlerName, string stageCall, string modeCall, bool postStage, string[]? explicitFilterAttributes, out string result, out string error)
        => TryInject(source, entityType, message, registrationCall, handlerName, stageCall, modeCall, postStage, explicitFilterAttributes, out result, out error, out _);

    // <paramref name="explicitFilterAttributes"/>: the resolved property name(s) to emit as a FilterOn selector.
    // Required for the messages AddCommand.RequiresFilter names; null or empty for any other message.
    // <paramref name="note"/>: set (and the step still added) when the plugin already has a step on the same trigger
    // that this one is told apart from; null otherwise.
    public static bool TryInject(string source, string entityType, string message, string registrationCall, string handlerName, string stageCall, string modeCall, bool postStage, string[]? explicitFilterAttributes, out string result, out string error, out string? note)
    {
        result = source;
        error = string.Empty;
        note = null;

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

        // Emit the FilterOn selector the caller resolved. A message that requires one (AddCommand.RequiresFilter)
        // is rejected before injection when none was given, so there is never a placeholder to write.
        bool hasAttributes = explicitFilterAttributes is { Length: > 0 };
        if (AddCommand.RequiresFilter(message) != hasAttributes)
        {
            // FilterOn exists only on the filter-required messages: emitting it elsewhere would not compile.
            error = hasAttributes
                ? $"A {message} step has no FilterOn; filtering attributes only apply to Create, Update, CreateMultiple and UpdateMultiple."
                : $"A {message} step needs filtering attributes.";
            return false;
        }

        // Several steps may share a trigger (message + stage + mode) when they are told apart by their filtering
        // columns, images, WithStepKey or ForRelationship (A-25). The step written here has only its FilterOn, so it is
        // an exact duplicate - indistinguishable, refused - when an existing step on the same trigger has the same
        // filter columns and none of the other distinguishing modifiers. The add-step defaults (Create /
        // PostOperation / Sync) are exactly what `vela add plugin` registers, so this is the common case.
        TriggerMatch match = FindDuplicateTrigger(body, registrationCall, stageCall, modeCall, explicitFilterAttributes ?? []);
        if (match.Kind == TriggerMatchKind.ExactDuplicate)
        {
            error = $"This plugin already registers a {match.Trigger} step with the same filtering columns and no image, step key or relationship, so the two could not be told apart (VELA008). Pass different --filtering-attributes, another --message, --stage or --mode, or add the columns to the existing step and branch in its handler.";
            return false;
        }

        if (match.Kind == TriggerMatchKind.SharedTrigger)
        {
            note = $"This plugin already registers a {match.Trigger} step; the new one is told apart by its filtering columns, which is allowed. Consider naming both with .WithStepKey(\"...\") for clarity.";
        }

        string filtering = explicitFilterAttributes is { Length: > 0 }
            ? $"                .FilterOn({string.Join(", ", System.Array.ConvertAll(explicitFilterAttributes, a => $"e => e.{a}"))})\n"
            : string.Empty;

        string registration =
            $"\n\n            RegisterStep().{registrationCall}\n" +
            $"                {stageCall}\n" +
            $"                {modeCall}\n" +
            filtering +
            $"                .Handle({handlerName});";

        // A modelled message binds the strongly-typed stage context (pre or post to match the step's stage); an
        // On<XxxRequest>() step binds the request-typed IMessagePre/PostContext<XxxRequest>.
        string handlerContextType = AddCommand.HandlerContextType(registrationCall, message, entityType, postStage);

        // Point the author at the idiomatic, typed access for this message rather than the low-level
        // ctx.GetTarget<T>(): a single record is ctx.Request.Target, a bulk message is ctx.Request.Targets,
        // and any other message reads its typed request.
        // Only CreateMultiple/UpdateMultiple carry a Targets collection; RetrieveMultiple is query-based.
        bool isMultiple = message == "CreateMultiple" || message == "UpdateMultiple";
        string accessHint = AddCommand.TryGetRequestType(registrationCall, out _)
            ? "read the typed request via ctx.Request"
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

    // One registration chain: RegisterStep().On<Message>() or OnMessage("name"), then everything up to its ';'.
    private static readonly Regex Registration = new(
        @"RegisterStep\s*\(\s*\)\s*\.\s*(?:On(?<typed>[A-Za-z]\w*)\s*\(\s*\)|OnMessage\s*\(\s*""(?<custom>[^""]*)""\s*\))(?<rest>[^;]*);",
        RegexOptions.Compiled);

    private static readonly Regex StageCall = new(@"\.\s*(?<stage>PreValidation|PreOperation|PostOperation)\s*\(\s*\)", RegexOptions.Compiled);
    private static readonly Regex AsyncCall = new(@"\.\s*Async\s*\(\s*\)", RegexOptions.Compiled);

    // Modifiers that tell two steps on the same trigger apart besides their filter columns (A-25).
    private static readonly Regex Distinguishing = new(@"\.\s*(?:WithStepKey|WithPreImage|WithPostImage|WithSubordinatePreImage|WithSubordinatePostImage|ForRelationship)\s*[<(]", RegexOptions.Compiled);
    private static readonly Regex FilterOnCall = new(@"\.\s*FilterOn\s*\(", RegexOptions.Compiled);
    private static readonly Regex SelectedMember = new(@"=>\s*\w+\s*\.\s*(?<member>\w+)", RegexOptions.Compiled);

    internal enum TriggerMatchKind
    {
        None,
        SharedTrigger,
        ExactDuplicate,
    }

    internal readonly record struct TriggerMatch(TriggerMatchKind Kind, string? Trigger);

    // Compares the step being added with the registrations in <paramref name="body"/>: an ExactDuplicate is an existing
    // step with the same trigger (messages compare case-insensitively, so OnMessage("create") is the Create trigger),
    // the same filter columns and no other distinguishing modifier; a SharedTrigger is any other step on that trigger.
    // A chain whose stage cannot be read is skipped, so this never refuses on a guess.
    // The chains are found in a copy with comments and literal contents blanked (offsets kept), so a registration
    // inside a string or comment is not read, and a ';' or a ".FilterOn(" inside a literal neither ends nor alters a
    // chain. An OnMessage("...") argument is then read from the original text at the same offsets.
    internal static TriggerMatch FindDuplicateTrigger(string body, string registrationCall, string stageCall, string modeCall, string[] filterAttributes)
    {
        string addedSource = "RegisterStep()." + registrationCall + stageCall + modeCall + ";";
        Match added = Registration.Match(MaskCommentsAndLiterals(addedSource));
        if (!added.Success)
        {
            return new TriggerMatch(TriggerMatchKind.None, null);
        }

        (string message, string? stage, bool isAsync) wanted = Trigger(added, addedSource);
        HashSet<string> wantedFilter = new(filterAttributes, StringComparer.Ordinal);
        TriggerMatch result = new(TriggerMatchKind.None, null);
        foreach (Match existing in Registration.Matches(MaskCommentsAndLiterals(body)))
        {
            (string message, string? stage, bool isAsync) found = Trigger(existing, body);
            if (found.stage is null
                || !string.Equals(found.message, wanted.message, StringComparison.OrdinalIgnoreCase)
                || found.stage != wanted.stage
                || found.isAsync != wanted.isAsync)
            {
                continue;
            }

            string trigger = $"{found.message} {found.stage} {(found.isAsync ? "Async" : "Sync")}";
            string rest = existing.Groups["rest"].Value;
            if (!Distinguishing.IsMatch(rest) && FilterColumns(rest).SetEquals(wantedFilter))
            {
                return new TriggerMatch(TriggerMatchKind.ExactDuplicate, trigger);
            }

            result = new TriggerMatch(TriggerMatchKind.SharedTrigger, trigger);
        }

        return result;
    }

    // The member names selected by a chain's FilterOn(...) call (e => e.Name, c => c.Telephone1, ...).
    private static HashSet<string> FilterColumns(string chain)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);
        Match call = FilterOnCall.Match(chain);
        if (!call.Success)
        {
            return columns;
        }

        int start = call.Index + call.Length;
        int depth = 1;
        int end = start;
        while (end < chain.Length && depth > 0)
        {
            depth += chain[end] == '(' ? 1 : chain[end] == ')' ? -1 : 0;
            end++;
        }

        foreach (Match member in SelectedMember.Matches(chain.Substring(start, Math.Max(0, end - start - 1))))
        {
            columns.Add(member.Groups["member"].Value);
        }

        return columns;
    }

    // <paramref name="registration"/> matched the masked text; <paramref name="original"/> supplies the literal text.
    private static (string Message, string? Stage, bool IsAsync) Trigger(Match registration, string original)
    {
        Group custom = registration.Groups["custom"];
        string message = registration.Groups["typed"].Success && registration.Groups["typed"].Value != "Message"
            ? registration.Groups["typed"].Value
            : original.Substring(custom.Index, custom.Length);
        string rest = registration.Groups["rest"].Value;
        Match stage = StageCall.Match(rest);
        return (message, stage.Success ? stage.Groups["stage"].Value : null, AsyncCall.IsMatch(rest));
    }

    // Blanks out comments and the contents of string and char literals (keeping their delimiters, line breaks and
    // every offset), so only live code is searched.
    internal static string MaskCommentsAndLiterals(string source)
    {
        char[] masked = source.ToCharArray();
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length && (source[i + 1] == '/' || source[i + 1] == '*'))
            {
                int end = source[i + 1] == '/'
                    ? source.IndexOf('\n', i) is int nl and >= 0 ? nl - 1 : source.Length - 1
                    : source.IndexOf("*/", i + 2, StringComparison.Ordinal) is int close and >= 0 ? close + 1 : source.Length - 1;
                for (int j = i; j <= end; j++)
                {
                    if (masked[j] != '\n')
                    {
                        masked[j] = ' ';
                    }
                }

                i = end;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                int close = c == '"' ? SkipString(source, i) : SkipCharLiteral(source, i);
                if (close < 0)
                {
                    break;
                }

                for (int j = i + 1; j < close; j++)
                {
                    if (masked[j] != '\n')
                    {
                        masked[j] = ' ';
                    }
                }

                i = close;
            }
        }

        return new string(masked);
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

    // Given the index of the opening quote, returns the index of the closing quote. Handles regular, verbatim
    // (@"", $@"", @$"") and raw ("""...""", $$"""...""") string literals; braces inside any of them are content.
    private static int SkipString(string s, int i)
    {
        // The prefix decides the kind first: a verbatim string (@"", $@"", @$"") may open with an escaped quote
        // (@"""quoted"" text"), which would otherwise look like a raw string's """ fence.
        bool verbatim = false;
        for (int back = i - 1; back >= 0 && back >= i - 3 && (s[back] == '@' || s[back] == '$'); back--)
        {
            verbatim |= s[back] == '@';
        }

        int quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"')
        {
            quotes++;
        }

        if (!verbatim && quotes >= 3)
        {
            // Raw string literal: it ends at the next run of (at least) the same number of quotes.
            string fence = new('"', quotes);
            int close = s.IndexOf(fence, i + quotes, StringComparison.Ordinal);
            if (close < 0)
            {
                return -1;
            }

            int last = close + quotes - 1;
            while (last + 1 < s.Length && s[last + 1] == '"')
            {
                last++;
            }

            return last;
        }

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
