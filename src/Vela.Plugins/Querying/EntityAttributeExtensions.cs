using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;

namespace Vela.Querying;

/// <summary>
/// Typed helpers for working with entity attributes without string literals.
/// </summary>
public static class EntityAttributeExtensions
{
    /// <summary>
    /// Determines whether the specified typed attribute is present in the entity attribute bag.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="entity">The entity instance.</param>
    /// <param name="attribute">The typed attribute selector.</param>
    /// <returns>True when the attribute is present in the payload.</returns>
    public static bool Contains<TEntity>(this TEntity entity, Expression<Func<TEntity, object?>> attribute)
        where TEntity : Entity
    {
        entity = entity ?? throw new ArgumentNullException(nameof(entity));
        return entity.Attributes.ContainsKey(GetAttributeLogicalName(attribute));
    }

    /// <summary>
    /// Determines whether all specified typed attributes are present in the entity attribute bag.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="entity">The entity instance.</param>
    /// <param name="attributes">The typed attribute selectors.</param>
    /// <returns>True when all requested attributes are present in the payload.</returns>
    public static bool Contains<TEntity>(this TEntity entity, params Expression<Func<TEntity, object?>>[] attributes)
        where TEntity : Entity
    {
        entity = entity ?? throw new ArgumentNullException(nameof(entity));
        attributes = attributes ?? throw new ArgumentNullException(nameof(attributes));

        if (attributes.Length == 0)
        {
            throw new ArgumentException("At least one attribute expression is required.", nameof(attributes));
        }

        foreach (Expression<Func<TEntity, object?>> attribute in attributes)
        {
            if (!entity.Attributes.ContainsKey(GetAttributeLogicalName(attribute)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether at least one of the specified typed attributes is present in the entity attribute bag.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="entity">The entity instance.</param>
    /// <param name="attributes">The typed attribute selectors.</param>
    /// <returns>True when any requested attribute is present in the payload.</returns>
    public static bool ContainsAny<TEntity>(this TEntity entity, params Expression<Func<TEntity, object?>>[] attributes)
        where TEntity : Entity
    {
        entity = entity ?? throw new ArgumentNullException(nameof(entity));
        attributes = attributes ?? throw new ArgumentNullException(nameof(attributes));

        if (attributes.Length == 0)
        {
            throw new ArgumentException("At least one attribute expression is required.", nameof(attributes));
        }

        foreach (Expression<Func<TEntity, object?>> attribute in attributes)
        {
            if (entity.Attributes.ContainsKey(GetAttributeLogicalName(attribute)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the formatted (display) value for the selected attribute — the choice label, lookup name,
    /// formatted money/date, etc. — or <c>null</c> when none was provided. Avoids the verbose
    /// <c>entity.FormattedValues.ContainsKey("x") ? entity.FormattedValues["x"] : null</c> dance.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="entity">The entity instance.</param>
    /// <param name="attribute">The typed attribute selector.</param>
    public static string? GetFormattedValue<TEntity>(this TEntity entity, Expression<Func<TEntity, object?>> attribute)
        where TEntity : Entity
    {
        entity = entity ?? throw new ArgumentNullException(nameof(entity));
        string name = GetAttributeLogicalName(attribute);
        return entity.FormattedValues.ContainsKey(name) ? entity.FormattedValues[name] : null;
    }

    private static string GetAttributeLogicalName<TEntity>(Expression<Func<TEntity, object?>> expression)
        where TEntity : Entity
        => Vela.Steps.ExpressionHelper.GetAttributeLogicalName(expression);
}
