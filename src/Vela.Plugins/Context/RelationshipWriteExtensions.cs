using System;
using System.Collections;
using Microsoft.Xrm.Sdk;
using Vela.Steps;

namespace Vela.Context
{
    /// <summary>
    /// Typed relationship write helpers. A generated early-bound record can carry related records in its navigation
    /// collections: 1:N children are created with the parent in one transaction (<see cref="CreateWithRelated"/>),
    /// and N:N members (existing records) are associated (<see cref="AssociateRelated"/>) - both without ever
    /// hand-writing a <c>Relationship</c> schema string.
    /// </summary>
    public static class RelationshipWriteExtensions
    {
        /// <summary>
        /// Creates <paramref name="entity"/> together with the related records held in its generated 1:N
        /// navigation collections, in a single transactional deep insert. Populated collections are projected into
        /// <see cref="Entity.RelatedEntities"/> first (a relationship already set there is left as-is); an entity
        /// with no related records behaves exactly like a plain <see cref="IOrganizationService.Create"/>. N:N
        /// collections are NOT created here (they associate existing records - see <see cref="AssociateRelated"/>).
        /// </summary>
        /// <returns>The id of the created root record.</returns>
        public static Guid CreateWithRelated(this IOrganizationService service, Entity entity)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            RelationshipProjector.PopulateRelatedEntities(entity);
            return service.Create(entity);
        }

        /// <summary>
        /// Associates <paramref name="entity"/> with the existing records held in its generated N:N navigation
        /// collections (an N:N link connects records that already exist, so each related record must have an id).
        /// <paramref name="entity"/> must have an id. Empty collections and non-N:N navs are ignored.
        /// </summary>
        public static void AssociateRelated(this IOrganizationService service, Entity entity)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (entity.Id == Guid.Empty)
            {
                throw new ArgumentException("The entity must have an id to associate related records.", nameof(entity));
            }

            foreach (RelationshipNav nav in RelationshipNavMap.For(entity.GetType()))
            {
                if (!nav.IsManyToMany || nav.Property.GetValue(entity) is not IEnumerable value || value is string)
                {
                    continue;
                }

                EntityReferenceCollection related = new EntityReferenceCollection();
                foreach (object? item in value)
                {
                    if (item is Entity record && record.Id != Guid.Empty)
                    {
                        related.Add(record.ToEntityReference());
                    }
                }

                if (related.Count > 0)
                {
                    service.Associate(entity.LogicalName, entity.Id, new Relationship(nav.SchemaName), related);
                }
            }
        }
    }
}
