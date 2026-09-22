using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        // Per type: the collection nav properties (property + relationship schema name), cached (plugins run hot).
        private static readonly ConcurrentDictionary<Type, KeyValuePair<PropertyInfo, string>[]> Maps = new();

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

            foreach (KeyValuePair<PropertyInfo, string> entry in GetMap(entity.GetType()))
            {
                if (entity.RelatedEntities.Contains(new Relationship(entry.Value)))
                {
                    continue;
                }

                if (entry.Key.GetValue(entity) is not IEnumerable value || value is string)
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
                    entity.RelatedEntities[new Relationship(entry.Value)] = new EntityCollection(children);
                }
            }

            return entity;
        }

        private static KeyValuePair<PropertyInfo, string>[] GetMap(Type type) =>
            Maps.GetOrAdd(type, static t =>
            {
                List<KeyValuePair<PropertyInfo, string>> map = new List<KeyValuePair<PropertyInfo, string>>();
                foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    // Only 1:N/N:N collection navs deep-insert children; a single N:1 parent is not projected here.
                    if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType) || property.PropertyType == typeof(string))
                    {
                        continue;
                    }

                    string? schemaName = GetRelationshipSchemaName(property);
                    if (!string.IsNullOrEmpty(schemaName))
                    {
                        map.Add(new KeyValuePair<PropertyInfo, string>(property, schemaName!));
                    }
                }

                return map.ToArray();
            });

        // The generated [RelationshipSchemaName(...)] attribute lives in the consumer's namespace, so it is matched
        // by name and its SchemaName is read reflectively (no type reference to generated code).
        private static string? GetRelationshipSchemaName(PropertyInfo property)
        {
            Attribute? attribute = property.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name == "RelationshipSchemaNameAttribute");
            return attribute?.GetType().GetProperty("SchemaName")?.GetValue(attribute) as string;
        }
    }
}
