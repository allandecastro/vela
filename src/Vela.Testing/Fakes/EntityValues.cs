using System;
using System.Collections.Generic;
using System.Globalization;
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

        // A request target as the platform receives it: an independent copy of what the caller sent (values, alternate
        // key, deep-insert related records, row version), so nothing the pipeline or a plugin does to it - setting the
        // created id, changing an attribute in PreOperation - is visible on the caller's object afterwards.
        public static Entity CloneTarget(Entity entity)
        {
            Entity clone = Clone(entity);
            clone.RowVersion = entity.RowVersion;
            foreach (KeyValuePair<string, object> key in entity.KeyAttributes)
            {
                clone.KeyAttributes[key.Key] = CloneValue(key.Value)!;
            }

            foreach (KeyValuePair<Relationship, EntityCollection> related in entity.RelatedEntities)
            {
                clone.RelatedEntities[related.Key] = new EntityCollection(related.Value.Entities.Select(CloneTarget).ToList())
                {
                    EntityName = related.Value.EntityName,
                };
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

        // Query equality for a condition operand: a text operand (every FetchXML <value>) is first read as the
        // column's type, so eq/ne/in on a date, lookup, money, number or boolean column match as Dataverse does.
        public static bool ConditionEquals(object? actual, object? operand)
        {
            return AreEqual(actual, CoerceOperand(actual, operand));
        }

        public static int Compare(object? actual, object? expected)
        {
            object? left = Normalize(actual);
            object? right = Normalize(CoerceOperand(actual, expected));
            if (left is IComparable comparable && right != null)
            {
                return comparable.CompareTo(left.GetType().IsInstanceOfType(right)
                    ? right
                    : Convert.ChangeType(right, left.GetType(), CultureInfo.InvariantCulture));
            }

            throw new NotSupportedException("InMemoryOrganizationService cannot compare the supplied values.");
        }

        // Reads a text operand as the CLR type of the stored value it is compared with, using the invariant culture
        // (FetchXML values are culture-neutral: "2026-10-09", "10.50", "{guid}", "1"). A typed operand, a text
        // column or a null stored value is returned unchanged. Text that is not a valid value of the column's type
        // fails, as Dataverse rejects the query, rather than silently matching nothing (or everything, for ne).
        public static object? CoerceOperand(object? actual, object? operand)
        {
            object? stored = Normalize(actual);
            if (operand is not string text || stored == null || stored is string)
            {
                return operand;
            }

            Type type = stored.GetType();
            if (TryParse(text.Trim(), type, out object? parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException(
                $"The query value '{text}' is not a valid {type.Name} for the column it is compared with.");
        }

        private static bool TryParse(string text, Type type, out object? value)
        {
            value = null;
            if (type == typeof(Guid))
            {
                bool ok = Guid.TryParse(text, out Guid guid);
                value = guid;
                return ok;
            }

            if (type == typeof(DateTime))
            {
                // Dataverse stores date-times in UTC; a value with no offset is read as UTC, one with an offset is
                // converted to it.
                bool ok = DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime date);
                value = date;
                return ok;
            }

            if (type == typeof(bool))
            {
                switch (text.ToLowerInvariant())
                {
                    case "1":
                    case "true":
                        value = true;
                        return true;
                    case "0":
                    case "false":
                        value = false;
                        return true;
                    default:
                        return false;
                }
            }

            if (type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(double))
            {
                try
                {
                    value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }

            // Any other stored type keeps the text operand (compared through the invariant string fallback).
            value = text;
            return true;
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
