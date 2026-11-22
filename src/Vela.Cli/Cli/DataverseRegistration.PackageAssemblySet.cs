using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// The plugin assemblies of a plugin package are fixed when the package is created. An update cannot change that set
/// (verified live): new content that renames or drops a registered assembly is refused atomically with 0x80040265
/// ("Unable to delete '&lt;type&gt;' plugintype due to N step(s)" while its types have steps, then "Unable to delete
/// plug-in assembly as it is part of plugin package" without steps), and new content that adds an assembly is accepted
/// but registers nothing for it. The only route is to delete the package (after its steps) and create it again, which
/// Vela leaves to the user: plan and apply stop before any write and explain it.
/// </summary>
internal static partial class DataverseRegistration
{
    /// <summary>
    /// The read-only pre-flight for the package updates of the run: for each plugin package the run republishes, the
    /// deployed assemblies (<c>pluginassembly.packageid</c>) are compared with the local package's. A deployed assembly
    /// the local packages no longer build (removed or renamed), or a local plugin assembly the deployed package does
    /// not have (added or renamed), is one blocking problem per package.
    /// </summary>
    internal static IReadOnlyList<string> FindPackageAssemblySetChanges(IOrganizationService service, RegistrationState local, IReadOnlyList<PackagePublishAction> packageActions, string? solutionUniqueName)
    {
        HashSet<string> updated = new(packageActions.Where(a => !a.IsCreate).Select(a => a.PackageName), StringComparer.OrdinalIgnoreCase);
        if (updated.Count == 0)
        {
            return [];
        }

        // An assembly the local scan knows (each package's plugin assembly and the registrable assemblies it ships) is
        // not reported as removed: whether Dataverse also registers a package's dependency assemblies is not checked.
        HashSet<string> localAssemblies = new(
            local.SourceAssemblies
                .Concat(local.SourceAssemblyPayloads.Select(p => p.AssemblyName))
                .Concat(local.PackagePayloads.Select(p => p.AssemblyName).OfType<string>()),
            StringComparer.OrdinalIgnoreCase);
        string? prefix = string.IsNullOrWhiteSpace(solutionUniqueName) ? null : ResolveSolutionCustomizationPrefix(service, solutionUniqueName!);
        List<string> problems = [];
        foreach (PluginPackagePayload payload in local.PackagePayloads)
        {
            string packageName = NormalizePackageNameWithPrefix(payload.PackageName, prefix);
            if (!updated.Contains(packageName) || GetPluginPackage(service, packageName) is not { } package)
            {
                continue;
            }

            QueryExpression query = new QueryExpression("pluginassembly") { ColumnSet = new ColumnSet("pluginassemblyid", "name") };
            query.Criteria.AddCondition("packageid", ConditionOperator.Equal, package.Id);
            string[] deployed = service.RetrieveAll(query)
                .Select(a => a.GetAttributeValue<string>("name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            string[] removed = deployed.Where(n => !localAssemblies.Contains(n)).ToArray();
            string[] added = payload.AssemblyName is { Length: > 0 } assembly && !deployed.Contains(assembly, StringComparer.OrdinalIgnoreCase)
                ? [assembly]
                : [];
            if (removed.Length == 0 && added.Length == 0)
            {
                continue;
            }

            problems.Add(DescribePackageAssemblySetChange(packageName, deployed, removed, added));
        }

        return problems;
    }

    private static string DescribePackageAssemblySetChange(string packageName, string[] deployed, string[] removed, string[] added)
    {
        string change = (removed.Length, added.Length) switch
        {
            (1, 1) => $"plugin assembly '{removed[0]}' is renamed to '{added[0]}'",
            (_, 0) => $"{Assemblies(removed)} no longer in the local package",
            (0, _) => $"{Assemblies(added)} added to it (the environment has {(deployed.Length == 0 ? "none" : Quote(deployed))})",
            _ => $"{Assemblies(removed)} no longer in the local package and {Assemblies(added)} added",
        };
        return $"Plugin package '{packageName}' changes its set of assemblies: {change}. Dataverse cannot apply that as a " +
            "package update: it refuses to remove or rename an assembly of a plugin package (0x80040265) and does not " +
            "register an assembly added to one. Either keep the assembly name the environment has (<AssemblyName> in the " +
            $"plugins project), or delete the steps registered on the package's plugin types, then plugin package '{packageName}', " +
            "and run 'vela apply' again: it creates the package and registers its steps and Custom APIs. Step secure " +
            "configurations are deleted with their steps; supply them again (--secure-config or VELA_SECURE_CONFIG_<NAME>).";
    }

    private static string Assemblies(string[] names) =>
        names.Length == 1 ? $"plugin assembly {Quote(names)} is" : $"plugin assemblies {Quote(names)} are";

    private static string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));
}
