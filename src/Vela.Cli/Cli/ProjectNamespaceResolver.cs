using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Vela.Cli.CommandLineOptions;

namespace Vela.Cli;

/// <summary>
/// Resolves the target namespace and default C# output directory for <c>vela models</c> from the project it is run
/// against: an explicit <c>--namespace</c> / <c>--out</c> wins, otherwise the project is located (a single top-level
/// .csproj, or a representative project from a single .sln) and the shared-models namespace is derived from its root
/// namespace. Reuses <see cref="CommandLineOptions.ReadRootNamespace"/> / <see cref="CommandLineOptions.StripRoleSuffix"/>
/// rather than duplicating the project-file parsing. Split out of <see cref="ModelsCommand"/>.
/// </summary>
internal static class ProjectNamespaceResolver
{
    // Default output directory for C# models: an explicit --out wins; otherwise, when the namespace is inferred from
    // a project (no --namespace) and a project is found, write into that project's Models/Generated folder so the
    // files land where the inferred namespace says. Falls back to the caller's default (the current directory).
    internal static string ResolveCSharpOutputDir(string[] args, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(TryReadOption(args, "--out"))
            || !string.IsNullOrWhiteSpace(TryReadOption(args, "--namespace")))
        {
            return fallback;
        }

        string? project = ResolveProjectFile(TryReadOption(args, "--project"));
        return project != null
            ? Path.Combine(Path.GetDirectoryName(project)!, "Models", "Generated")
            : fallback;
    }

    internal static string? ResolveNamespace(string[] args)
    {
        string? explicitNs = TryReadOption(args, "--namespace");
        if (!string.IsNullOrWhiteSpace(explicitNs))
        {
            return explicitNs;
        }

        string? projectFile = ResolveProjectFile(TryReadOption(args, "--project"));
        if (projectFile == null)
        {
            return null;
        }

        string prefix = StripRoleSuffix(ReadRootNamespace(projectFile));
        return $"{prefix}.Shared.Models.Generated";
    }

    private static string? ResolveProjectFile(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (File.Exists(requested) && requested.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return requested;
            }

            if (Directory.Exists(requested))
            {
                return FindProjectInDirectory(requested);
            }

            return null;
        }

        return FindProjectInDirectory(Directory.GetCurrentDirectory());
    }

    private static string? FindProjectInDirectory(string directory)
    {
        // A single top-level .csproj is the unambiguous project (running from a project folder).
        string? single = SingleCsproj(directory, out _);
        if (single != null)
        {
            return single;
        }

        // Otherwise, when run from a solution root, derive the namespace from a representative project listed
        // in the .sln so `vela models` "just works" from the root like plan/apply do.
        return ProjectFromSolution(directory);
    }

    // Picks a representative project from a single .sln in the directory. Any role-suffixed project yields the
    // same base namespace via StripRoleSuffix, but prefer .Shared/.Plugins/.Integrations (and avoid the test
    // project) so the derived shared-models namespace is the expected one.
    private static string? ProjectFromSolution(string directory)
    {
        string[] solutions = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
        if (solutions.Length != 1)
        {
            return null;
        }

        string[] projects = ReadSolutionProjectPaths(solutions[0]);
        if (projects.Length == 0)
        {
            return null;
        }

        foreach (string preferred in new[] { ".Shared.csproj", ".Plugins.csproj", ".Integrations.csproj" })
        {
            string? match = projects.FirstOrDefault(p => p.EndsWith(preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return projects.FirstOrDefault(p => !p.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
            ?? projects[0];
    }

    // Parses the project .csproj paths out of a .sln. Project lines look like:
    //   Project("{TypeGuid}") = "Name", "relative\Path.csproj", "{ProjectGuid}"
    private static string[] ReadSolutionProjectPaths(string solutionFile)
    {
        string solutionDir = Path.GetDirectoryName(solutionFile) ?? Directory.GetCurrentDirectory();
        List<string> paths = new List<string>();
        foreach (string line in File.ReadLines(solutionFile))
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            string rawPath = parts[1].Trim().Trim('"');
            if (!rawPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // .sln paths always use backslashes; normalize for non-Windows hosts.
            string relativePath = rawPath.Replace('\\', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
            if (File.Exists(full))
            {
                paths.Add(full);
            }
        }

        return paths.ToArray();
    }
}
