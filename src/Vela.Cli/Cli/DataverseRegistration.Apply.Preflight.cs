using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;


namespace Vela.Cli;

/// <summary> The checks that run before apply writes anything: package importability, an unmanaged target solution, the publisher prefix and cross-solution assembly collisions. </summary>
internal static partial class DataverseRegistration
{
    // Dataverse rejects a plugin-package import whose nuget name has no solution publisher prefix (0x80040265,
    // "The nuget file name does not contain a solution prefix"). Without a --solution to source that prefix, a
    // package whose name isn't already prefixed can't import — fail fast with a clear message instead. Shared by
    // apply (before writing) and plan (so the preview never advertises a create apply would refuse); purely local,
    // so plan can call it without a connection.
    internal static void EnsurePluginPackagesImportable(PluginPackagePayload[] packagePayloads, string? solutionUniqueName)
{
    if (!string.IsNullOrWhiteSpace(solutionUniqueName))
    {
        return;
    }

    // A package without registrations is never created, so its name cannot block the run (see ComputePackagePublishActions).
    PluginPackagePayload? unprefixed = packagePayloads.FirstOrDefault(p => p.HasRegistrations && !LooksSolutionPrefixed(p.PackageName));
    if (unprefixed != null)
    {
        throw new InvalidOperationException(
            $"Plugin package '{unprefixed.PackageName}' has no solution publisher prefix, which Dataverse requires " +
            "to import a package. Re-run 'apply' with --solution <uniqueName> so the prefix is applied.");
    }
}

    // A package name already carries a solution publisher prefix when it starts with "<prefix>_" and <prefix> is a
    // plausible Dataverse customization prefix (2-8 letters/digits, letter-first). Used to decide whether importing
    // without a --solution can possibly succeed.
    internal static bool LooksSolutionPrefixed(string packageName)
{
    int underscore = packageName.IndexOf('_');
    if (underscore < 2)
    {
        return false;
    }

    string prefix = packageName.Substring(0, underscore);
    return prefix.Length >= 2 && prefix.Length <= 8 && char.IsLetter(prefix[0]) && prefix.All(char.IsLetterOrDigit);
}

    // plan and apply deploy into an unmanaged solution of a development environment. Dataverse does not let a component
    // be added to a managed solution (Learn: "Solutions in Power Apps", AddSolutionComponent "adds a solution component
    // to an unmanaged solution"), so a --solution that is managed in the target environment means the run points at
    // the wrong environment (a test or production one where the solution was imported managed). Checked once, after
    // connecting and before anything is read or written; a missing solution fails here too rather than mid-apply.
    internal static void EnsureSolutionIsUnmanaged(IOrganizationService service, string solutionUniqueName, string? environment)
    {
        QueryExpression query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("ismanaged"),
            TopCount = 1,
        };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);

        Entity solution = service.RetrieveMultiple(query).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution not found: {solutionUniqueName}");
        if (solution.GetAttributeValue<bool>("ismanaged"))
        {
            throw new InvalidOperationException(
                $"Solution '{solutionUniqueName}' is managed in {environment ?? "this environment"}. Vela deploys to the " +
                "unmanaged solution in a development environment: check that the connection points at the development " +
                "environment, not a test or production one where the solution was imported managed. Nothing was written.");
        }
    }

    // The publisher prefix of each solution, per connection: plan and apply need it in the preview (package actions,
    // retired assemblies, solution membership, Custom API names), the publish and the read-back, and it does not change
    // during a run. Keyed by the connection (a solution-scoped wrapper shares its inner service's entry), so a new
    // connection reads it again. A failed lookup is not cached.
    private static readonly ConditionalWeakTable<IOrganizationService, Dictionary<string, string>> SolutionPrefixes = new();

    internal static string ResolveSolutionCustomizationPrefix(IOrganizationService service, string solutionUniqueName)
    {
        IOrganizationService connection = service is SolutionScopedOrganizationService scoped ? scoped.Inner : service;
        Dictionary<string, string> prefixes = SolutionPrefixes.GetValue(connection, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        lock (prefixes)
        {
            if (!prefixes.TryGetValue(solutionUniqueName, out string? prefix))
            {
                prefix = ReadSolutionCustomizationPrefix(connection, solutionUniqueName);
                prefixes[solutionUniqueName] = prefix;
            }

            return prefix;
        }
    }

    private static string ReadSolutionCustomizationPrefix(IOrganizationService service, string solutionUniqueName)
{
    QueryExpression query = new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("publisherid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);

    Entity? solution = service.RetrieveMultiple(query).Entities.FirstOrDefault();
    if (solution == null)
    {
        throw new InvalidOperationException($"Solution not found: {solutionUniqueName}");
    }

    Guid publisherId = solution.GetAttributeValue<EntityReference>("publisherid")?.Id ?? Guid.Empty;
    if (publisherId == Guid.Empty)
    {
        throw new InvalidOperationException($"Publisher not found for solution: {solutionUniqueName}");
    }

    Entity publisher = service.Retrieve("publisher", publisherId, new ColumnSet("customizationprefix"));
    string? prefix = publisher.GetAttributeValue<string>("customizationprefix");
    if (string.IsNullOrWhiteSpace(prefix))
    {
        throw new InvalidOperationException($"Customization prefix not found for solution: {solutionUniqueName}");
    }

    return prefix.Trim();
}

    // Returns the names of source assemblies that already exist in the org but are NOT part of the target solution.
    // The registered pluginassembly name is the DLL's assembly name (unique per org), so two projects that build the
    // same-named assembly collide: applying one rewrites the other's content. This is the signal for that collision -
    // an assembly that exists and belongs elsewhere. Pure over the service, so it is testable with a fake org.
    internal static List<string> FindCrossSolutionAssemblyCollisions(IOrganizationService service, AssemblyPayload[] payloads, string solutionUniqueName)
    {
        List<string> collisions = new List<string>();
        foreach (AssemblyPayload payload in payloads)
        {
            Entity? existing = GetPluginAssembly(service, payload.AssemblyName);
            if (existing != null && !IsAssemblyInSolution(service, existing.Id, solutionUniqueName))
            {
                collisions.Add(payload.AssemblyName);
            }
        }

        return collisions;
    }

    private static bool IsAssemblyInSolution(IOrganizationService service, Guid assemblyId, string solutionUniqueName)
    {
        QueryExpression query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("solutioncomponentid"),
            TopCount = 1,
        };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, assemblyId);
        LinkEntity solution = query.AddLink("solution", "solutionid", "solutionid");
        solution.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);
        return service.RetrieveMultiple(query).Entities.Count > 0;
    }
}
