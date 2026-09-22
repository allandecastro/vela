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
    /// Fills the generated relationship navigation properties of an early-bound record from a retrieved entity's
    /// <see cref="Entity.RelatedEntities"/> (the reverse of <see cref="RelationshipProjector"/>): a 1:N collection
    /// nav becomes a typed list, an N:1 nav becomes the single typed parent. Backs relationship-aware reads
    /// (<c>Include(...)</c>). Each related row is converted with the SDK's <c>ToEntity&lt;T&gt;()</c>.
    /// </summary>
    public static class RelationshipHydrator
    {
        private static readonly MethodInfo ToEntityDefinition = typeof(Entity).GetMethod(nameof(Entity.ToEntity))!;
        private static readonly ConcurrentDictionary<Type, MethodInfo> ToEntityByType = new ConcurrentDictionary<Type, MethodInfo>();

        /// <summary>
        /// Copies related records from <paramref name="source"/>'s <see cref="Entity.RelatedEntities"/> onto the
        /// typed relationship navigation properties of <paramref name="target"/>. Only relationships present on the
        /// source are set; the rest keep their default (empty list / null). Returns <paramref name="target"/>.
        /// </summary>
        public static Entity Hydrate(Entity source, Entity target)
        {
            if (source == null || target == null)
            {
                return target!;
            }

            foreach (RelationshipNav nav in RelationshipNavMap.For(target.GetType()))
            {
                Relationship relationship = new Relationship(nav.SchemaName);
                if (!source.RelatedEntities.Contains(relationship))
                {
                    continue;
                }

                EntityCollection related = source.RelatedEntities[relationship];
                if (nav.IsCollection)
                {
                    IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(nav.ElementType))!;
                    foreach (Entity row in related.Entities)
                    {
                        list.Add(ToTyped(row, nav.ElementType));
                    }

                    nav.Property.SetValue(target, list);
                }
                else
                {
                    Entity? first = related.Entities.FirstOrDefault();
                    nav.Property.SetValue(target, first == null ? null : ToTyped(first, nav.ElementType));
                }
            }

            return target;
        }

        private static object ToTyped(Entity entity, Type elementType)
        {
            MethodInfo method = ToEntityByType.GetOrAdd(elementType, t => ToEntityDefinition.MakeGenericMethod(t));
            return method.Invoke(entity, null)!;
        }
    }
}
