using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.Steps;

/// <summary>
/// Helper for extracting attribute names from lambda expressions.
/// </summary>
internal static class ExpressionHelper
{
    /// <summary>
    /// Resolves the Dataverse column a selector targets (<c>a =&gt; a.Name</c>): its <c>[AttributeLogicalName]</c>,
    /// or the lowercased property name. Used by FilterOn, the image selectors and the typed query helpers.
    /// </summary>
    public static string GetAttributeName<TEntity>(Expression<Func<TEntity, object?>> expression) => ResolveColumn(expression);

    /// <summary>
    /// Extracts multiple attribute names from an array of expressions.
    /// </summary>
    public static string[] GetAttributeNames<TEntity>(Expression<Func<TEntity, object?>>[] expressions)
    {
        return expressions.Select(GetAttributeName).ToArray();
    }

    /// <summary>Same as <see cref="GetAttributeName{TEntity}"/> (kept for the typed querying helpers).</summary>
    public static string GetAttributeLogicalName<TEntity>(Expression<Func<TEntity, object?>> expression) => ResolveColumn(expression);

    /// <summary>
    /// Resolves the Dataverse logical name for the attribute targeted by a lambda whose body is a property
    /// access, regardless of the lambda's return type. This lets the typed aggregate helpers accept a
    /// strongly-typed column selector (for example <c>x =&gt; x.createdon</c> typed <see cref="DateTime"/> or
    /// <c>x =&gt; x.amount</c> typed <see cref="Money"/>) rather than forcing an <c>object</c> boxing selector.
    /// </summary>
    public static string GetAttributeLogicalName(LambdaExpression expression) => ResolveColumn(expression);

    // The single column resolver. The selector must read a property of the lambda parameter directly: a nested path
    // (a.ParentAccount.Name, a.PrimaryContactId.Id) would otherwise resolve to the last member's name ("name", "id"),
    // a wrong or nonexistent column, and a base Entity member (Id, LogicalName, ...) without a logical name is not a
    // column at all. Both fail here instead of registering the wrong column.
    private static string ResolveColumn(LambdaExpression expression)
    {
        expression = expression ?? throw new ArgumentNullException(nameof(expression));

        Expression body = expression.Body;
        while (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked || unary.NodeType == ExpressionType.TypeAs))
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member || member.Member is not PropertyInfo property)
        {
            throw new ArgumentException($"A column selector must target a property, e.g. a => a.Name: {expression}", nameof(expression));
        }

        if (member.Expression is not ParameterExpression parameter)
        {
            throw new ArgumentException(
                $"A column selector must read a column of the entity itself, e.g. a => a.Name, not a nested path: {expression}. For a lookup, select the lookup column (a => a.PrimaryContactId).",
                nameof(expression));
        }

        // An expression tree references an overridden property by its base declaration (c => c.Id is Entity.Id), so
        // read the attributes off the most-derived declaration on the selector's entity type.
        property = MostDerived(parameter.Type, property);
        EnsureNotRelationship(property);
        var attribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
        if (attribute != null)
        {
            return attribute.LogicalName;
        }

        if (property.DeclaringType == typeof(Entity) || property.GetGetMethod()?.GetBaseDefinition().DeclaringType == typeof(Entity))
        {
            throw new ArgumentException(
                $"'{property.Name}' is a member of Entity, not a Dataverse column: {expression}. Select a column property (for the primary key, e.g. a => a.AccountId).",
                nameof(expression));
        }

        return property.Name.ToLowerInvariant();
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
    /// Resolves the message name for a typed message type. When <typeparamref name="TMessage"/> is an
    /// <see cref="OrganizationRequest"/>-derived type with a public parameterless constructor (an SDK request,
    /// a generated Custom API request, or a <c>TypedRequest</c>), the name is taken from its
    /// <see cref="OrganizationRequest.RequestName"/> - so <c>On&lt;XxxRequest&gt;()</c> needs no magic string. It
    /// falls back to a <see cref="CustomMessageAttribute"/> marker, then a <c>[SdkMessage]</c>. A type with none throws: guessing the name
    /// from the type would register the step on a message that most likely does not exist.
    /// </summary>
    /// <typeparam name="TMessage">The message type (a request type, or a marker type).</typeparam>
    /// <returns>The message name.</returns>
    public static string GetMessageName<TMessage>() where TMessage : class
    {
        if (typeof(OrganizationRequest).IsAssignableFrom(typeof(TMessage))
            && typeof(TMessage).GetConstructor(Type.EmptyTypes) != null)
        {
            var request = (OrganizationRequest)Activator.CreateInstance(typeof(TMessage))!;
            if (!string.IsNullOrWhiteSpace(request.RequestName))
            {
                return request.RequestName;
            }
        }

        var attribute = typeof(TMessage).GetCustomAttribute<CustomMessageAttribute>();
        if (attribute != null)
        {
            return attribute.MessageName;
        }

        var sdkMessage = typeof(TMessage).GetCustomAttribute<Vela.Messages.SdkMessageAttribute>(inherit: true);
        if (sdkMessage != null)
        {
            return sdkMessage.MessageName;
        }

        throw new InvalidOperationException(
            $"Cannot resolve the message for '{typeof(TMessage).Name}': it is neither an OrganizationRequest with a parameterless constructor that sets RequestName nor a type marked [CustomMessage(\"...\")] or [SdkMessage(\"...\")]. "
            + "Generate a typed request with 'vela models --messages <name>' (or '--custom-apis <name>') and use On<XxxRequest>(), or mark the type with [CustomMessage].");
    }

    private static PropertyInfo MostDerived(Type entityType, PropertyInfo property)
    {
        for (Type? type = entityType; type != null && type != property.DeclaringType; type = type.BaseType)
        {
            PropertyInfo? declared = type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (declared != null)
            {
                return declared;
            }
        }

        return property;
    }

    // A generated relationship navigation property carries [RelationshipSchemaName(...)] (not a column). Selecting
    // it as a column (FilterOn/Select/Where/WithPreImage/OrderBy) would otherwise silently fall back to the
    // property name and build a bogus column, so reject it with a clear message. Matched by attribute name so this
    // works without a type reference to the generated, per-namespace attribute.
    private static void EnsureNotRelationship(PropertyInfo property)
    {
        foreach (var attribute in property.GetCustomAttributes())
        {
            if (attribute.GetType().Name == "RelationshipSchemaNameAttribute")
            {
                throw new ArgumentException(
                    $"'{property.Name}' is a relationship, not a column. Load related records with a relationship-aware read or deep insert; only columns can be used in FilterOn/Select/Where/WithPreImage/OrderBy.",
                    nameof(property));
            }
        }
    }
}
