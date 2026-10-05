using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace Vela.Querying;

/// <summary>
/// Typed helper for the "duplicate a record" pattern: produce a detached copy of an entity that is ready to be
/// created as a brand-new row.
/// </summary>
public static class EntityCloneExtensions
{
    // System-assigned columns that Dataverse rejects (or ignores) on Create. They are dropped from the copy so the
    // clone is createable; business columns (including ownerid / statecode / statuscode) are preserved verbatim.
    private static readonly HashSet<string> NonCreatableAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "createdon", "createdby", "createdonbehalfby",
        "modifiedon", "modifiedby", "modifiedonbehalfby",
        "versionnumber",
    };

    /// <summary>
    /// Creates a detached copy of <paramref name="source"/> ready to pass to <c>Create</c> as a new record.
    /// The copy's <see cref="Entity.Id"/> is left empty and every link to the source row is dropped
    /// (<see cref="Entity.EntityState"/>, <see cref="Entity.RowVersion"/>, <see cref="Entity.KeyAttributes"/>,
    /// <see cref="Entity.FormattedValues"/>, <see cref="Entity.RelatedEntities"/>, the primary-key attribute and
    /// the system-assigned audit columns). All remaining attribute values are deep-copied, so mutating the clone
    /// never affects the original. Business columns such as <c>ownerid</c> and <c>statecode</c> are preserved;
    /// clear any your table rejects on create before calling <c>Create</c>.
    /// </summary>
    /// <typeparam name="TEntity">The entity type (early-bound model or late-bound <see cref="Entity"/>).</typeparam>
    /// <param name="source">The record to duplicate.</param>
    /// <returns>A new, create-ready copy of <paramref name="source"/>.</returns>
    public static TEntity Clone<TEntity>(this TEntity source)
        where TEntity : Entity, new()
    {
        source = source ?? throw new ArgumentNullException(nameof(source));

        TEntity clone = new TEntity();

        // A late-bound Entity has no logical name until it is set; carry the source's so the copy stays addressable.
        // An early-bound model already sets it in its constructor, so this is a no-op there.
        if (string.IsNullOrEmpty(clone.LogicalName))
        {
            clone.LogicalName = source.LogicalName;
        }

        string primaryKeyAttribute = source.LogicalName + "id";
        foreach (KeyValuePair<string, object> attribute in source.Attributes)
        {
            if (string.Equals(attribute.Key, primaryKeyAttribute, StringComparison.OrdinalIgnoreCase)
                || NonCreatableAttributes.Contains(attribute.Key))
            {
                continue;
            }

            clone[attribute.Key] = CloneValue(attribute.Value);
        }

        return clone;
    }

    // Deep-copies a single attribute value. Mutable Dataverse wrappers (EntityReference, Money, option sets,
    // byte[], nested entities and collections) are rebuilt; immutable scalars are passed through. Mirrors the
    // harness' read-isolation clone so the semantics match what a real org round-trip would produce.
    private static object? CloneValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case EntityReference reference:
                EntityReference clonedReference = new EntityReference(reference.LogicalName, reference.Id) { Name = reference.Name };
                foreach (KeyValuePair<string, object> key in reference.KeyAttributes)
                {
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
                return CloneNested(nested);
            case EntityCollection collection:
                return new EntityCollection(collection.Entities.Select(CloneNested).ToList()) { EntityName = collection.EntityName };
            default:
                // Strings, Guids, numbers, DateTime, bool and other immutable scalars.
                return value;
        }
    }

    // A nested record in a duplicated graph (e.g. an activity partylist entry) is itself a new row: deep-copy its
    // attributes but drop its Id so Dataverse assigns a fresh one.
    private static Entity CloneNested(Entity source)
    {
        Entity clone = new Entity(source.LogicalName);
        foreach (KeyValuePair<string, object> attribute in source.Attributes)
        {
            clone[attribute.Key] = CloneValue(attribute.Value);
        }

        return clone;
    }
}
