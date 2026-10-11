using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Steps;

namespace Vela.Context
{
    /// <summary>
    /// Fluent builder for the related records to pull alongside a root record in a single
    /// <see cref="RelationshipReadExtensions.RetrieveWithRelated{TEntity}"/> call. Each <see cref="Include"/> adds a
    /// generated relationship navigation property (N:1 parent or 1:N children) to the retrieve.
    /// </summary>
    public sealed class RelationshipIncludeBuilder<TEntity>
        where TEntity : Entity
    {
        internal RelationshipQueryCollection Query { get; } = new RelationshipQueryCollection();

        /// <summary>
        /// Includes the records reachable through a generated relationship navigation property (for example
        /// <c>o =&gt; o.Order_Details</c> or <c>o =&gt; o.Parent_Account</c>), with all their columns. They are
        /// hydrated onto that typed property after the retrieve. Including the same relationship twice throws.
        /// </summary>
        public RelationshipIncludeBuilder<TEntity> Include(Expression<Func<TEntity, object?>> navigation) =>
            Add(navigation, columns: null);

        /// <summary>
        /// Includes the 1:N or N:N records of a generated collection navigation property (for example
        /// <c>o =&gt; o.Order_Details</c>), reading only <paramref name="columns"/> (all columns when none are
        /// given). Including the same relationship twice throws.
        /// </summary>
        public RelationshipIncludeBuilder<TEntity> Include<TRelated>(
            Expression<Func<TEntity, IEnumerable<TRelated>?>> navigation,
            params Expression<Func<TRelated, object?>>[] columns)
            where TRelated : Entity =>
            Add(navigation, columns);

        /// <summary>
        /// Includes the N:1 parent of a generated navigation property (for example <c>o =&gt; o.Parent_Account</c>),
        /// reading only <paramref name="columns"/> (all columns when none are given). Including the same
        /// relationship twice throws.
        /// </summary>
        public RelationshipIncludeBuilder<TEntity> Include<TRelated>(
            Expression<Func<TEntity, TRelated?>> navigation,
            params Expression<Func<TRelated, object?>>[] columns)
            where TRelated : Entity =>
            Add(navigation, columns);

        private RelationshipIncludeBuilder<TEntity> Add(LambdaExpression navigation, LambdaExpression[]? columns)
        {
            if (navigation == null)
            {
                throw new ArgumentNullException(nameof(navigation));
            }

            PropertyInfo property = ResolveProperty(navigation);
            RelationshipNav nav = RelationshipNavMap.For(typeof(TEntity)).FirstOrDefault(n => n.Property == property);
            if (nav.Property == null)
            {
                throw new ArgumentException(
                    $"'{property.Name}' is not a generated relationship navigation property (it needs [RelationshipSchemaName]).",
                    nameof(navigation));
            }

            // A second Include of the same relationship would silently replace the first one's query.
            Relationship relationship = new Relationship(nav.SchemaName);
            if (Query.Contains(relationship))
            {
                throw new ArgumentException($"'{property.Name}' is already included.", nameof(navigation));
            }

            string childLogicalName = ((Entity)Activator.CreateInstance(nav.ElementType)!).LogicalName;
            ColumnSet columnSet = columns is { Length: > 0 }
                ? new ColumnSet(columns.Select(ExpressionHelper.GetAttributeLogicalName).ToArray())
                : new ColumnSet(true);
            Query[relationship] = new QueryExpression(childLogicalName) { ColumnSet = columnSet };
            return this;
        }

        private static PropertyInfo ResolveProperty(LambdaExpression navigation)
        {
            Expression body = navigation.Body;
            if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                body = unary.Operand;
            }

            if (body is MemberExpression member && member.Member is PropertyInfo property)
            {
                return property;
            }

            throw new ArgumentException("Include expects a navigation property, e.g. o => o.Order_Details.", nameof(navigation));
        }
    }

    /// <summary>
    /// Relationship-aware read. Retrieves a single record together with related records in one round trip, hydrating
    /// the generated relationship navigation properties. This is a single-record read only: Dataverse nests related
    /// records on <see cref="RetrieveRequest"/> (not <c>RetrieveMultiple</c>), one level deep, and each related query
    /// is subject to the 5000-row page cap.
    /// </summary>
    public static class RelationshipReadExtensions
    {
        /// <summary>
        /// Retrieves the record <paramref name="target"/> points at (by id or alternate key) and, in the same call,
        /// the related records selected by <paramref name="includes"/>, early-bound as <typeparamref name="TEntity"/>
        /// with its relationship navigation properties hydrated. Pass root columns, or none for all columns.
        /// </summary>
        public static TEntity RetrieveWithRelated<TEntity>(
            this IOrganizationService service,
            EntityReference target,
            Action<RelationshipIncludeBuilder<TEntity>> includes,
            params Expression<Func<TEntity, object?>>[] columns)
            where TEntity : Entity
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (includes == null)
            {
                throw new ArgumentNullException(nameof(includes));
            }

            RelationshipIncludeBuilder<TEntity> builder = new RelationshipIncludeBuilder<TEntity>();
            includes(builder);

            ColumnSet columnSet = columns is { Length: > 0 }
                ? new ColumnSet(columns.Select(ExpressionHelper.GetAttributeLogicalName).ToArray())
                : new ColumnSet(true);

            RetrieveResponse response = (RetrieveResponse)service.Execute(new RetrieveRequest
            {
                Target = target,
                ColumnSet = columnSet,
                RelatedEntitiesQuery = builder.Query,
            });

            TEntity root = response.Entity.ToEntity<TEntity>();
            RelationshipHydrator.Hydrate(response.Entity, root);
            return root;
        }
    }
}
