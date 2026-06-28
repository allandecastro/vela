using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;

namespace Vela.Testing
{
    /// <summary>
    /// Scalar value operations shared by <see cref="InMemoryOrganizationService"/> (Create/Update,
    /// alternate-key and auto-number handling) and <see cref="InMemoryQueryEngine"/> (filtering, ordering).
    /// Dataverse value wrappers are reduced to comparable scalars so equality and comparison behave intuitively.
    /// </summary>
    internal static class EntityValues
    {
        public static Entity Clone(Entity entity)
        {
            Entity clone = new Entity(entity.LogicalName) { Id = entity.Id };
            foreach (KeyValuePair<string, object> attribute in entity.Attributes)
            {
                // Deep-copy values so callers cannot mutate stored state by changing a retrieved/snapshotted
                // entity in place. Real Dataverse returns an independent (deserialized) copy on every read;
                // a shallow copy here would alias mutable wrappers (EntityCollection/partylists, byte[],
                // nested Entity) with the live store and break read isolation and transaction rollback.
                clone[attribute.Key] = CloneValue(attribute.Value);
            }

            return clone;
        }

        public static object? CloneValue(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case EntityReference reference:
                    EntityReference clonedReference = new EntityReference(reference.LogicalName, reference.Id) { Name = reference.Name };
                    foreach (KeyValuePair<string, object> key in reference.KeyAttributes)
                    {
                        // Deep-copy alternate-key values too, so a mutable key value (byte[], nested Entity)
                        // can't alias the stored reference and break read isolation.
                        clonedReference.KeyAttributes[key.Key] = CloneValue(key.Value)!;
                    }

                    return clonedReference;
                case Money money:
                    return new Money(money.Value);
                case OptionSetValue optionSet:
                    return new OptionSetValue(optionSet.Value);
                case OptionSetValueCollection optionSetCollection:
                    return new OptionSetValueCollection(optionSetCollection.Select(o => new OptionSetValue(o.Value)).ToList());
                case byte[] bytes:
                    return (byte[])bytes.Clone();
                case Entity nested:
                    return Clone(nested);
                case EntityCollection collection:
                    return new EntityCollection(collection.Entities.Select(Clone).ToList()) { EntityName = collection.EntityName };
                default:
                    // Strings, Guids, numbers, DateTime, bool, AliasedValue and other immutable scalars.
                    return value;
            }
        }

        public static object? GetValue(Entity entity, string attributeName)
        {
            if (entity.Contains(attributeName))
            {
                return entity[attributeName];
            }

            // The primary-key attribute ({logicalname}id) mirrors Entity.Id even when not set explicitly,
            // so joins/filters on the primary key behave like Dataverse.
            if (entity.Id != Guid.Empty && string.Equals(attributeName, entity.LogicalName + "id", StringComparison.OrdinalIgnoreCase))
            {
                return entity.Id;
            }

            return null;
        }

        // Delegates to the runtime's value-equality so query filtering/joins and change detection can't diverge.
        // (Also gains partylist/EntityCollection comparison for free — the standalone version fell back to a
        // type-name string compare that reported any two collections as equal.)
        public static bool AreEqual(object? actual, object? expected)
        {
            return AttributeValueComparer.AreEqual(actual, expected);
        }

        public static int Compare(object? actual, object? expected)
        {
            object? left = Normalize(actual);
            object? right = Normalize(expected);
            if (left is IComparable comparable && right != null)
            {
                return comparable.CompareTo(Convert.ChangeType(right, left.GetType()));
            }

            throw new NotSupportedException("InMemoryOrganizationService cannot compare the supplied values.");
        }

        // Reduce Dataverse value wrappers to their comparable scalar (shared with the runtime — single source).
        public static object? Normalize(object? value)
        {
            return AttributeValueComparer.Normalize(value);
        }

        public static string AsString(object? value)
        {
            return Normalize(value)?.ToString() ?? string.Empty;
        }
    }
}
