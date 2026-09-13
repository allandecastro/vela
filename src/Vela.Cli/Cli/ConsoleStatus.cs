using System;
using Spectre.Console;

namespace Vela.Cli;

/// <summary>
/// Runs a slow operation behind an animated status spinner so interactive users get feedback while the CLI
/// waits (Dataverse connect + state read). The spinner is drawn on <b>stderr</b> so stdout stays pure JSON
/// for piping, and it is only shown on a real terminal (never when stderr is redirected or color is opted
/// out) so scripted/CI runs are byte-for-byte unchanged.
/// </summary>
internal static class ConsoleStatus
{
    private static IAnsiConsole? errorConsole;

    private static IAnsiConsole ErrorConsole =>
        errorConsole ??= AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error),
        });

    /// <summary>Runs <paramref name="action"/>, showing a spinner labelled <paramref name="label"/> while it works.</summary>
    public static T Run<T>(string label, Func<T> action)
    {
        if (!ConsoleTheme.StderrColor)
        {
            return action();
        }

        T result = default!;
        ErrorConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(new Color(99, 102, 241))) // brand indigo
            .Start(label, _ => result = action());
        return result;
    }
}
