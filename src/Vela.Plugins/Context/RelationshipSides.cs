using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Steps;

namespace Vela.Context
{
    /// <summary>
    /// The two ends of an Associate / Disassociate, split by the plugin's table instead of by the caller's direction.
    /// The same link can arrive with <c>Target</c> on either end (associating contacts from the account, or the account
    /// from a contact); <see cref="ThisSide"/> and <see cref="OtherSide"/> are the same in both cases. Get it with
    /// <see cref="RelationshipSidesExtensions"/> (<c>ctx.Sides()</c>).
    /// </summary>
    /// <remarks>
    /// The split, for a plugin on table <c>T</c>: when <c>Target</c> is a <c>T</c>, <see cref="ThisSide"/> is
    /// <c>Target</c> and <see cref="OtherSide"/> is every <c>RelatedEntities</c> entry. Otherwise
    /// <see cref="ThisSide"/> is the <c>RelatedEntities</c> entries that are a <c>T</c>, and <see cref="OtherSide"/>
    /// is <c>Target</c> followed by the remaining entries. Every reference lands on exactly one side. On a
    /// self-referential relationship (both ends are <c>T</c>), <c>Target</c> is this side. On a relationship that
    /// does not involve <c>T</c> (a step without <c>ForRelationship</c>), <see cref="ThisSide"/> is empty.
    /// </remarks>
    public sealed class RelationshipSides
    {
        internal RelationshipSides(Relationship? relationship, IReadOnlyList<EntityReference> thisSide, IReadOnlyList<EntityReference> otherSide)
        {
            Relationship = relationship;
            ThisSide = thisSide;
            OtherSide = otherSide;
        }

        /// <summary>The relationship of the request (<c>ctx.Request.Relationship</c>).</summary>
        public Relationship? Relationship { get; }

        /// <summary>The records of the plugin's table, whichever end the caller passed them on.</summary>
        public IReadOnlyList<EntityReference> ThisSide { get; }

        /// <summary>The records at the other end of the link.</summary>
        public IReadOnlyList<EntityReference> OtherSide { get; }

        internal static RelationshipSides Split<TEntity>(EntityReference? target, Relationship? relationship, EntityReferenceCollection? relatedEntities)
            where TEntity : Entity
        {
            string table = ExpressionHelper.GetEntityLogicalName<TEntity>();
            List<EntityReference> thisSide = new List<EntityReference>();
            List<EntityReference> otherSide = new List<EntityReference>();

            if (target != null && IsTable(target, table))
            {
                thisSide.Add(target);
                AddAll(otherSide, relatedEntities);
                return new RelationshipSides(relationship, thisSide, otherSide);
            }

            if (target != null)
            {
                otherSide.Add(target);
            }

            if (relatedEntities != null)
            {
                foreach (EntityReference related in relatedEntities)
                {
                    if (related != null)
                    {
                        (IsTable(related, table) ? thisSide : otherSide).Add(related);
                    }
                }
            }

            return new RelationshipSides(relationship, thisSide, otherSide);
        }

        private static bool IsTable(EntityReference reference, string table) =>
            string.Equals(reference.LogicalName, table, StringComparison.OrdinalIgnoreCase);

        private static void AddAll(List<EntityReference> side, EntityReferenceCollection? references)
        {
            if (references != null)
            {
                foreach (EntityReference reference in references)
                {
                    if (reference != null)
                    {
                        side.Add(reference);
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>ctx.Sides()</c> on the Associate / Disassociate contexts: the link's ends split by the plugin's table (see
    /// <see cref="RelationshipSides"/>). The plugin's table does not filter these steps; the handler runs whichever
    /// end the caller passed as <c>Target</c>.
    /// </summary>
    public static class RelationshipSidesExtensions
    {
        /// <summary>Splits the link by the plugin's table (see <see cref="RelationshipSides"/>).</summary>
        public static RelationshipSides Sides<TEntity>(this IAssociatePreContext<TEntity> context) where TEntity : Entity =>
            Split<TEntity>(Request(context));

        /// <summary>Splits the link by the plugin's table (see <see cref="RelationshipSides"/>).</summary>
        public static RelationshipSides Sides<TEntity>(this IAssociatePostContext<TEntity> context) where TEntity : Entity =>
            Split<TEntity>(Request(context));

        /// <summary>Splits the link by the plugin's table (see <see cref="RelationshipSides"/>).</summary>
        public static RelationshipSides Sides<TEntity>(this IDisassociatePreContext<TEntity> context) where TEntity : Entity =>
            Split<TEntity>(Request(context));

        /// <summary>Splits the link by the plugin's table (see <see cref="RelationshipSides"/>).</summary>
        public static RelationshipSides Sides<TEntity>(this IDisassociatePostContext<TEntity> context) where TEntity : Entity =>
            Split<TEntity>(Request(context));

        private static TRequest Request<TRequest>(IStepRequestContext<TRequest> context)
            where TRequest : OrganizationRequest
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.Request;
        }

        private static RelationshipSides Split<TEntity>(AssociateRequest request) where TEntity : Entity =>
            RelationshipSides.Split<TEntity>(request.Target, request.Relationship, request.RelatedEntities);

        private static RelationshipSides Split<TEntity>(DisassociateRequest request) where TEntity : Entity =>
            RelationshipSides.Split<TEntity>(request.Target, request.Relationship, request.RelatedEntities);
    }
}
