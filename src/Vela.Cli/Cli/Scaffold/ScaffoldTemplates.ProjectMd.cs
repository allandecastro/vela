using System.Collections.Generic;
using System.Text;

namespace Vela.Cli;

/// <summary>The generated <c>PROJECT.md</c>: the project values the commands take as flags.</summary>
internal static partial class ScaffoldTemplates
{
    /// <summary>The version of the <c>PROJECT.md</c> front matter format. A reader that finds another version stops
    /// instead of misreading the file.</summary>
    public const int ProjectMdSchemaVersion = 1;

    /// <summary>
    /// <c>PROJECT.md</c>, for people and agents only: the CLI never reads it. Its YAML front matter records the
    /// publisher prefix (<c>--publisher-prefix</c>) and maps every generated plugin package id to the solution it
    /// deploys into (<c>--solution</c>); a value that was not given is left empty with a comment. <c>vela init</c>
    /// writes it once, tracked in the scaffold manifest like every generated file, and no command edits it.
    /// </summary>
    public static string ProjectMd(ScaffoldOptions options)
    {
        List<string> packageIds = [options.PluginsPackageId];
        if (options.IncludeIntegrations)
        {
            packageIds.Add(options.IntegrationsPackageId);
        }

        StringBuilder text = new();
        text.Append("---\n");
        text.Append($"schemaVersion: {ProjectMdSchemaVersion}\n");
        text.Append(options.KnownPublisherPrefix is not { } publisherPrefix
            ? "publisherPrefix:  # the publisher prefix of your solution: vela solution show <solution> --json\n"
            : $"publisherPrefix: {publisherPrefix}\n");
        text.Append("packages:\n");
        foreach (string packageId in packageIds)
        {
            text.Append(string.IsNullOrEmpty(options.SolutionUniqueName)
                ? $"  {packageId}:  # the unique name of the solution this package deploys into\n"
                : $"  {packageId}: {options.SolutionUniqueName}\n");
        }

        text.Append("---\n");
        text.Append('\n');
        text.Append("# Project\n");
        text.Append('\n');
        text.Append("Values the Vela commands need. Pass them as flags: `--solution` on plan/apply, `--prefix` on\n");
        text.Append("add customapi. Check a solution's prefix with `vela solution show <solution> --json`. The\n");
        text.Append("environment URL is per user and never lives in the repo.\n");
        return text.ToString();
    }
}
