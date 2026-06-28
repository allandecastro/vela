using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// A configurable <see cref="IOrganizationService"/> test double with no mocking-library dependency.
    /// Every operation is a no-op returning SDK defaults unless the matching delegate hook is set.
    /// Consumers can ignore this type and inject their own <see cref="IOrganizationService"/> via the builder, or
    /// use <see cref="InMemoryOrganizationService"/> for a stateful store.
    /// </summary>
    public sealed class HookOrganizationService : IOrganizationService
    {
        public Func<Entity, Guid>? OnCreate { get; set; }
        public Func<string, Guid, ColumnSet, Entity>? OnRetrieve { get; set; }
        public Action<Entity>? OnUpdate { get; set; }
        public Action<string, Guid>? OnDelete { get; set; }
        public Func<OrganizationRequest, OrganizationResponse>? OnExecute { get; set; }
        public Action<string, Guid, Relationship, EntityReferenceCollection>? OnAssociate { get; set; }
        public Action<string, Guid, Relationship, EntityReferenceCollection>? OnDisassociate { get; set; }
        public Func<QueryBase, EntityCollection>? OnRetrieveMultiple { get; set; }

        public Guid Create(Entity entity)
        {
            return OnCreate != null ? OnCreate(entity) : Guid.Empty;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            return OnRetrieve != null ? OnRetrieve(entityName, id, columnSet) : null!;
        }

        public void Update(Entity entity)
        {
            OnUpdate?.Invoke(entity);
        }

        public void Delete(string entityName, Guid id)
        {
            OnDelete?.Invoke(entityName, id);
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            return OnExecute != null ? OnExecute(request) : null!;
        }

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            OnAssociate?.Invoke(entityName, entityId, relationship, relatedEntities);
        }

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            OnDisassociate?.Invoke(entityName, entityId, relationship, relatedEntities);
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            return OnRetrieveMultiple != null ? OnRetrieveMultiple(query) : new EntityCollection();
        }
    }
}
