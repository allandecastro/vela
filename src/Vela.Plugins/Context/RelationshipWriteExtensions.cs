using System;
using Microsoft.Xrm.Sdk;
using Vela.Steps;

namespace Vela.Context
{
    /// <summary>
    /// Typed deep-insert helpers. A generated early-bound record can carry related records in its 1:N navigation
    /// collections; these move those collections into <see cref="Entity.RelatedEntities"/> and create the whole
    /// graph in one transaction, so you never hand-write a <c>Relationship</c> schema string.
    /// </summary>
    public static class RelationshipWriteExtensions
    {
        /// <summary>
        /// Creates <paramref name="entity"/> together with the related records held in its generated 1:N
        /// navigation collections, in a single transactional deep insert. Populated collections are projected into
        /// <see cref="Entity.RelatedEntities"/> first (a relationship already set there is left as-is); an entity
        /// with no related records behaves exactly like a plain <see cref="IOrganizationService.Create"/>.
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
    }
}
