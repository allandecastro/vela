using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Vela.Steps
{
    /// <summary>
    /// One generated relationship navigation property: the CLR property, its Dataverse relationship schema name,
    /// whether it is a 1:N/N:N collection or a single N:1 parent, and the related entity CLR type.
    /// </summary>
    internal readonly struct RelationshipNav
    {
        public RelationshipNav(PropertyInfo property, string schemaName, bool isCollection, bool isManyToMany, Type elementType, bool isHydratable)
        {
            Property = property;
            SchemaName = schemaName;
            IsCollection = isCollection;
            IsManyToMany = isManyToMany;
            ElementType = elementType;
            IsHydratable = isHydratable;
        }

        public PropertyInfo Property { get; }
        public string SchemaName { get; }
        public bool IsCollection { get; }
        public bool IsManyToMany { get; }
        public Type ElementType { get; }

        // True when the read hydrator can assign to this nav: a settable property whose type accepts the value the
        // hydrator produces (a List&lt;ElementType&gt; for a collection, or an ElementType instance for a parent). A
        // hand-edited model with an array or a getter-only nav is false and is skipped instead of throwing.
        public bool IsHydratable { get; }
    }

    /// <summary>
    /// Reflects the generated relationship navigation properties of an early-bound type (those carrying
    /// <c>[RelationshipSchemaName(...)]</c>), cached per type. Shared by the deep-insert projector (write) and the
    /// include hydrator (read) so schema-name resolution lives in one place. The attribute is matched by name (it is
    /// generated per consumer namespace, so there is no type to reference).
    /// </summary>
    internal static class RelationshipNavMap
    {
        private static readonly ConcurrentDictionary<Type, RelationshipNav[]> Maps = new ConcurrentDictionary<Type, RelationshipNav[]>();

        public static RelationshipNav[] For(Type type) => Maps.GetOrAdd(type, Build);

        // The [RelationshipSchemaName] value on a property, or null when it is not a relationship nav.
        public static string? GetSchemaName(PropertyInfo property)
        {
            Attribute? attribute = GetAttribute(property);
            return attribute?.GetType().GetProperty("SchemaName")?.GetValue(attribute) as string;
        }

        // Matched by simple name because the generated attribute lives in the consumer's namespace. The SDK's own
        // Microsoft.Xrm.Sdk.RelationshipSchemaNameAttribute (emitted by pac modelbuilder) shares that name, so it is
        // excluded - a pac-modelbuilder model mixed with Vela is never mistaken for a Vela relationship nav.
        private static Attribute? GetAttribute(PropertyInfo property) =>
            property.GetCustomAttributes().FirstOrDefault(a =>
                a.GetType().Name == "RelationshipSchemaNameAttribute" && a.GetType().Namespace != "Microsoft.Xrm.Sdk");

        private static RelationshipNav[] Build(Type type)
        {
            List<RelationshipNav> navs = new List<RelationshipNav>();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Attribute? attribute = GetAttribute(property);
                if (attribute?.GetType().GetProperty("SchemaName")?.GetValue(attribute) is not string schemaName ||
                    string.IsNullOrEmpty(schemaName))
                {
                    continue;
                }

                bool isManyToMany = attribute.GetType().GetProperty("ManyToMany")?.GetValue(attribute) is bool m && m;
                bool isCollection = property.PropertyType != typeof(string) &&
                    typeof(IEnumerable).IsAssignableFrom(property.PropertyType);
                Type elementType = isCollection ? ElementTypeOf(property.PropertyType) : property.PropertyType;
                bool isHydratable = property.CanWrite &&
                    (!isCollection || property.PropertyType.IsAssignableFrom(typeof(List<>).MakeGenericType(elementType)));
                navs.Add(new RelationshipNav(property, schemaName, isCollection, isManyToMany, elementType, isHydratable));
            }

            return navs.ToArray();
        }

        // The T of an IList<T>/IEnumerable<T> nav collection; falls back to the property type itself if not generic.
        private static Type ElementTypeOf(Type collectionType)
        {
            if (collectionType.IsGenericType)
            {
                Type[] args = collectionType.GetGenericArguments();
                if (args.Length == 1)
                {
                    return args[0];
                }
            }

            return collectionType;
        }
    }
}
