using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;


namespace Vela.Cli;

/// <summary> The --managed-identity link of the published plugin packages: validation, link state and the link write. </summary>
internal static partial class DataverseRegistration
{
    // --managed-identity takes the id of a managedidentity record, and each environment has its own: check it exists
    // here before anything is planned or written. A query rather than a Retrieve, so a missing record reads as "not
    // found" whatever fault the service raises for an unknown id.
    internal static void EnsureManagedIdentityExists(IOrganizationService service, Guid managedIdentityId)
    {
        QueryExpression query = new QueryExpression("managedidentity")
        {
            ColumnSet = new ColumnSet("managedidentityid"),
            TopCount = 1,
        };
        query.Criteria.AddCondition("managedidentityid", ConditionOperator.Equal, managedIdentityId);
        if (service.RetrieveMultiple(query).Entities.Count == 0)
        {
            throw new InvalidOperationException(
                $"Managed identity not found: {managedIdentityId}. --managed-identity takes the id (managedidentityid) of a " +
                "managedidentity record in the target environment; create that record first.");
        }
    }

    // The link state of every plugin package the run publishes, against --managed-identity. Read-only: plan lists it,
    // apply writes the pending links after the publish and reads it again to check it converged. Same package name and
    // same create/skip decision as ComputePackagePublishActions / UpsertPluginPackage: a package absent from Dataverse is
    // created by apply, so it is to link; one without registrations is never created, so it is left out.
    internal static ManagedIdentityPackageLink[] ComputeManagedIdentityLinks(IOrganizationService service, PluginPackagePayload[] packagePayloads, string? solutionUniqueName, Guid managedIdentityId)
    {
        if (packagePayloads.Length == 0)
        {
            return [];
        }

        string? solutionPrefix = string.IsNullOrWhiteSpace(solutionUniqueName)
            ? null
            : ResolveSolutionCustomizationPrefix(service, solutionUniqueName);

        List<ManagedIdentityPackageLink> links = [];
        foreach (PluginPackagePayload package in packagePayloads)
        {
            string packageName = NormalizePackageNameWithPrefix(package.PackageName, solutionPrefix);
            Entity? existing = GetPluginPackage(service, packageName, withManagedIdentity: true);
            if (existing == null)
            {
                if (package.HasRegistrations)
                {
                    links.Add(new ManagedIdentityPackageLink(packageName, ManagedIdentityPackageLink.Link));
                }

                continue;
            }

            Guid? current = existing.GetAttributeValue<EntityReference>("managedidentityid")?.Id;
            links.Add(current == managedIdentityId
                ? new ManagedIdentityPackageLink(packageName, ManagedIdentityPackageLink.Linked)
                : current.HasValue
                    ? new ManagedIdentityPackageLink(packageName, ManagedIdentityPackageLink.Change, current)
                    : new ManagedIdentityPackageLink(packageName, ManagedIdentityPackageLink.Link));
        }

        return links.ToArray();
    }

    // Sets pluginpackage.managedidentityid, through the caller's writer (solution-scoped under --solution, like every other
    // write). The package is looked up again: one created by this apply had no id when the plan was computed.
    internal static void LinkPluginPackageToManagedIdentity(IOrganizationService writer, IOrganizationService reader, string packageName, Guid managedIdentityId, bool verbose)
    {
        Entity package = GetPluginPackage(reader, packageName)
            ?? throw new InvalidOperationException(
                $"Plugin package '{packageName}' was not found after the publish, so it cannot be linked to managed identity {managedIdentityId}.");

        Entity link = new Entity("pluginpackage", package.Id)
        {
            ["managedidentityid"] = new EntityReference("managedidentity", managedIdentityId),
        };
        writer.Update(link);
        LogOperation("package", "link", packageName, $"managed identity {managedIdentityId}", verbose);
    }

    internal static string DescribeManagedIdentityLink(string packageName, Guid managedIdentityId) =>
        $"link plugin package {packageName} to managed identity {managedIdentityId}";
}
