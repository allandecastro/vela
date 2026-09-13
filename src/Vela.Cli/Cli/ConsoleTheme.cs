using System;

namespace Vela.Cli;

/// <summary>
/// Central terminal styling for the CLI: the brand palette (see <c>branding/DESIGN_SYSTEM.md</c>), glyphs,
/// and small helpers that wrap text in ANSI escape codes.
/// </summary>
/// <remarks>
/// Color is emitted only when the target stream is a real terminal. When stdout/stderr is redirected to a
/// file, pipe, or CI/test capture — or when <c>NO_COLOR</c> / <c>VELA_NO_COLOR</c> is set — the helpers return
/// the plain text unchanged. That keeps piped and scripted output clean (Vela is AI-ready: agents parse it)
/// and keeps existing plain-text assertions matching. Styling is applied only around whole tokens/lines, never
/// inside a word, so substring checks survive even if color is ever left on.
/// </remarks>
internal static class ConsoleTheme
{
    private const string Escape = "\x1b[";
    private const string Reset = "\x1b[0m";

    // Brand palette as 24-bit truecolor SGR parameters. Indigo/Cyan are the brand gradient endpoints;
    // Slate is secondary text. Green/Red/Amber are the diff/status accents (not brand marks, so kept as
    // conventional, accessible signal colors).
    private const string Indigo = "38;2;99;102;241";   // #6366F1 - brand / headings
    private const string Cyan = "38;2;34;211;238";     // #22D3EE - accent / command names
    private const string Slate = "38;2;148;163;184";   // #94A3B8 - muted / secondary text
    private const string Green = "38;2;34;197;94";      // #22C55E - added / success
    private const string Red = "38;2;239;68;68";        // #EF4444 - removed / error
    private const string Amber = "38;2;245;158;11";     // #F59E0B - changed / warning
    private const string BoldSgr = "1";

    /// <summary>Glyphs used across command output. Plain, single-width, and safe on modern terminals.</summary>
    public const string Check = "✓";   // ✓
    public const string Cross = "✗";   // ✗
    public const string Bullet = "•";  // •
    public const string Arrow = "→";   // →

    /// <summary>True unless a NO_COLOR / VELA_NO_COLOR opt-out is set (independent of stream redirection).</summary>
    private static bool ColorAllowed =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELA_NO_COLOR"));

    /// <summary>Whether ANSI styling should be written to standard output right now.</summary>
    public static bool StdoutColor => ColorAllowed && !Console.IsOutputRedirected;

    /// <summary>Whether ANSI styling should be written to standard error right now.</summary>
    public static bool StderrColor => ColorAllowed && !Console.IsErrorRedirected;

    private static string Paint(string sgr, string text, bool enabled) =>
        enabled ? $"{Escape}{sgr}m{text}{Reset}" : text;

    // Semantic helpers. Each takes the caller's "is this stream a terminal" decision so a single call site
    // can style stdout and stderr differently. Pass ConsoleTheme.StdoutColor / StderrColor.
    public static string Heading(string text, bool color) => Paint($"{BoldSgr};{Indigo}", text, color);
    public static string Accent(string text, bool color) => Paint(Cyan, text, color);
    public static string Muted(string text, bool color) => Paint(Slate, text, color);
    public static string Added(string text, bool color) => Paint(Green, text, color);
    public static string Removed(string text, bool color) => Paint(Red, text, color);
    public static string Changed(string text, bool color) => Paint(Amber, text, color);
    public static string Bold(string text, bool color) => Paint(BoldSgr, text, color);

    /// <summary>Green success line prefixed with a check glyph.</summary>
    public static string Success(string text, bool color) => Paint(Green, $"{Check} {text}", color);

    /// <summary>Red failure line prefixed with a cross glyph.</summary>
    public static string Failure(string text, bool color) => Paint(Red, $"{Cross} {text}", color);

    /// <summary>
    /// Writes a command-completion line to stdout as a green "✓ message" on a terminal, or the bare message
    /// when stdout is redirected — so piped/scripted/asserted output is byte-for-byte unchanged.
    /// </summary>
    public static void WriteSuccessLine(string message) =>
        Console.WriteLine(StdoutColor ? Success(message, true) : message);
}
