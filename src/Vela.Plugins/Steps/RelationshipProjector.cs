using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Vela.Steps
{
    /// <summary>
    /// Populates <see cref="Entity.RelatedEntities"/> from the generated 1:N relationship navigation properties (the
    /// ones carrying <c>[RelationshipSchemaName(...)]</c>) so a typed object graph can be written as a single deep
    /// insert. Mirrors the escape-hatch pattern (<c>entity.RelatedEntities[new Relationship(schema)] = new
    /// EntityCollection(children)</c>) but keyed off the typed collections instead of hand-written schema strings.
    /// Runs recursively so multi-level graphs project in one pass (the platform bounds the actual insert depth).
    /// </summary>
    public static class RelationshipProjector
    {
        /// <summary>
        /// Moves each populated 1:N navigation collection on <paramref name="entity"/> into
        /// <see cref="Entity.RelatedEntities"/>, recursing into the children. A relationship already present in
        /// <see cref="Entity.RelatedEntities"/> is left untouched (an explicit assignment wins). Empty collections
        /// are skipped. Returns the same instance for chaining.
        /// </summary>
        public static Entity PopulateRelatedEntities(Entity entity)
        {
            if (entity == null)
            {
                return entity!;
            }

            PopulateRelatedEntities(entity, new HashSet<Entity>(ReferenceComparer.Instance));
            return entity;
        }

        // The visited set (reference equality) breaks cycles: a graph where A holds B and B holds A back would
        // otherwise recurse forever and tear down the sandbox with an uncatchable StackOverflowException. A node
        // already being projected is added to its parent's collection but not descended into again.
        private static void PopulateRelatedEntities(Entity entity, HashSet<Entity> visited)
        {
            if (!visited.Add(entity))
            {
                return;
            }

            foreach (RelationshipNav nav in RelationshipNavMap.For(entity.GetType()))
            {
                // Only 1:N child collections deep-insert. A single N:1 parent is not projected, and a N:N collection
                // associates existing records (see AssociateRelated) rather than creating them, so both are skipped.
                if (!nav.IsCollection || nav.IsManyToMany || entity.RelatedEntities.Contains(new Relationship(nav.SchemaName)))
                {
                    continue;
                }

                if (nav.Property.GetValue(entity) is not IEnumerable value || value is string)
                {
                    continue;
                }

                List<Entity> children = new List<Entity>();
                foreach (object? item in value)
                {
                    if (item is Entity child)
                    {
                        PopulateRelatedEntities(child, visited);
                        children.Add(child);
                    }
                }

                if (children.Count > 0)
                {
                    entity.RelatedEntities[new Relationship(nav.SchemaName)] = new EntityCollection(children);
                }
            }
        }

        // Reference-equality set membership (net462 has no ReferenceEqualityComparer).
        private sealed class ReferenceComparer : IEqualityComparer<Entity>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(Entity x, Entity y) => ReferenceEquals(x, y);

            public int GetHashCode(Entity obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
