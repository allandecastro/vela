using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.StepBuilder;

/// <summary>
/// Helper for extracting attribute names from lambda expressions.
/// </summary>
internal static class ExpressionHelper
{
    /// <summary>
    /// Extracts the property name from a member expression.
    /// Handles both direct property access and unary conversions.
    /// </summary>
    public static string GetAttributeName<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        var member = GetMemberExpression(expression) ?? throw new ArgumentException($"Expression must be a property access: {expression}", nameof(expression));

        // Get the AttributeLogicalNameAttribute if present, otherwise use the property name
        var property = member.Member as PropertyInfo;
        if (property != null)
        {
            var attribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
            if (attribute != null)
            {
                return attribute.LogicalName;
            }
        }

        // Fallback to lowercase property name (convention)
        return member.Member.Name.ToLowerInvariant();
    }

    /// <summary>
    /// Extracts multiple attribute names from an array of expressions.
    /// </summary>
    public static string[] GetAttributeNames<TEntity>(Expression<Func<TEntity, object?>>[] expressions)
    {
        return expressions.Select(GetAttributeName).ToArray();
    }

    /// <summary>
    /// Resolves the Dataverse logical name for the attribute targeted by the expression.
    /// Unlike <see cref="GetAttributeName{TEntity}"/>, this requires the member to be a property
    /// and throws <see cref="ArgumentException"/> with the "Expression must target a property."
    /// message otherwise (used by the typed querying helpers).
    /// </summary>
    public static string GetAttributeLogicalName<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        expression = expression ?? throw new ArgumentNullException(nameof(expression));

        Expression body = expression.Body;
        if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member || member.Member is not PropertyInfo property)
        {
            throw new ArgumentException("Expression must target a property.", nameof(expression));
        }

        var attribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
        return attribute?.LogicalName ?? property.Name.ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the Dataverse logical name for an entity type, preferring its
    /// <see cref="EntityLogicalNameAttribute"/> and falling back to the lowercased type name.
    /// </summary>
    public static string GetEntityLogicalName<TEntity>()
    {
        var attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
        return attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant();
    }

    /// <summary>
    /// Gets the custom message name for a typed message marker.
    /// </summary>
    /// <typeparam name="TMessage">The message marker type.</typeparam>
    /// <returns>The message name.</returns>
    public static string GetMessageName<TMessage>() where TMessage : class
    {
        var attribute = typeof(TMessage).GetCustomAttribute<CustomMessageAttribute>();
        if (attribute != null)
        {
            return attribute.MessageName;
        }

        return typeof(TMessage).Name;
    }

    private static MemberExpression? GetMemberExpression<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        var body = expression.Body;

        // Handle Convert/UnboxAny for value types
        if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        return body as MemberExpression;
    }
}
