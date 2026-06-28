using System;

namespace Vela.Cli;

/// <summary> Centralized formatting for registration apply progress lines written to stderr. </summary>
internal static partial class DataverseRegistration
{
    /// <summary>
    /// Writes a single registration operation line in the canonical <c>[tag] action name details</c>
    /// format, e.g. <c>[step] create Foo</c> or <c>  [input-param] update Bar [String, optional]</c>.
    /// </summary>
    /// <param name="tag">Operation category, e.g. <c>step</c>, <c>custom-api</c>, <c>input-param</c>.</param>
    /// <param name="action">Operation verb, e.g. <c>create</c>, <c>update</c>, <c>delete</c>.</param>
    /// <param name="name">Subject name being operated on.</param>
    /// <param name="details">Optional trailing detail (already formatted), appended after a space.</param>
    /// <param name="verbose">When false, suppresses the line.</param>
    /// <param name="indent">When true, prefixes the line with two spaces (nested operations).</param>
    internal static void LogOperation(
        string tag,
        string action,
        string name,
        string? details = null,
        bool verbose = true,
        bool indent = false)
    {
        if (!verbose)
        {
            return;
        }

        string prefix = indent ? "  " : string.Empty;
        string suffix = string.IsNullOrEmpty(details) ? string.Empty : $" {details}";
        Console.Error.WriteLine($"{prefix}[{tag}] {action} {name}{suffix}");
    }
}
