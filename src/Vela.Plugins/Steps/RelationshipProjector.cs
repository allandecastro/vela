using System.Collections;
using System.Collections.Generic;
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
                        PopulateRelatedEntities(child);
                        children.Add(child);
                    }
                }

                if (children.Count > 0)
                {
                    entity.RelatedEntities[new Relationship(nav.SchemaName)] = new EntityCollection(children);
                }
            }

            return entity;
        }
    }
}
