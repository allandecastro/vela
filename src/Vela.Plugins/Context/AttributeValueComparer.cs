using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// The single source of Dataverse attribute value-equality, shared by the runtime (change detection /
    /// <c>HasChanged</c>) and the in-memory test harness (<c>EntityValues</c>, used by the query engine), so the
    /// two can't diverge: option sets compare by value, lookups by id, money by amount, partylists by their set
    /// of party references; everything else by value (with a tolerant string fallback).
    /// </summary>
    internal static class AttributeValueComparer
    {
        public static bool AreEqual(object? actual, object? expected)
        {
            object? left = Normalize(actual);
            object? right = Normalize(expected);
            if (Equals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            // EntityCollection (e.g. ActivityParty partylists) has no value-equality and no meaningful
            // ToString(): falling through to the string fallback would compare the type name for both sides
            // and report any two collections as equal, so HasChanged would never fire. Compare by members.
            if (left is EntityCollection || right is EntityCollection)
            {
                return left is EntityCollection leftCollection
                    && right is EntityCollection rightCollection
                    && CollectionsEqual(leftCollection, rightCollection);
            }

            return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool CollectionsEqual(EntityCollection left, EntityCollection right)
        {
            if (left.Entities.Count != right.Entities.Count)
            {
                return false;
            }

            // Order-independent: compare the multiset of member identities. Partylist rows carry the actual
            // party in a "partyid" lookup; fall back to the row's own (logicalname, id) when absent.
            List<string> leftKeys = left.Entities.Select(PartyKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
            List<string> rightKeys = right.Entities.Select(PartyKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return leftKeys.SequenceEqual(rightKeys, StringComparer.Ordinal);
        }

        private static string PartyKey(Entity party)
        {
            EntityReference? reference = party?.GetAttributeValue<EntityReference>("partyid");
            if (reference != null)
            {
                return $"{reference.LogicalName}:{reference.Id}";
            }

            return party == null ? string.Empty : $"{party.LogicalName}:{party.Id}";
        }

        // Reduce Dataverse value wrappers to their comparable scalar (shared with the harness via EntityValues).
        internal static object? Normalize(object? value)
        {
            switch (value)
            {
                case OptionSetValue optionSet:
                    return optionSet.Value;
                case EntityReference reference:
                    return reference.Id;
                case Money money:
                    return money.Value;
                default:
                    return value;
            }
        }
    }
}
