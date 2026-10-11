namespace Vela.StepApiGen;

/// <summary>
/// Emits src/Vela.Plugins/Steps/TypedTriggers.cs: the names of the messages with a typed <c>On{Message}()</c> trigger,
/// from <see cref="MessageModel"/>. Self-contained (no other Vela type) so the CLI links it as source: the runtime guard
/// of <c>On&lt;TRequest&gt;()</c> and <c>vela models --messages</c> read the same list.
/// </summary>
internal static class TypedTriggersEmitter
{
    public const string RelativePath = "src/Vela.Plugins/Steps/TypedTriggers.cs";

    public static string Emit()
    {
        var w = new CodeWriter();
        w.Header("Typed step triggers.");
        w.Line("namespace Vela.Steps");
        w.Line("{");
        w.Line("    /// <summary>The messages registered through a typed <c>On{Message}()</c> trigger: one way per message, so");
        w.Line("    /// <c>On&lt;TRequest&gt;()</c> rejects them and <c>vela models --messages</c> emits no extension for them.</summary>");
        w.Line("    internal static class TypedTriggers");
        w.Line("    {");
        w.Line("        /// <summary>The request names of the messages with a typed trigger (<c>On{Name}()</c>).</summary>");
        w.Line("        internal static readonly string[] Messages =");
        w.Line("        {");
        foreach (var message in MessageModel.Messages)
        {
            w.Line($"            \"{message.Name}\",");
        }
        w.Line("        };");
        w.Line();
        w.Line("        /// <summary>The typed trigger of <paramref name=\"message\"/> (<c>On{Name}</c>), or null when it has none.</summary>");
        w.Line("        internal static string? Find(string? message)");
        w.Line("        {");
        w.Line("            foreach (string typed in Messages)");
        w.Line("            {");
        w.Line("                if (string.Equals(typed, message?.Trim(), System.StringComparison.OrdinalIgnoreCase))");
        w.Line("                {");
        w.Line("                    return \"On\" + typed;");
        w.Line("                }");
        w.Line("            }");
        w.Line();
        w.Line("            return null;");
        w.Line("        }");
        w.Line("    }");
        w.Line("}");
        return w.ToString();
    }
}
