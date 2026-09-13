using System.Globalization;
using System.Linq;
using System.Text;

namespace Vela.Cli;

/// <summary>
/// Small helpers to turn arbitrary Dataverse names (Custom API parameter names, SDK message field names) into
/// valid, non-colliding C# member identifiers. The model generators can rely on the SchemaName being a valid
/// identifier; the message / Custom API generators cannot (a parameter can be named <c>object</c>, <c>params</c>,
/// or carry a dot/space), so they route names through here before emitting <c>public {type} {name}</c>.
/// </summary>
internal static class CSharpIdentifier
{
    private static readonly System.Collections.Generic.HashSet<string> Keywords = new(System.StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>Prefixes a bare C# keyword with <c>@</c> so it is usable as an identifier; otherwise returns it unchanged.</summary>
    public static string EscapeKeyword(string identifier) =>
        Keywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>
    /// Resolves the C# member name for a Parameters/Results-backed property and the explicit key argument to pass to
    /// <c>Get&lt;T&gt;()</c> / <c>Set(value)</c>. The key argument is empty when the member's
    /// <c>[CallerMemberName]</c> already equals the raw name (the common case, including a plain <c>@keyword</c>
    /// escape, whose caller name drops the <c>@</c>); it is a quoted literal of the raw name when the member had to
    /// be sanitized (e.g. a dot or space), so the runtime bag key stays the real Dataverse name.
    /// </summary>
    public static (string Member, string KeyArg) Member(string rawName)
    {
        string member = Sanitize(rawName);
        string effective = member.StartsWith("@") ? member.Substring(1) : member;
        string keyArg = string.Equals(effective, rawName, System.StringComparison.Ordinal) ? string.Empty : Literal(rawName);
        return (member, keyArg);
    }

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Formats an option-set integer value as a valid identifier fragment: a negative value uses an <c>n</c> prefix
    /// on its magnitude (<c>-1</c> -&gt; <c>n1</c>) so it never yields an invalid member like <c>_-1</c>.
    /// </summary>
    public static string NumericToken(int value) =>
        value < 0
            ? "n" + (-(long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns an arbitrary name into a valid C# identifier: non-identifier characters become <c>_</c>, a leading
    /// digit is prefixed with <c>_</c>, an empty result becomes <c>_</c>, and a C# keyword is prefixed with <c>@</c>.
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "_";
        }

        StringBuilder sb = new StringBuilder(name!.Length);
        foreach (char c in name!)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        string cleaned = sb.ToString();
        if (cleaned.Length == 0)
        {
            return "_";
        }

        if (char.IsDigit(cleaned[0]))
        {
            cleaned = "_" + cleaned;
        }

        return EscapeKeyword(cleaned);
    }
}
