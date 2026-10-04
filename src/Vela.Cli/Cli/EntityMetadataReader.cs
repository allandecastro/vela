using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Reads table metadata and solution membership from the live org for the <c>vela models</c> scopes: resolving the
/// scoped logical names (<c>--solution</c> components plus explicit <c>--entities</c>), reading a table's metadata,
/// and building the logical-name to early-bound type-name map the message/custom-api generators need. Split out of
/// <see cref="ModelsCommand"/> so the metadata-reading responsibility stands on its own.
/// </summary>
internal static class EntityMetadataReader
{
    internal static List<string> ResolveLogicalNames(IOrganizationService service, string? solution, string[] entities)
    {
        SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entity in entities)
        {
            names.Add(entity);
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            foreach (string name in EntitiesInSolution(service, solution!))
            {
                names.Add(name);
            }
        }

        return names.ToList();
    }

    // Collects the N:N join (intersect) table logical names reachable from the given entities' many-to-many
    // relationships, skipping any already in <paramref name="exclude"/> and de-duplicating. One level only: the
    // intersects of these entities, not transitively the intersects of those intersects.
    internal static List<string> CollectIntersectNames(IEnumerable<EntityMetadata> entities, ISet<string> exclude)
    {
        List<string> names = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EntityMetadata entity in entities)
        {
            if (entity.ManyToManyRelationships is null)
            {
                continue;
            }

            foreach (ManyToManyRelationshipMetadata relationship in entity.ManyToManyRelationships)
            {
                string? intersect = relationship.IntersectEntityName;
                if (string.IsNullOrEmpty(intersect) || exclude.Contains(intersect!) || !seen.Add(intersect!))
                {
                    continue;
                }

                names.Add(intersect!);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // Derives the table list from a solution's components (componenttype 1 = Entity), mapping the component
    // object ids to logical names via a lightweight RetrieveAllEntities (entity filter only).
    private static IEnumerable<string> EntitiesInSolution(IOrganizationService service, string uniqueName)
    {
        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity? solution = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 1);
        HashSet<Guid> objectIds = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .ToHashSet();

        if (objectIds.Count == 0)
        {
            return Enumerable.Empty<string>();
        }

        RetrieveAllEntitiesResponse all = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });
        return all.EntityMetadata
            .Where(m => m.MetadataId.HasValue && objectIds.Contains(m.MetadataId.Value) && !string.IsNullOrWhiteSpace(m.LogicalName))
            .Select(m => m.LogicalName)
            .ToList();
    }

    internal static EntityMetadata ReadEntity(IOrganizationService service, string logicalName)
    {
        RetrieveEntityResponse response = (RetrieveEntityResponse)service.Execute(
            new RetrieveEntityRequest { LogicalName = logicalName, EntityFilters = EntityFilters.Attributes | EntityFilters.Entity | EntityFilters.Relationships });
        return response.EntityMetadata;
    }

    // Maps every entity's logical name to the early-bound C# type name the model generator emits for it (its
    // SchemaName, escaped the same way as ModelGenerator.ClassName). One lightweight RetrieveAllEntities (Entity
    // filter only). Keys are case-insensitive because sdkmessagefilter.primaryobjecttypecode /
    // customapi.boundentitylogicalname casing is not guaranteed. Used to wire generated On<Message>() step
    // extensions to the exact model type (e.g. new_CustomEntity, not NewCustomentity).
    internal static IReadOnlyDictionary<string, string> BuildEntityTypeNameMap(IOrganizationService service)
    {
        RetrieveAllEntitiesResponse all = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });

        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (EntityMetadata entity in all.EntityMetadata)
        {
            if (string.IsNullOrWhiteSpace(entity.LogicalName))
            {
                continue;
            }

            string typeName = !string.IsNullOrWhiteSpace(entity.SchemaName)
                ? entity.SchemaName
                : char.ToUpperInvariant(entity.LogicalName[0]) + entity.LogicalName.Substring(1);
            map[entity.LogicalName] = CSharpIdentifier.EscapeKeyword(typeName);
        }

        return map;
    }
}
