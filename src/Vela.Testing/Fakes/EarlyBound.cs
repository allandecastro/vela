using System;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.Testing
{
    /// <summary>
    /// Reads Dataverse logical names off early-bound types and member expressions, so typed overloads can map
    /// <c>ac =&gt; ac.AccountId</c> to <c>"accountid"</c> and a generated class to its table name. Mirrors the
    /// conventions of the framework's typed query builder.
    /// </summary>
    internal static class EarlyBound
    {
        /// <summary>The table logical name from <see cref="EntityLogicalNameAttribute"/> (falls back to the type name).</summary>
        public static string EntityLogicalName<TEntity>()
            where TEntity : Entity
        {
            EntityLogicalNameAttribute? attribute = typeof(TEntity).GetCustomAttribute<EntityLogicalNameAttribute>();
            return attribute?.LogicalName ?? typeof(TEntity).Name.ToLowerInvariant();
        }

        /// <summary>The column logical name from a property access (via <see cref="AttributeLogicalNameAttribute"/>, else the property name).</summary>
        public static string AttributeLogicalName<TEntity>(Expression<Func<TEntity, object?>> expression)
            where TEntity : Entity
        {
            MemberExpression? member = expression.Body as MemberExpression
                ?? (expression.Body as UnaryExpression)?.Operand as MemberExpression;

            if (member?.Member is PropertyInfo property)
            {
                AttributeLogicalNameAttribute? attribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
                return attribute?.LogicalName ?? property.Name.ToLowerInvariant();
            }

            throw new ArgumentException("Expression must be a property access, e.g. ac => ac.AccountId.", nameof(expression));
        }
    }
}
